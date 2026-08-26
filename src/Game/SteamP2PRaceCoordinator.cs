using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;
using Sts2SpireRace.Core;
using Sts2SpireRace.UI;

namespace Sts2SpireRace.Game;

public sealed class SteamP2PRaceCoordinator : IRaceEntertainmentP2PService, IDisposable
{
    private const string MarkerKey = "spire_race";
    private const string MarkerValue = "competitive_p2p_v1";
    private const string SessionKey = "spire_race_session";
    private const string HostKey = "spire_race_host";
    private const string RulesKey = "spire_race_rules";
    private const string StateKey = "spire_race_state";
    private const string StartedAtKey = "spire_race_started_at";
    private const string RosterKey = "spire_race_roster";
    private const string SharedCharacterKey = "spire_race_character";
    private const string FirstHostKey = "spire_race_team1_host";
    private const string SecondHostKey = "spire_race_team2_host";
    private const string FirstLobbyKey = "spire_race_team1_lobby";
    private const string SecondLobbyKey = "spire_race_team2_lobby";
    private const string SettlementKey = "spire_race_settlement";
    private const string MemberNameKey = "spire_race_name";
    private const string MemberTeamKey = "spire_race_team";
    private const string MemberReadyKey = "spire_race_ready";
    private const string MemberCharacterKey = "spire_race_character";
    private const string MemberGameLobbyKey = "spire_race_game_lobby";
    private const string MemberProgressKey = "spire_race_progress";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Characters = ["Ironclad", "Silent", "Defect", "Necrobinder", "Regent"];

    private readonly RaceSessionLauncher _launcher;
    private readonly Callback<LobbyDataUpdate_t> _dataCallback;
    private readonly Callback<LobbyChatUpdate_t> _chatCallback;
    private CSteamID? _lobby;
    private bool _teamLobbyPreparing;
    private bool _launching;
    private string _appliedSettlement = string.Empty;
    private ProgressCheckpoint? _localProgress;

    public SteamP2PRaceCoordinator(RaceSessionLauncher launcher)
    {
        _launcher = launcher;
        _dataCallback = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
        _chatCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
    }

    public EntertainmentRoom? CurrentRoom { get; private set; }
    public MatchAssignment? CurrentMatch { get; private set; }
    public SettlementSnapshot? CurrentSettlement { get; private set; }
    public LegendDraftPrompt? CurrentLegendDraft => null;

    public event Action<EntertainmentRoom?>? RoomChanged;
    public event Action<string>? RoomExited;
    public event Action<MatchAssignment?>? MatchChanged;
    public event Action<SettlementSnapshot>? MatchSettled;
    public event Action<LegendDraftPrompt?>? LegendDraftChanged { add { } remove { } }

    public static bool IsRaceLobby(ulong lobbyId) =>
        string.Equals(SteamMatchmaking.GetLobbyData(new CSteamID(lobbyId), MarkerKey), MarkerValue, StringComparison.Ordinal);

    public async Task<EntertainmentRoom> CreateRoomAsync(RaceRuleSet rules, CancellationToken cancellationToken = default)
    {
        RequireSteam();
        RaceRules.Validate(rules);
        await LeaveRoomAsync(cancellationToken);
        rules = rules with { CoordinationMode = "p2p" };
        using var result = new SteamCallResult<LobbyCreated_t>(
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Math.Clamp((int)rules.TeamSize * 2, 2, 8)), cancellationToken);
        var created = await result.Task;
        if (created.m_eResult != EResult.k_EResultOK)
            throw new InvalidOperationException($"Steam race lobby creation failed: {created.m_eResult}");
        _lobby = new CSteamID(created.m_ulSteamIDLobby);
        var localId = SteamUser.GetSteamID().m_SteamID.ToString();
        SetLobby(MarkerKey, MarkerValue);
        SetLobby(SessionKey, $"p2p-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{System.Security.Cryptography.RandomNumberGenerator.GetHexString(4)}");
        SetLobby(HostKey, localId);
        SetLobby(RulesKey, JsonSerializer.Serialize(rules, Json));
        SetLobby(StateKey, "waiting");
        SetLobby(SettlementKey, string.Empty);
        SetLocalMemberDefaults(1);
        RefreshFromSteam();
        Log.Info($"[SpireRace] Created competitive Steam P2P coordination lobby {_lobby.Value.m_SteamID}.");
        return CurrentRoom!;
    }

    public async Task<bool> JoinInvitedLobbyAsync(ulong lobbyId, CancellationToken cancellationToken = default)
    {
        RequireSteam();
        await LeaveRoomAsync(cancellationToken);
        using var result = new SteamCallResult<LobbyEnter_t>(SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)), cancellationToken);
        var entered = await result.Task;
        if (entered.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            throw new InvalidOperationException($"Steam race lobby join failed: {(EChatRoomEnterResponse)entered.m_EChatRoomEnterResponse}");
        _lobby = new CSteamID(lobbyId);
        if (!IsRaceLobby(lobbyId))
        {
            SteamMatchmaking.LeaveLobby(_lobby.Value);
            _lobby = null;
            return false;
        }
        SetLocalMemberDefaults(ChooseBalancedTeam());
        RefreshFromSteam();
        Log.Info($"[SpireRace] Joined competitive Steam P2P coordination lobby {lobbyId}.");
        return true;
    }

    public Task<EntertainmentRoom> JoinRoomAsync(string code, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(RaceTextCatalog.Get("fun.p2p_invite_only"));

    public Task OpenSteamInviteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lobby is null)
            throw new InvalidOperationException("No Steam race lobby is active.");
        if (!SteamUtils.IsOverlayEnabled())
            throw new InvalidOperationException(RaceTextCatalog.Get("fun.steam_overlay_required"));
        SteamFriends.ActivateGameOverlayInviteDialog(_lobby.Value);
        return Task.CompletedTask;
    }

    public Task InviteFriendAsync(string playerId, CancellationToken cancellationToken = default) => OpenSteamInviteAsync(cancellationToken);

    public Task<EntertainmentRoom> UpdateRoomRulesAsync(RaceRuleSet rules, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireHost();
        rules = rules with { CoordinationMode = "p2p" };
        RaceRules.Validate(rules);
        SetLobby(RulesKey, JsonSerializer.Serialize(rules, Json));
        RefreshFromSteam();
        return Task.FromResult(CurrentRoom!);
    }

    public Task<EntertainmentRoom> SwitchTeamAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireWaitingRoom();
        var local = LocalMember();
        var target = local.Team == 1 ? 2 : 1;
        if (CurrentRoom!.Members.Count(x => x.Team == target) >= (int)CurrentRoom.Rules.TeamSize)
            throw new InvalidOperationException(RaceTextCatalog.Get("fun.team_full"));
        SteamMatchmaking.SetLobbyMemberData(_lobby!.Value, MemberTeamKey, target.ToString());
        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberReadyKey, "0");
        RefreshFromSteam();
        return Task.FromResult(CurrentRoom!);
    }

    public Task<EntertainmentRoom> SetRoomMemberAsync(string characterId, bool ready, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireWaitingRoom();
        if (!Characters.Contains(characterId, StringComparer.Ordinal))
            characterId = "Ironclad";
        SteamMatchmaking.SetLobbyMemberData(_lobby!.Value, MemberCharacterKey, characterId);
        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberReadyKey, ready ? "1" : "0");
        RefreshFromSteam();
        return Task.FromResult(CurrentRoom!);
    }

    public Task<EntertainmentRoom> StartRoomAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireHost();
        RequireWaitingRoom();
        var room = CurrentRoom!;
        var required = (int)room.Rules.TeamSize;
        if (room.Members.Count != required * 2 || room.Members.Any(x => !x.IsReady) ||
            room.Members.Count(x => x.Team == 1) != required || room.Members.Count(x => x.Team == 2) != required)
            throw new InvalidOperationException(RaceTextCatalog.Get("fun.everyone_ready_required"));

        var rules = room.Rules;
        if (rules.RandomSeed)
            rules = rules with { Seed = System.Security.Cryptography.RandomNumberGenerator.GetHexString(8), RandomSeed = false };
        var roster = room.Members.OrderBy(x => x.Team).ThenBy(x => x.PlayerId, StringComparer.Ordinal).ToArray();
        var firstHost = roster.First(x => x.Team == 1).PlayerId;
        var secondHost = roster.First(x => x.Team == 2).PlayerId;
        var sharedCharacter = rules.TeamSize == TeamSize.One ? roster.First(x => x.Team == 1).CharacterId : string.Empty;
        SetLobby(RulesKey, JsonSerializer.Serialize(rules, Json));
        SetLobby(RosterKey, JsonSerializer.Serialize(roster, Json));
        SetLobby(FirstHostKey, firstHost);
        SetLobby(SecondHostKey, secondHost);
        SetLobby(SharedCharacterKey, sharedCharacter);
        SetLobby(StartedAtKey, DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds().ToString());
        SetLobby(StateKey, rules.TeamSize == TeamSize.One ? "launching" : "preparing_team_lobbies");
        RefreshFromSteam();
        return Task.FromResult(CurrentRoom!);
    }

    public async Task LeaveRoomAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lobby is not null)
        {
            var state = GetLobby(StateKey);
            if (state is "launching" or "preparing_team_lobbies" or "running")
            {
                if (CurrentMatch is not null)
                {
                    var localTeam = CurrentMatch.LocalTeam.Id.EndsWith("team2", StringComparison.Ordinal) ? 2 : 1;
                    if (CanAdjudicate())
                        SetForcedSettlement(localTeam, FinishReason.Disconnect);
                    else
                    {
                        var checkpoint = (_localProgress ?? new ProgressCheckpoint(CurrentMatch.MatchId, CurrentMatch.GameId,
                            CurrentMatch.LocalTeam.Id, 0, 0, 0, false, null, ParticipantOutcome.Active, 0, 0, 0)) with
                        {
                            Sequence = (_localProgress?.Sequence ?? 0) + 1,
                            Outcome = ParticipantOutcome.Forfeited
                        };
                        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberProgressKey, JsonSerializer.Serialize(checkpoint, Json));
                        await Task.Delay(250, cancellationToken);
                    }
                }
            }
            else if (IsLocalHost())
                SteamMatchmaking.SetLobbyData(_lobby.Value, StateKey, "closed");
            SteamMatchmaking.LeaveLobby(_lobby.Value);
        }
        ClearLocal("left");
    }

    public Task ReportProgressAsync(ProgressCheckpoint checkpoint, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lobby is null || CurrentMatch is null)
            return Task.CompletedTask;
        _localProgress = checkpoint;
        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberProgressKey, JsonSerializer.Serialize(checkpoint, Json));
        RefreshFromSteam();
        return Task.CompletedTask;
    }

    public Task ChooseDeathActionAsync(bool restart, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RequestSaveAndQuitAsync(SlCategory category, bool confirmForfeit, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResumeSavedRunAsync(string idempotencyKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SurrenderAsync(CancellationToken cancellationToken = default) => VoteSurrenderAsync(true, cancellationToken);
    public Task VoteSurrenderAsync(bool accept, CancellationToken cancellationToken = default)
    {
        if (!accept || CurrentMatch is null)
            return Task.CompletedTask;
        var checkpoint = (_localProgress ?? new ProgressCheckpoint(CurrentMatch.MatchId, CurrentMatch.GameId,
            CurrentMatch.LocalTeam.Id, 1, 0, 0, false, null, ParticipantOutcome.Active, 0, 0, 0)) with
        {
            Sequence = (_localProgress?.Sequence ?? 0) + 1,
            Outcome = ParticipantOutcome.Surrendered
        };
        return ReportProgressAsync(checkpoint, $"p2p-surrender:{Guid.NewGuid():N}", cancellationToken);
    }
    public Task SubmitLegendBansAsync(string banOne, string banTwo, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SelectLegendCharacterAsync(string characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private void OnLobbyDataUpdate(LobbyDataUpdate_t update)
    {
        if (_lobby?.m_SteamID != update.m_ulSteamIDLobby)
            return;
        RefreshFromSteam();
    }

    private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
    {
        if (_lobby?.m_SteamID != update.m_ulSteamIDLobby)
            return;
        RefreshFromSteam();
    }

    private void RefreshFromSteam()
    {
        if (_lobby is null)
            return;
        var state = GetLobby(StateKey);
        if (state == "closed")
        {
            SteamMatchmaking.LeaveLobby(_lobby.Value);
            ClearLocal("host_closed");
            return;
        }
        if (!HostStillPresent())
        {
            var roster = ReadRoster();
            var present = ReadMembers().Select(x => x.PlayerId).ToHashSet(StringComparer.Ordinal);
            var missingHost = roster.FirstOrDefault(x => x.PlayerId == GetLobby(HostKey) && !present.Contains(x.PlayerId));
            if (missingHost is not null && state is not "waiting" && CanAdjudicate())
            {
                SetForcedSettlement(missingHost.Team, FinishReason.Disconnect);
                state = GetLobby(StateKey);
            }
            else
            {
                SteamMatchmaking.LeaveLobby(_lobby.Value);
                ClearLocal("host_closed");
                return;
            }
        }
        var currentMembers = ReadMembers();
        if (state is not "waiting" and not "completed")
        {
            var present = currentMembers.Select(x => x.PlayerId).ToHashSet(StringComparer.Ordinal);
            var missing = ReadRoster().FirstOrDefault(x => !present.Contains(x.PlayerId));
            if (missing is not null && CanAdjudicate())
            {
                SetForcedSettlement(missing.Team, FinishReason.Disconnect);
                state = GetLobby(StateKey);
            }
        }
        var rules = ReadRules();
        CurrentRoom = new EntertainmentRoom(RoomCode(_lobby.Value.m_SteamID), GetLobby(HostKey), rules, currentMembers,
            DateTimeOffset.UtcNow, EntertainmentCoordinationMode.SteamP2P, state);
        RoomChanged?.Invoke(CurrentRoom);

        if (state == "preparing_team_lobbies")
        {
            _ = PrepareLocalTeamLobbyAsync();
            // The coordination host may not be one of the two team-lobby hosts.
            // Re-check whenever Steam publishes member data so both prepared lobby
            // identifiers are promoted into shared lobby data as soon as available.
            if (IsLocalHost())
                TryLaunchPreparedTeams();
        }
        // Steam may coalesce lobby-data updates. A guest can observe "running"
        // without ever observing the short-lived "launching" value.
        if (state is "launching" or "running" && CurrentMatch is null)
            _ = LaunchLocalRaceAsync();
        var settlement = GetLobby(SettlementKey);
        if (!string.IsNullOrWhiteSpace(settlement) && settlement != _appliedSettlement)
            ApplySettlement(settlement);
        if (CanAdjudicate() && state is "running" or "launching")
            TrySettleFromProgress();
    }

    private async Task PrepareLocalTeamLobbyAsync()
    {
        if (_teamLobbyPreparing || _lobby is null)
            return;
        var localId = SteamUser.GetSteamID().m_SteamID.ToString();
        if (localId != GetLobby(FirstHostKey) && localId != GetLobby(SecondHostKey))
            return;
        _teamLobbyPreparing = true;
        try
        {
            var assignment = BuildAssignment();
            var lobbyId = await _launcher.CreateTeamLobbyAsync(assignment);
            SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberGameLobbyKey, lobbyId.ToString());
            if (IsLocalHost())
                TryLaunchPreparedTeams();
        }
        catch (Exception exception)
        {
            Log.Error($"[SpireRace] Failed to prepare a P2P team lobby: {exception}");
            _teamLobbyPreparing = false;
        }
    }

    private void TryLaunchPreparedTeams()
    {
        var roster = ReadRoster();
        var firstHost = GetLobby(FirstHostKey);
        var secondHost = GetLobby(SecondHostKey);
        var firstLobby = MemberData(firstHost, MemberGameLobbyKey);
        var secondLobby = MemberData(secondHost, MemberGameLobbyKey);
        if (string.IsNullOrWhiteSpace(firstLobby) || string.IsNullOrWhiteSpace(secondLobby))
            return;
        if (!roster.Any(x => x.PlayerId == firstHost) || !roster.Any(x => x.PlayerId == secondHost))
            return;
        SetLobby(FirstLobbyKey, firstLobby);
        SetLobby(SecondLobbyKey, secondLobby);
        SetLobby(StateKey, "launching");
        RefreshFromSteam();
    }

    private async Task LaunchLocalRaceAsync()
    {
        if (_launching)
            return;
        if (CurrentRoom!.Rules.TeamSize != TeamSize.One &&
            (string.IsNullOrWhiteSpace(GetLobby(FirstLobbyKey)) || string.IsNullOrWhiteSpace(GetLobby(SecondLobbyKey))))
            return;
        _launching = true;
        try
        {
            CurrentMatch = BuildAssignment();
            MatchChanged?.Invoke(CurrentMatch);
            RaceActiveSession.Begin(CurrentMatch);
            if (IsLocalHost())
                SetLobby(StateKey, "running");
            var startAt = CurrentMatch.StartedAtUnixMilliseconds;
            var delay = startAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (delay > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(delay));
            await _launcher.LaunchAsync(CurrentMatch);
        }
        catch (Exception exception)
        {
            _launching = false;
            Log.Error($"[SpireRace] Failed to launch a competitive Steam P2P race: {exception}");
            if (_lobby is { } lobby)
                SteamMatchmaking.LeaveLobby(lobby);
            ClearLocal("launch_failed");
        }
    }

    private MatchAssignment BuildAssignment()
    {
        var rules = ReadRules();
        var roster = ReadRoster();
        var localId = SteamUser.GetSteamID().m_SteamID.ToString();
        var localTeamNumber = roster.First(x => x.PlayerId == localId).Team;
        RaceTeam Team(int number, bool local) => new($"{GetLobby(SessionKey)}-team{number}", $"Team {number}",
            roster.Where(x => x.Team == number).Select(x => new RaceParticipant(x.PlayerId, x.DisplayName, x.CharacterId,
                x.PlayerId == localId, x.IsReady)).ToArray());
        var first = Team(1, localTeamNumber == 1);
        var second = Team(2, localTeamNumber == 2);
        var localTeam = localTeamNumber == 1 ? first : second;
        var opponent = localTeamNumber == 1 ? second : first;
        var characters = roster.ToDictionary(x => x.PlayerId, x => x.CharacterId, StringComparer.Ordinal);
        return new MatchAssignment(GetLobby(SessionKey), GetLobby(SessionKey), RaceRuntimeInfo.GameVersion,
            QueueKind.Entertainment, rules.TeamSize, rules, localTeam, opponent, GetLobby(SharedCharacterKey),
            GetLobby(SessionKey), ParseLong(GetLobby(StartedAtKey)), null, characters, GetLobby(FirstHostKey),
            GetLobby(SecondHostKey), GetLobby(FirstLobbyKey), GetLobby(SecondLobbyKey));
    }

    private void TrySettleFromProgress()
    {
        if (!string.IsNullOrWhiteSpace(GetLobby(SettlementKey)))
            return;
        var roster = ReadRoster();
        var first = TeamProgress(1, roster);
        var second = TeamProgress(2, roster);
        if (first is { Outcome: ParticipantOutcome.Surrendered or ParticipantOutcome.Forfeited })
        {
            SetForcedSettlement(1, first.Outcome == ParticipantOutcome.Surrendered ? FinishReason.Surrender : FinishReason.Disconnect);
            return;
        }
        if (second is { Outcome: ParticipantOutcome.Surrendered or ParticipantOutcome.Forfeited })
        {
            SetForcedSettlement(2, second.Outcome == ParticipantOutcome.Surrendered ? FinishReason.Surrender : FinishReason.Disconnect);
            return;
        }
        if (first is null || second is null || !Terminal(first) || !Terminal(second))
            return;
        var decision = RaceAdjudicator.Decide(first, second);
        WriteSettlement(decision.WinnerTeamId, decision.Reason, decision.AuditDetail, first, second);
    }

    private ProgressCheckpoint? TeamProgress(int team, IReadOnlyList<EntertainmentRoomMember> roster)
    {
        var values = roster.Where(x => x.Team == team).Select(x => MemberData(x.PlayerId, MemberProgressKey))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x =>
            {
                try { return JsonSerializer.Deserialize<ProgressCheckpoint>(x, Json); }
                catch { return null; }
            }).Where(x => x is not null).Cast<ProgressCheckpoint>().ToArray();
        if (values.Length == 0)
            return null;
        var forced = values.FirstOrDefault(x => x.Outcome is ParticipantOutcome.Surrendered or ParticipantOutcome.Forfeited or ParticipantOutcome.TimedOut);
        if (forced is not null)
            return forced with { TeamId = $"{GetLobby(SessionKey)}-team{team}" };
        var floor = values.Max(x => x.Floor);
        var atFloor = values.Where(x => x.Floor == floor).ToArray();
        return values[0] with
        {
            TeamId = $"{GetLobby(SessionKey)}-team{team}",
            Floor = floor,
            FloorEnteredAtMilliseconds = atFloor.Min(x => x.FloorEnteredAtMilliseconds),
            FinalBossDefeated = values.Any(x => x.FinalBossDefeated),
            CompletedAtMilliseconds = values.Where(x => x.CompletedAtMilliseconds.HasValue).Select(x => x.CompletedAtMilliseconds).Min(),
            Outcome = values.Any(x => x.Outcome == ParticipantOutcome.Finished) ? ParticipantOutcome.Finished :
                values.All(x => x.Outcome == ParticipantOutcome.ScoreLocked) ? ParticipantOutcome.ScoreLocked : ParticipantOutcome.Active,
            RestartCount = values.Max(x => x.RestartCount),
            EventSlUsed = values.Max(x => x.EventSlUsed),
            CombatSlUsed = values.Max(x => x.CombatSlUsed)
        };
    }

    private void SetForcedSettlement(int losingTeam, FinishReason reason)
    {
        if (!string.IsNullOrWhiteSpace(GetLobby(SettlementKey)))
            return;
        var session = GetLobby(SessionKey);
        var losing = new ProgressCheckpoint(session, session, $"{session}-team{losingTeam}", 1, 0, 0, false, null,
            reason == FinishReason.Surrender ? ParticipantOutcome.Surrendered : ParticipantOutcome.Forfeited, 0, 0, 0);
        var winningTeam = losingTeam == 1 ? 2 : 1;
        var winning = new ProgressCheckpoint(session, session, $"{session}-team{winningTeam}", 1, 0, 0, false, null,
            ParticipantOutcome.Active, 0, 0, 0);
        WriteSettlement(winning.TeamId, reason, "steam-p2p-forced-result", losingTeam == 1 ? losing : winning, losingTeam == 1 ? winning : losing);
    }

    private void WriteSettlement(string winner, FinishReason reason, string audit, ProgressCheckpoint first, ProgressCheckpoint second)
    {
        var wire = new SettlementWire(winner, reason,
            ToSide(first, "Team 1"), ToSide(second, "Team 2"), audit, DateTimeOffset.UtcNow);
        SetLobby(SettlementKey, JsonSerializer.Serialize(wire, Json));
        SetLobby(StateKey, "completed");
        RefreshFromSteam();
    }

    private void ApplySettlement(string encoded)
    {
        var wire = JsonSerializer.Deserialize<SettlementWire>(encoded, Json);
        if (wire is null || CurrentMatch is null)
            return;
        _appliedSettlement = encoded;
        var localFirst = wire.First.TeamId == CurrentMatch.LocalTeam.Id;
        CurrentSettlement = new SettlementSnapshot(CurrentMatch.MatchId, CurrentMatch.GameId, wire.WinnerTeamId,
            wire.Reason, localFirst ? wire.First : wire.Second, localFirst ? wire.Second : wire.First, 0, [], wire.AuditDetail, wire.CompletedAt);
        MatchSettled?.Invoke(CurrentSettlement);
    }

    private static SettlementSide ToSide(ProgressCheckpoint value, string name) => new(value.TeamId, name, value.Outcome,
        value.Floor, value.FloorEnteredAtMilliseconds, value.CompletedAtMilliseconds, value.RestartCount, value.EventSlUsed, value.CombatSlUsed);
    private static bool Terminal(ProgressCheckpoint value) => value.FinalBossDefeated ||
        value.Outcome is ParticipantOutcome.Finished or ParticipantOutcome.ScoreLocked or ParticipantOutcome.Surrendered or ParticipantOutcome.Forfeited or ParticipantOutcome.TimedOut;

    private RaceRuleSet ReadRules()
    {
        try { return JsonSerializer.Deserialize<RaceRuleSet>(GetLobby(RulesKey), Json) ?? RaceRules.EntertainmentDefault(); }
        catch { return RaceRules.EntertainmentDefault(); }
    }

    private EntertainmentRoomMember[] ReadRoster()
    {
        try { return JsonSerializer.Deserialize<EntertainmentRoomMember[]>(GetLobby(RosterKey), Json) ?? ReadMembers(); }
        catch { return ReadMembers(); }
    }

    private EntertainmentRoomMember[] ReadMembers()
    {
        if (_lobby is null)
            return [];
        var host = GetLobby(HostKey);
        var result = new List<EntertainmentRoomMember>();
        for (var index = 0; index < SteamMatchmaking.GetNumLobbyMembers(_lobby.Value); index++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(_lobby.Value, index);
            var id = member.m_SteamID.ToString();
            var name = SteamMatchmaking.GetLobbyMemberData(_lobby.Value, member, MemberNameKey);
            if (string.IsNullOrWhiteSpace(name))
                name = PlatformUtil.GetPlayerNameRaw(PlatformType.Steam, member.m_SteamID);
            var team = int.TryParse(SteamMatchmaking.GetLobbyMemberData(_lobby.Value, member, MemberTeamKey), out var parsedTeam) ? parsedTeam : 1;
            var ready = SteamMatchmaking.GetLobbyMemberData(_lobby.Value, member, MemberReadyKey) == "1";
            var character = SteamMatchmaking.GetLobbyMemberData(_lobby.Value, member, MemberCharacterKey);
            result.Add(new EntertainmentRoomMember(id, name, Math.Clamp(team, 1, 2), id == host, ready,
                Characters.Contains(character, StringComparer.Ordinal) ? character : "Ironclad"));
        }
        return result.ToArray();
    }

    private void SetLocalMemberDefaults(int team)
    {
        var id = SteamUser.GetSteamID().m_SteamID;
        SteamMatchmaking.SetLobbyMemberData(_lobby!.Value, MemberNameKey, PlatformUtil.GetPlayerNameRaw(PlatformType.Steam, id));
        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberTeamKey, team.ToString());
        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberReadyKey, "0");
        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberCharacterKey, "Ironclad");
        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberGameLobbyKey, string.Empty);
        SteamMatchmaking.SetLobbyMemberData(_lobby.Value, MemberProgressKey, string.Empty);
    }

    private int ChooseBalancedTeam()
    {
        var members = ReadMembers();
        return members.Count(x => x.Team == 1) <= members.Count(x => x.Team == 2) ? 1 : 2;
    }

    private EntertainmentRoomMember LocalMember() => CurrentRoom?.Members.FirstOrDefault(x => x.PlayerId == SteamUser.GetSteamID().m_SteamID.ToString())
        ?? throw new InvalidOperationException("The local player is not in the Steam race lobby.");
    private bool IsLocalHost() => _lobby is not null && GetLobby(HostKey) == SteamUser.GetSteamID().m_SteamID.ToString();
    private bool CanAdjudicate() => _lobby is not null &&
        (IsLocalHost() || SteamMatchmaking.GetLobbyOwner(_lobby.Value) == SteamUser.GetSteamID());
    private bool HostStillPresent() => _lobby is not null && ReadMembers().Any(x => x.PlayerId == GetLobby(HostKey));
    private void RequireHost() { if (!IsLocalHost()) throw new InvalidOperationException(RaceTextCatalog.Get("fun.host_only")); }
    private void RequireWaitingRoom() { if (CurrentRoom is null || CurrentRoom.State != "waiting") throw new InvalidOperationException("The Steam race room is no longer waiting."); }
    private static void RequireSteam() { if (!SteamInitializer.Initialized || !SteamAPI.IsSteamRunning()) throw new InvalidOperationException(RaceTextCatalog.Get("auth.steam_required")); }
    private string GetLobby(string key) => _lobby is null ? string.Empty : SteamMatchmaking.GetLobbyData(_lobby.Value, key);
    private void SetLobby(string key, string value) { if (_lobby is null || !SteamMatchmaking.SetLobbyData(_lobby.Value, key, value)) throw new InvalidOperationException($"Steam failed to update race lobby field {key}."); }
    private string MemberData(string playerId, string key) => _lobby is null || !ulong.TryParse(playerId, out var id) ? string.Empty : SteamMatchmaking.GetLobbyMemberData(_lobby.Value, new CSteamID(id), key);
    private static string RoomCode(ulong lobbyId) { var value = lobbyId.ToString(); return value.Length <= 6 ? value : value[^6..]; }
    private static long ParseLong(string value) => long.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void ClearLocal(string reason)
    {
        _lobby = null;
        _teamLobbyPreparing = false;
        _launching = false;
        _appliedSettlement = string.Empty;
        _localProgress = null;
        CurrentRoom = null;
        CurrentMatch = null;
        CurrentSettlement = null;
        RoomChanged?.Invoke(null);
        MatchChanged?.Invoke(null);
        RoomExited?.Invoke(reason);
    }

    public void Dispose()
    {
        if (_lobby is not null)
            SteamMatchmaking.LeaveLobby(_lobby.Value);
        _dataCallback.Dispose();
        _chatCallback.Dispose();
    }

    private sealed record SettlementWire(string WinnerTeamId, FinishReason Reason, SettlementSide First,
        SettlementSide Second, string AuditDetail, DateTimeOffset CompletedAt);
}

[HarmonyPatch(typeof(SteamJoinCallbackHandler), "OnSteamLobbyJoinRequested")]
internal static class SteamP2PRaceInvitePatch
{
    [HarmonyPrefix]
    private static bool Prefix(GameLobbyJoinRequested_t lobbyJoinRequest)
    {
        var lobbyId = lobbyJoinRequest.m_steamIDLobby.m_SteamID;
        TaskHelper.RunSafely(SteamP2PRaceInviteRouter.RouteAsync(lobbyId, lobbyJoinRequest.m_steamIDFriend.m_SteamID));
        return false;
    }
}

[HarmonyPatch(typeof(SteamJoinCallbackHandler), nameof(SteamJoinCallbackHandler.CheckForCommandLineJoin))]
internal static class SteamP2PRaceCommandLineInvitePatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (!CommandLineHelper.TryGetValue("+connect_lobby", out var value) || !ulong.TryParse(value, out var lobbyId))
            return true;
        TaskHelper.RunSafely(SteamP2PRaceInviteRouter.RouteAsync(lobbyId, null));
        return false;
    }
}

internal static class SteamP2PRaceInviteRouter
{
    public static async Task RouteAsync(ulong lobbyId, ulong? friendId)
    {
        if (!SteamP2PRaceCoordinator.IsRaceLobby(lobbyId))
            await RequestLobbyDataAsync(lobbyId);
        if (SteamP2PRaceCoordinator.IsRaceLobby(lobbyId) &&
            RaceServiceRegistry.Services.SessionLauncher is RaceSessionLauncher launcher)
        {
            if (await launcher.P2P.JoinInvitedLobbyAsync(lobbyId))
            {
                var controller = NGame.Instance?.MainMenu?.GetNodeOrNull<RaceUiController>("SpireRaceController");
                if (controller is not null)
                    Callable.From(() => controller.NotifySteamInviteJoined(RoomCode(lobbyId))).CallDeferred();
            }
            return;
        }

        // Preserve the original game's invite behavior for ordinary cooperative
        // lobbies after the race marker check has completed.
        var join = AccessTools.Method(typeof(SteamJoinCallbackHandler), "JoinToHost")
            ?? throw new MissingMethodException(typeof(SteamJoinCallbackHandler).FullName, "JoinToHost");
        if (join.Invoke(null, [lobbyId, friendId, null]) is Task task)
            await task;
    }

    private static async Task RequestLobbyDataAsync(ulong lobbyId)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = Callback<LobbyDataUpdate_t>.Create(update =>
        {
            if (update.m_ulSteamIDLobby == lobbyId)
                completion.TrySetResult(update.m_bSuccess != 0);
        });
        if (!SteamMatchmaking.RequestLobbyData(new CSteamID(lobbyId)))
            return;
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
    }

    private static string RoomCode(ulong lobbyId)
    {
        var value = lobbyId.ToString();
        return value.Length <= 6 ? value : value[^6..];
    }
}
