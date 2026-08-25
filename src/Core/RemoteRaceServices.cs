using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Modding;
using Sts2SpireRace.Game;

namespace Sts2SpireRace.Core;

public sealed class RemoteRaceServices : IRaceServices, IRaceAuthService, IRaceClockService, IRaceMatchService,
    IRaceIntegrityService, IRaceEntertainmentRoomService, IRacePartyService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static RemoteRaceServices() => Json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

    private HttpClient _http;
    private readonly DemoRaceServices _secondary;
    private readonly SteamWebApiTicketProvider _ticketProvider = new();
    private readonly SemaphoreSlim _authenticationLock = new(1, 1);
    private readonly SemaphoreSlim _socketWriteLock = new(1, 1);
    private readonly SemaphoreSlim _integrityLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _socketLifetime;
    private string _token = string.Empty;
    private string _refreshToken = string.Empty;
    private PlatformIdentity? _identity;
    private long _clockOffsetMilliseconds;
    private long _lastClockSyncLocalMilliseconds;
    private QueueRequest? _queueRequest;
    private RaceTeam? _localTeam;
    private RaceTeam? _opponentTeam;
    private TaskCompletionSource<bool>? _saveQuitReply;
    private long? _localFinishPending;
    private DateTimeOffset _integrityVerifiedAt;

    public RemoteRaceServices(IRacePlatformIdentityProvider identityProvider, IRaceSessionLauncher? sessionLauncher = null, Uri? serverUri = null)
    {
        IdentityProvider = identityProvider;
        SessionLauncher = sessionLauncher ?? new NoOpSessionLauncher();
        _secondary = new DemoRaceServices(identityProvider, SessionLauncher);
        _http = new HttpClient { BaseAddress = serverUri ?? RaceRuntimeInfo.ServerUri, Timeout = TimeSpan.FromSeconds(15) };
    }

    public IRacePlatformIdentityProvider IdentityProvider { get; }
    public IRaceSessionLauncher SessionLauncher { get; }
    public QueueSnapshot CurrentQueue { get; private set; } = new(QueueState.Idle);
    public MatchAssignment? CurrentMatch { get; private set; }
    public SettlementSnapshot? CurrentSettlement { get; private set; }
    public LegendDraftPrompt? CurrentLegendDraft { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(_token);
    public IntegrityVerdict LastVerdict { get; private set; } = IntegrityVerdict.Pending;
    public ServerClockSnapshot CurrentClock { get; private set; } = new(0, 0, 0, 0, false);
    public EntertainmentRoom? CurrentRoom { get; private set; }
    public RaceParty? CurrentParty { get; private set; }

    public event Action<QueueSnapshot>? QueueChanged;
    public event Action<ServerClockSnapshot>? ClockChanged;
    public event Action<MatchAssignment?>? MatchChanged;
    public event Action<SettlementSnapshot>? MatchSettled;
    public event Action<LegendDraftPrompt?>? LegendDraftChanged;
    public event Action<EntertainmentRoom?>? RoomChanged;
    public event Action<string>? RoomExited;
    public event Action<RaceParty?>? PartyChanged;
    public event Action<RaceInvite>? InviteReceived;

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (IsAuthenticated)
            return;
        await _authenticationLock.WaitAsync(cancellationToken);
        try
        {
            if (IsAuthenticated)
                return;
            _identity = await IdentityProvider.GetLocalIdentityAsync(cancellationToken);
            if (_identity.PlatformId == 0 && !RaceRuntimeInfo.DevelopmentAuthentication)
                throw new InvalidOperationException("A Steam identity is required for online races.");
            if (await TryResumeSessionAsync(_identity.PlatformId.ToString(), cancellationToken))
                return;
            var ticket = RaceRuntimeInfo.DevelopmentAuthentication
                ? "development"
                : await _ticketProvider.GetTicketAsync(cancellationToken);
            var response = await PostAsync<AuthResponse>("v1/auth/steam", new
            {
                steam_id = _identity.PlatformId.ToString(),
                display_name = _identity.DisplayName,
                ticket
            }, false, cancellationToken);
            ApplyAuthentication(response, _identity.PlatformId.ToString());
        }
        finally
        {
            _authenticationLock.Release();
        }
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default) => AuthenticateAsync(cancellationToken);

    public async Task ChangeServerAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        var normalized = new Uri((serverUri.AbsoluteUri.TrimEnd('/') + "/"));
        var previousHttp = _http;
        _socketLifetime?.Cancel();
        if (_socket is not null)
        {
            try { _socket.Dispose(); } catch { }
            _socket = null;
        }
        _token = string.Empty;
        _refreshToken = string.Empty;
        _http = new HttpClient { BaseAddress = normalized, Timeout = TimeSpan.FromSeconds(15) };
        _queueRequest = null;
        _localTeam = null;
        _opponentTeam = null;
        _localFinishPending = null;
        CurrentMatch = null;
        CurrentSettlement = null;
        CurrentLegendDraft = null;
        CurrentRoom = null;
        CurrentParty = null;
        CurrentQueue = new QueueSnapshot(QueueState.Idle, Detail: "server_changed");
        MatchChanged?.Invoke(null);
        RoomChanged?.Invoke(null);
        PartyChanged?.Invoke(null);
        QueueChanged?.Invoke(CurrentQueue);
        try
        {
            await EnsureOnlineReadyAsync(cancellationToken, verifyIntegrity: false);
            previousHttp.Dispose();
        }
        catch
        {
            _http.Dispose();
            _http = previousHttp;
            _token = string.Empty;
            _refreshToken = string.Empty;
            await EnsureOnlineReadyAsync(cancellationToken, verifyIntegrity: false);
            throw;
        }
    }

    private async Task<bool> TryResumeSessionAsync(string playerId, CancellationToken cancellationToken)
    {
        try
        {
            var path = Godot.ProjectSettings.GlobalizePath("user://stsrace_credentials.json");
            if (!File.Exists(path))
                return false;
            var cached = JsonSerializer.Deserialize<CredentialCache>(await File.ReadAllTextAsync(path, cancellationToken), Json);
            if (cached is null || cached.PlayerId != playerId || string.IsNullOrWhiteSpace(cached.RefreshToken))
                return false;
            var response = await PostAsync<AuthResponse>("v1/auth/refresh", new { refresh_token = cached.RefreshToken }, false, cancellationToken);
            ApplyAuthentication(response, playerId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyAuthentication(AuthResponse response, string playerId)
    {
        _token = response.AccessToken;
        _refreshToken = response.RefreshToken;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (string.IsNullOrWhiteSpace(_refreshToken))
            return;
        var path = Godot.ProjectSettings.GlobalizePath("user://stsrace_credentials.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new CredentialCache(playerId, _refreshToken), Json));
    }

    public async Task JoinQueueAsync(QueueRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Kind == QueueKind.Entertainment)
            throw new InvalidOperationException("Entertainment rooms do not use matchmaking.");
        RaceRules.Validate(request.Rules);
        _queueRequest = request;
        _localTeam = await BuildLocalTeamAsync(request.TeamSize, cancellationToken);
        SetQueue(new QueueSnapshot(QueueState.Searching, request, _localTeam, Detail: "searching"));
        try
        {
            await EnsureOnlineReadyAsync(cancellationToken);
            var response = await PostAsync<QueueJoinResponse>("v1/queue/join", new
            {
                game_version = RaceRuntimeInfo.GameVersion,
                kind = request.Kind == QueueKind.Ranked ? "ranked" : "casual",
                team_size = (int)request.TeamSize,
                pool = request.TeamSize == TeamSize.One ? "solo" : "team",
                visible_tiers = new[] { request.Kind == QueueKind.Ranked ? "Unranked" : "Gold" },
                team_player_ids = _localTeam.Participants.Select(x => x.Id).ToArray(),
                character_id = request.PreferredCharacter
            }, true, cancellationToken);
            if (response.Assignment is not null)
                ApplyAssignment(response.Assignment, QueueState.ReadyCheck);
        }
        catch (Exception exception)
        {
            SetQueue(new QueueSnapshot(QueueState.Idle, request, _localTeam, Detail: exception.Message));
            throw;
        }
    }

    public async Task CancelQueueAsync(CancellationToken cancellationToken = default)
    {
        if (IsAuthenticated)
            await PostAsync<JsonElement>("v1/queue/cancel", new { }, true, cancellationToken);
        SetQueue(new QueueSnapshot(QueueState.Idle, Detail: "cancelled"));
    }

    public async Task ConfirmMatchAsync(bool accepted, CancellationToken cancellationToken = default)
    {
        await PostAsync<JsonElement>("v1/match/confirm", new { accepted }, true, cancellationToken);
        if (!accepted)
        {
            SetQueue(new QueueSnapshot(QueueState.Idle, Detail: "declined"));
            return;
        }
        SetQueue(CurrentQueue with { State = QueueState.Lobby, Detail = "lobby" });
    }

    public async Task SetLocalTeamReadyAsync(bool ready, CancellationToken cancellationToken = default)
    {
        if (!ready)
            return;
        await PostAsync<JsonElement>("v1/match/ready", new { }, true, cancellationToken);
        SetQueue(CurrentQueue with { State = QueueState.Starting, Detail = "starting" });
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var localBefore = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var response = await GetAsync<ClockResponse>("v1/clock", false, cancellationToken);
        var roundTrip = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var midpoint = localBefore + roundTrip / 2;
        _clockOffsetMilliseconds = response.ServerUnixMs - midpoint;
        _lastClockSyncLocalMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        UpdateClock(roundTrip);
    }

    public async Task<IntegrityVerdict> VerifyAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        if (LastVerdict == IntegrityVerdict.Accepted && DateTimeOffset.UtcNow - _integrityVerifiedAt < TimeSpan.FromMinutes(5))
            return LastVerdict;
        await _integrityLock.WaitAsync(cancellationToken);
        try
        {
            if (LastVerdict == IntegrityVerdict.Accepted && DateTimeOffset.UtcNow - _integrityVerifiedAt < TimeSpan.FromMinutes(5))
                return LastVerdict;
        await AuthenticateAsync(cancellationToken);
        var manifest = await GetAsync<IntegrityManifestDto>($"v1/integrity/{gameVersion}", false, cancellationToken);
        var root = Path.GetDirectoryName(Godot.OS.GetExecutablePath()) ?? AppContext.BaseDirectory;
        var files = new List<object>();
        foreach (var file in manifest.GameFiles.Concat(manifest.AllowedModFiles))
        {
            var path = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                LastVerdict = IntegrityVerdict.ModifiedGameFile;
                return LastVerdict;
            }
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            files.Add(new { path = file.Path, sha256 = hash, size = stream.Length });
        }
        var modIds = ModManager.GetLoadedMods().Select(x => x.manifest?.id).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var verdict = await PostAsync<IntegrityVerdictDto>("v1/integrity/verify", new
        {
            game_version = gameVersion,
            files,
            loaded_mod_ids = modIds,
            challenge_nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
        }, true, cancellationToken);
        LastVerdict = verdict.Accepted ? IntegrityVerdict.Accepted : verdict.Code switch
        {
            "unsupported_version" => IntegrityVerdict.UnsupportedVersion,
            "unsupported_mod" => IntegrityVerdict.UnsupportedMod,
            "modified_file" or "missing_file" => IntegrityVerdict.ModifiedGameFile,
            _ => IntegrityVerdict.ChallengeFailed
        };
        if (LastVerdict == IntegrityVerdict.Accepted)
            _integrityVerifiedAt = DateTimeOffset.UtcNow;
        return LastVerdict;
        }
        finally { _integrityLock.Release(); }
    }

    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        try { await EnsureOnlineReadyAsync(cancellationToken); }
        catch { /* Queue entry will surface a localized connection/integrity error. */ }
    }

    public Task ReportProgressAsync(ProgressCheckpoint checkpoint, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendSocketAsync("progress", new
        {
            idempotency_key = idempotencyKey,
            progress = new
            {
                match_id = checkpoint.MatchId,
                game_id = checkpoint.GameId,
                team_id = checkpoint.TeamId,
                sequence = checkpoint.Sequence,
                floor = checkpoint.Floor,
                floor_entered_at_ms = checkpoint.FloorEnteredAtMilliseconds,
                final_boss_defeated = checkpoint.FinalBossDefeated,
                completed_at_ms = checkpoint.CompletedAtMilliseconds,
                outcome = checkpoint.Outcome,
                restart_count = checkpoint.RestartCount,
                event_sl_used = checkpoint.EventSlUsed,
                combat_sl_used = checkpoint.CombatSlUsed
            }
        }, cancellationToken);
    public Task ChooseDeathActionAsync(bool restart, CancellationToken cancellationToken = default) =>
        SendSocketAsync("death_choice", new { restart }, cancellationToken);
    public async Task RequestSaveAndQuitAsync(SlCategory category, bool confirmForfeit, CancellationToken cancellationToken = default)
    {
        _saveQuitReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendSocketAsync("save_quit", new { combat = category == SlCategory.Combat, confirm_forfeit = confirmForfeit }, cancellationToken);
        try
        {
            if (!await _saveQuitReply.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken))
                throw new InvalidOperationException("Save and quit was rejected by the race server.");
        }
        finally { _saveQuitReply = null; }
    }
    public Task ResumeSavedRunAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendSocketAsync("resume", new { idempotency_key = idempotencyKey }, cancellationToken);
    public Task SurrenderAsync(CancellationToken cancellationToken = default) => VoteSurrenderAsync(true, cancellationToken);
    public Task VoteSurrenderAsync(bool accept, CancellationToken cancellationToken = default) =>
        SendSocketAsync("surrender_vote", new { accept }, cancellationToken);
    public Task SubmitLegendBansAsync(string banOne, string banTwo, CancellationToken cancellationToken = default) =>
        SendSocketAsync("legend_bans", new { ban_one = banOne, ban_two = banTwo }, cancellationToken);
    public Task SelectLegendCharacterAsync(string characterId, CancellationToken cancellationToken = default) =>
        SendSocketAsync("legend_pick", new { character_id = characterId }, cancellationToken);

    public async Task<EntertainmentRoom> CreateRoomAsync(RaceRuleSet rules, CancellationToken cancellationToken = default)
    {
        await EnsureOnlineReadyAsync(cancellationToken, verifyIntegrity: false);
        var room = await PostAsync<RoomDto>("v1/rooms", ToServerRules(rules), true, cancellationToken);
        return ApplyRoom(room);
    }

    public async Task<EntertainmentRoom> JoinRoomAsync(string code, CancellationToken cancellationToken = default)
    {
        await EnsureOnlineReadyAsync(cancellationToken, verifyIntegrity: false);
        var room = await PostAsync<RoomDto>("v1/rooms/join", new { code }, true, cancellationToken);
        return ApplyRoom(room);
    }
    public async Task<EntertainmentRoom> UpdateRoomRulesAsync(RaceRuleSet rules, CancellationToken cancellationToken = default)
    {
        if (CurrentRoom is null) throw new InvalidOperationException("No entertainment room is active.");
        var room = await PutAsync<RoomDto>($"v1/rooms/{CurrentRoom.Code}/rules", ToServerRules(rules), true, cancellationToken);
        return ApplyRoom(room);
    }
    public async Task<EntertainmentRoom> SwitchTeamAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentRoom is null) throw new InvalidOperationException("No entertainment room is active.");
        var room = await PostAsync<RoomDto>($"v1/rooms/{CurrentRoom.Code}/team", new { }, true, cancellationToken);
        return ApplyRoom(room);
    }

    public async Task<EntertainmentRoom> SetRoomMemberAsync(string characterId, bool ready, CancellationToken cancellationToken = default)
    {
        if (CurrentRoom is null) throw new InvalidOperationException("No entertainment room is active.");
        var room = await PutAsync<RoomDto>($"v1/rooms/{CurrentRoom.Code}/member", new { character_id = characterId, ready }, true, cancellationToken);
        return ApplyRoom(room);
    }

    public async Task<EntertainmentRoom> StartRoomAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentRoom is null) throw new InvalidOperationException("No entertainment room is active.");
        var room = await PostAsync<RoomDto>($"v1/rooms/{CurrentRoom.Code}/start", new { }, true, cancellationToken);
        return ApplyRoom(room);
    }
    public async Task LeaveRoomAsync(CancellationToken cancellationToken = default)
    {
        var room = CurrentRoom;
        if (room is not null)
        {
            try
            {
                _ = await PostAsync<JsonElement>($"v1/rooms/{room.Code}/leave", new { }, true, cancellationToken);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("room not found", StringComparison.OrdinalIgnoreCase))
            {
                // Older servers returned 404 after the host had already closed
                // the room. Locally this is the same successful end state.
            }
        }
        CurrentRoom = null;
        RoomChanged?.Invoke(null);
        RoomExited?.Invoke("left");
    }
    public Task InviteFriendAsync(string playerId, CancellationToken cancellationToken = default) => InviteAsync(playerId, cancellationToken);

    public async Task OpenPartyLobbyAsync(QueueKind kind, TeamSize teamSize, CancellationToken cancellationToken = default)
    {
        if (CurrentParty is { } existing && existing.Kind == kind && existing.TeamSize == teamSize)
            return;
        await EnsureOnlineReadyAsync(cancellationToken, verifyIntegrity: false);
        await SendSocketAsync("party_open", new { kind = kind == QueueKind.Ranked ? "ranked" : "casual", team_size = (int)teamSize }, cancellationToken);
    }
    public Task SetPartyCharacterAsync(string characterId, CancellationToken cancellationToken = default) =>
        SendSocketAsync("party_character", new { character_id = characterId }, cancellationToken);

    public async Task LeavePartyAsync(CancellationToken cancellationToken = default)
    {
        await SendSocketAsync("party_leave", new { }, cancellationToken);
        CurrentParty = null;
        PartyChanged?.Invoke(null);
    }

    public async Task<PlayerProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default)
    {
        await AuthenticateAsync(cancellationToken);
        var profile = await GetAsync<ProfileDto>("v1/profile", true, cancellationToken);
        return ToPlayerProfile(profile, true);
    }
    public async Task<PlayerProfile?> GetProfileAsync(string playerId, CancellationToken cancellationToken = default)
    {
        await AuthenticateAsync(cancellationToken);
        try
        {
            var profile = await GetAsync<ProfileDto>($"v1/profile/{Uri.EscapeDataString(playerId)}", true, cancellationToken);
            return ToPlayerProfile(profile);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }
    public async Task<PlayerProfile> UpdateLocalProfileAsync(string displayName, string favoriteCharacter, CancellationToken cancellationToken = default)
    {
        await AuthenticateAsync(cancellationToken);
        var profile = await PutAsync<ProfileDto>("v1/profile", new
        {
            display_name = displayName.Trim(),
            favorite_character = favoriteCharacter
        }, true, cancellationToken);
        if (_identity is not null)
            _identity = _identity with { DisplayName = profile.DisplayName };
        return ToPlayerProfile(profile, true);
    }
    public Task<IReadOnlyList<TitleDefinition>> GetTitlesAsync(CancellationToken cancellationToken = default) => _secondary.GetTitlesAsync(cancellationToken);
    public Task EquipTitleAsync(string titleId, CancellationToken cancellationToken = default) => _secondary.EquipTitleAsync(titleId, cancellationToken);
    public async Task<IReadOnlyList<FriendEntry>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        await AuthenticateAsync(cancellationToken);
        var rows = await GetAsync<SocialDto[]>("v1/friends", true, cancellationToken);
        return rows.Select(MapSocial).ToArray();
    }
    public async Task<IReadOnlyList<FriendEntry>> SearchPlayersAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return Array.Empty<FriendEntry>();
        await AuthenticateAsync(cancellationToken);
        var rows = await GetAsync<SocialDto[]>($"v1/players/search?q={Uri.EscapeDataString(query.Trim())}", true, cancellationToken);
        return rows.Select(x => new FriendEntry(x.PlayerId, x.DisplayName, FriendPresence.SearchResult, "search", "", x.Tier)).ToArray();
    }
    public async Task SendFriendRequestAsync(string playerId, CancellationToken cancellationToken = default) =>
        _ = await PostAsync<JsonElement>("v1/friends/request", new { player_id = playerId }, true, cancellationToken);
    public async Task AcceptRequestAsync(string playerId, CancellationToken cancellationToken = default) =>
        _ = await PostAsync<JsonElement>("v1/friends/accept", new { player_id = playerId }, true, cancellationToken);
    public async Task DeclineRequestAsync(string playerId, CancellationToken cancellationToken = default) =>
        _ = await PostAsync<JsonElement>("v1/friends/decline", new { player_id = playerId }, true, cancellationToken);
    public async Task RemoveFriendAsync(string playerId, CancellationToken cancellationToken = default) =>
        _ = await PostAsync<JsonElement>("v1/friends/remove", new { player_id = playerId }, true, cancellationToken);
    public async Task InviteAsync(string playerId, CancellationToken cancellationToken = default)
    {
        await EnsureOnlineReadyAsync(cancellationToken, verifyIntegrity: false);
        await SendSocketAsync("friend_invite", new { player_id = playerId, room_code = CurrentRoom?.Code ?? string.Empty }, cancellationToken);
    }
    public async Task RespondToInviteAsync(string playerId, bool accepted, CancellationToken cancellationToken = default)
    {
        await EnsureOnlineReadyAsync(cancellationToken, verifyIntegrity: false);
        await SendSocketAsync("friend_invite_response", new { player_id = playerId, accepted }, cancellationToken);
    }
    public async Task<IReadOnlyList<LeaderboardEntry>> QueryAsync(RankedPool pool, bool friendsOnly, bool historicalSeason, CancellationToken cancellationToken = default)
    {
        await AuthenticateAsync(cancellationToken);
        var rows = await GetAsync<LeaderboardDto[]>($"v1/leaderboard?pool={(pool == RankedPool.Solo ? "solo" : "team")}&friends_only={friendsOnly.ToString().ToLowerInvariant()}&historical={historicalSeason.ToString().ToLowerInvariant()}", true, cancellationToken);
        return rows.Select(x => new LeaderboardEntry(x.Position, x.PlayerId, x.DisplayName, pool, x.Tier, x.Rating,
            x.Wins + x.Losses == 0 ? 0 : (double)x.Wins / (x.Wins + x.Losses), TimeSpan.FromMilliseconds(x.BestTimeMs))).ToArray();
    }
    public Task<ActivityDefinition> GetCurrentActivityAsync(CancellationToken cancellationToken = default) => _secondary.GetCurrentActivityAsync(cancellationToken);
    public Task ClaimMissionAsync(string missionId, CancellationToken cancellationToken = default) => _secondary.ClaimMissionAsync(missionId, cancellationToken);
    public Task ClaimRewardAsync(int level, CancellationToken cancellationToken = default) => _secondary.ClaimRewardAsync(level, cancellationToken);

    private async Task EnsureOnlineReadyAsync(CancellationToken cancellationToken, bool verifyIntegrity = true)
    {
        await AuthenticateAsync(cancellationToken);
        if (verifyIntegrity && await VerifyAsync(RaceRuntimeInfo.GameVersion, cancellationToken) != IntegrityVerdict.Accepted)
            throw new InvalidOperationException($"Integrity check failed: {LastVerdict}");
        await EnsureSocketAsync(cancellationToken);
        await SynchronizeAsync(cancellationToken);
    }

    private async Task EnsureSocketAsync(CancellationToken cancellationToken)
    {
        if (_socket?.State == WebSocketState.Open)
            return;
        _socketLifetime?.Cancel();
        _socketLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _socket = new ClientWebSocket();
        var baseUri = _http.BaseAddress ?? throw new InvalidOperationException("Server URI is missing.");
        var builder = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = "/v1/ws", Query = "token=" + Uri.EscapeDataString(_token) };
        await _socket.ConnectAsync(builder.Uri, cancellationToken);
        _ = ReceiveLoopAsync(_socket, _socketLifetime.Token);
        _ = HeartbeatLoopAsync(_socket, _socketLifetime.Token);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await SendSocketPayloadAsync(socket, "heartbeat", new
                {
                    client_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (!root.TryGetProperty("data", out var data))
                    continue;
                switch (type)
                {
                    case "match_found": ApplyAssignment(data.Deserialize<AssignmentDto>(Json)!, QueueState.ReadyCheck); break;
                    case "match_started":
                        ApplyAssignment(data.Deserialize<AssignmentDto>(Json)!, QueueState.Starting);
                        if (CurrentMatch is not null) await SessionLauncher.LaunchAsync(CurrentMatch, cancellationToken);
                        break;
                    case "clock":
                        if (data.TryGetProperty("server_unix_ms", out var now))
                        {
                            _clockOffsetMilliseconds = now.GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            UpdateClock(0);
                        }
                        break;
                    case "settlement": ApplySettlement(data.Deserialize<SettlementDto>(Json)!); break;
                    case "finish_pending":
                        _localFinishPending = data.TryGetProperty("completed_at_ms", out var completedAt) && completedAt.ValueKind == JsonValueKind.Number
                            ? completedAt.GetInt64() : null;
                        SetQueue(CurrentQueue with { State = QueueState.FinishPending, Detail = "finished_pending" });
                        break;
                    case "match_cancelled":
                        CurrentMatch = null;
                        MatchChanged?.Invoke(null);
                        SetQueue(new QueueSnapshot(QueueState.Idle, _queueRequest, _localTeam, Detail: "opponent_disconnected"));
                        break;
                    case "entertainment_room_updated": ApplyRoom(data.Deserialize<RoomDto>(Json)!); break;
                    case "entertainment_room_starting": ApplyRoom(data.Deserialize<RoomDto>(Json)!); break;
                    case "entertainment_steam_lobby_required":
                        var roomLobby = data.Deserialize<EntertainmentLobbyRequiredDto>(Json)!;
                        var requiredRoom = ApplyRoom(roomLobby.Room);
                        if (SessionLauncher is IRaceSteamLobbyCoordinator roomCoordinator)
                        {
                            var prepared = BuildEntertainmentAssignment(requiredRoom, roomLobby.HostPlayerId, "", "");
                            var roomLobbyId = await roomCoordinator.CreateTeamLobbyAsync(prepared, cancellationToken);
                            await SendSocketPayloadAsync(socket, "entertainment_steam_lobby_ready", new { code = requiredRoom.Code, lobby_id = roomLobbyId.ToString() }, cancellationToken);
                        }
                        break;
                    case "entertainment_match_started":
                        var entertainmentLaunch = data.Deserialize<EntertainmentLaunchDto>(Json)!;
                        var launchRoom = ApplyRoom(entertainmentLaunch.Room);
                        CurrentMatch = BuildEntertainmentAssignment(launchRoom, entertainmentLaunch.FirstSteamHostPlayerId,
                            entertainmentLaunch.FirstSteamLobbyId, entertainmentLaunch.SecondSteamLobbyId, entertainmentLaunch.SecondSteamHostPlayerId);
                        MatchChanged?.Invoke(CurrentMatch);
                        await SessionLauncher.LaunchAsync(CurrentMatch, cancellationToken);
                        break;
                    case "entertainment_room_closed":
                        CurrentRoom = null;
                        RoomChanged?.Invoke(null);
                        RoomExited?.Invoke("host_closed");
                        break;
                    case "party_updated": ApplyParty(data.Deserialize<PartyDto>(Json)!); break;
                    case "party_closed":
                        CurrentParty = null;
                        PartyChanged?.Invoke(null);
                        break;
                    case "steam_lobby_required":
                        var lobbyAssignment = data.Deserialize<AssignmentDto>(Json)!;
                        ApplyAssignment(lobbyAssignment, QueueState.Starting);
                        if (SessionLauncher is IRaceSteamLobbyCoordinator coordinator)
                        {
                            var lobbyId = await coordinator.CreateTeamLobbyAsync(CurrentMatch!, cancellationToken);
                            await SendSocketPayloadAsync(socket, "steam_lobby_ready", new { lobby_id = lobbyId.ToString() }, cancellationToken);
                        }
                        break;
                    case "friend_invite":
                        var invite = data.Deserialize<RaceInviteDto>(Json)!;
                        InviteReceived?.Invoke(new RaceInvite(invite.PlayerId, invite.DisplayName, invite.RoomCode, invite.PartyId,
                            invite.Kind == "ranked" ? QueueKind.Ranked : QueueKind.Casual,
                            Enum.IsDefined(typeof(TeamSize), invite.TeamSize) ? (TeamSize)invite.TeamSize : TeamSize.One));
                        break;
                    case "save_quit_accepted": _saveQuitReply?.TrySetResult(true); break;
                    case "save_quit_rejected": _saveQuitReply?.TrySetException(new InvalidOperationException(data.ValueKind == JsonValueKind.String ? data.GetString() : "SL allowance exhausted")); break;
                    case "legend_ban_required": ApplyLegendPrompt(data, true); break;
                    case "legend_pick_required": ApplyLegendPrompt(data, false); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)
        {
            if (CurrentQueue.State is QueueState.Searching or QueueState.MatchFound or QueueState.ReadyCheck or QueueState.Lobby or QueueState.Starting)
                SetQueue(new QueueSnapshot(QueueState.Idle, _queueRequest, _localTeam, Detail: "connection_lost"));
        }
    }

    private void ApplyAssignment(AssignmentDto dto, QueueState state)
    {
        var identityId = (_identity?.PlatformId ?? 0).ToString();
        var localFirst = dto.FirstPlayerIds.Contains(identityId);
        var localIds = localFirst ? dto.FirstPlayerIds : dto.SecondPlayerIds;
        var opponentIds = localFirst ? dto.SecondPlayerIds : dto.FirstPlayerIds;
        _localTeam = BuildTeam(localFirst ? dto.FirstTeamId : dto.SecondTeamId, "Blue", localIds, identityId, dto.Rules.CharacterId, dto.CharacterIds);
        _opponentTeam = BuildTeam(localFirst ? dto.SecondTeamId : dto.FirstTeamId, "Red", opponentIds, string.Empty, dto.Rules.CharacterId, dto.CharacterIds);
        var kind = dto.Kind == "ranked" ? QueueKind.Ranked : QueueKind.Casual;
        var rules = FromServerRules(dto.Rules);
        _queueRequest ??= new QueueRequest(kind, (TeamSize)dto.TeamSize, RaceRules.PoolFor((TeamSize)dto.TeamSize), rules);
        CurrentMatch = new MatchAssignment(dto.MatchId, dto.GameId, dto.GameVersion, kind, (TeamSize)dto.TeamSize, rules,
            _localTeam, _opponentTeam, dto.Rules.CharacterId, dto.SessionNonce, dto.StartedAtMs, null, dto.CharacterIds,
            dto.FirstSteamHostPlayerId, dto.SecondSteamHostPlayerId, dto.FirstSteamLobbyId, dto.SecondSteamLobbyId);
        MatchChanged?.Invoke(CurrentMatch);
        SetQueue(new QueueSnapshot(state, _queueRequest, _localTeam, _opponentTeam, Detail: state == QueueState.MatchFound ? "match_found" : "starting"));
    }

    private void ApplySettlement(SettlementDto dto)
    {
        if (CurrentMatch is null || _localTeam is null || _opponentTeam is null)
            return;
        var localSide = dto.First.TeamId == _localTeam.Id ? dto.First : dto.Second;
        var enemySide = dto.First.TeamId == _localTeam.Id ? dto.Second : dto.First;
        var games = (dto.SeriesGames ?? Array.Empty<LegendGameDto>()).Select(x => new LegendGameResult(
            x.GameNumber, x.CharacterId, x.WinnerTeamId, ParseFinishReason(x.Reason), x.ElapsedMs)).ToArray();
        var localPlayerId = (_identity?.PlatformId ?? 0).ToString();
        var ratingDelta = dto.VisibleRatingDeltas?.GetValueOrDefault(localPlayerId) ?? 0;
        var settlement = new SettlementSnapshot(dto.MatchId, dto.GameId, dto.WinnerTeamId, ParseFinishReason(dto.Reason),
            MapSide(localSide, _localTeam.Name), MapSide(enemySide, _opponentTeam.Name), ratingDelta, games, dto.AuditDetail, dto.CompletedAt);
        CurrentSettlement = settlement;
        MatchSettled?.Invoke(settlement);
        var localTime = localSide.CompletionMs.HasValue ? TimeSpan.FromMilliseconds(localSide.CompletionMs.Value) : (TimeSpan?)null;
        var enemyTime = enemySide.CompletionMs.HasValue ? TimeSpan.FromMilliseconds(enemySide.CompletionMs.Value) : (TimeSpan?)null;
        _localTeam = _localTeam with { SharedRunTime = localTime };
        _opponentTeam = _opponentTeam with { SharedRunTime = enemyTime };
        var result = new RaceResult(dto.MatchId, _localTeam, _opponentTeam, dto.WinnerTeamId == _localTeam.Id, ratingDelta, dto.CompletedAt, settlement);
        SetQueue(new QueueSnapshot(QueueState.Completed, _queueRequest, _localTeam, _opponentTeam, result, "completed"));
    }

    private void ApplyLegendPrompt(JsonElement data, bool banPhase)
    {
        var available = data.GetProperty("available_characters").Deserialize<string[]>(Json) ?? Array.Empty<string>();
        var state = data.TryGetProperty("draft", out var draftElement)
            ? draftElement.Deserialize<LegendDraftState>(Json)!
            : new LegendDraftState("", "", "", "", Array.Empty<string>(), null, null, 1, 0, 0, DateTimeOffset.UtcNow.AddSeconds(RaceRules.LegendPickSeconds));
        CurrentLegendDraft = new LegendDraftPrompt(state, available, banPhase, CurrentMatch?.LocalTeam.Id == state.SelectingTeamId);
        LegendDraftChanged?.Invoke(CurrentLegendDraft);
        SetQueue(CurrentQueue with { State = QueueState.Draft, Detail = banPhase ? "legend_ban" : "legend_pick" });
    }

    private void UpdateClock(long roundTrip)
    {
        var serverNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _clockOffsetMilliseconds;
        var started = CurrentMatch?.StartedAtUnixMilliseconds ?? 0;
        CurrentClock = new ServerClockSnapshot(serverNow, started, started == 0 ? 0 : Math.Max(0, serverNow - started), roundTrip, _lastClockSyncLocalMilliseconds != 0);
        ClockChanged?.Invoke(CurrentClock);
    }

    private async Task SendSocketAsync(string type, object data, CancellationToken cancellationToken)
    {
        await EnsureSocketAsync(cancellationToken);
        await SendSocketPayloadAsync(_socket!, type, data, cancellationToken);
    }

    private async Task SendSocketPayloadAsync(ClientWebSocket socket, string type, object data, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { type, data }, Json);
        await _socketWriteLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
                throw new WebSocketException("Race server connection is not open.");
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally { _socketWriteLock.Release(); }
    }

    private async Task<T> GetAsync<T>(string path, bool authenticated, CancellationToken cancellationToken)
    {
        if (authenticated) await AuthenticateAsync(cancellationToken);
        using var response = await _http.GetAsync(path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> PostAsync<T>(string path, object payload, bool authenticated, CancellationToken cancellationToken)
    {
        if (authenticated) await AuthenticateAsync(cancellationToken);
        using var content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> PutAsync<T>(string path, object payload, bool authenticated, CancellationToken cancellationToken)
    {
        if (authenticated) await AuthenticateAsync(cancellationToken);
        using var content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync(path, content, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? detail = null;
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.TryGetProperty("code", out var code) && code.GetString() == "beta_access_required" &&
                    string.Equals(response.RequestMessage?.RequestUri?.Host, "spirerace.xyz", StringComparison.OrdinalIgnoreCase))
                    detail = RaceTextCatalog.Get("auth.beta_access_required");
                else if (root.TryGetProperty("error", out var error)) detail = error.GetString();
                else if (root.TryGetProperty("detail", out var verdictDetail) && !string.IsNullOrWhiteSpace(verdictDetail.GetString())) detail = verdictDetail.GetString();
                else if (root.TryGetProperty("code", out var fallbackCode)) detail = fallbackCode.GetString();
            }
            catch (JsonException) { }
            throw new InvalidOperationException(detail ?? $"Race server returned {(int)response.StatusCode}: {text}");
        }
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new InvalidOperationException("Race server returned an empty response.");
    }

    private async Task<RaceTeam> BuildLocalTeamAsync(TeamSize size, CancellationToken cancellationToken)
    {
        var identity = _identity ?? await IdentityProvider.GetLocalIdentityAsync(cancellationToken);
        var participants = CurrentParty is { TeamSize: var partySize } party && partySize == size
            ? party.Members.Select(x => x with { IsLocal = x.Id == identity.PlatformId.ToString() }).ToArray()
            : new[] { new RaceParticipant(identity.PlatformId.ToString(), identity.DisplayName, "", true) };
        return new RaceTeam("pending-local", "Blue", participants);
    }

    private static RaceTeam BuildTeam(string id, string name, IReadOnlyList<string> ids, string localIdentity, string character,
        IReadOnlyDictionary<string, string>? characters = null) =>
        new(id, name, ids.Select(x => new RaceParticipant(x, x == localIdentity ? "You" : x,
            characters?.GetValueOrDefault(x) ?? character, x == localIdentity)).ToArray());
    private static RaceRuleSet FromServerRules(ServerRulesDto x) => new((TeamSize)x.TeamSize, x.Seed, x.RandomSeed, x.Ascension, x.AllowDuplicateCharacters,
        string.IsNullOrEmpty(x.CharacterPolicy) ? (string.IsNullOrEmpty(x.CharacterId) ? "free_pick" : "server_shared") : x.CharacterPolicy,
        string.IsNullOrEmpty(x.TimerKind) ? "server_time" : x.TimerKind, (int)(x.TimeLimitMs / 60000),
        string.IsNullOrEmpty(x.VictoryRule) ? "certified_race" : x.VictoryRule, x.AllowSpectators,
        string.IsNullOrEmpty(x.Visibility) ? "matchmade" : x.Visibility, x.Modifiers, x.EventSlLimit, x.CombatSlLimit,
        string.IsNullOrEmpty(x.CoordinationMode) ? "server" : x.CoordinationMode);
    private static object ToServerRules(RaceRuleSet x) => new { team_size = (int)x.TeamSize, seed = x.Seed, ascension = x.Ascension,
        time_limit_ms = x.TimeLimitMinutes * 60_000L, event_sl_limit = x.EventSlLimit, combat_sl_limit = x.CombatSlLimit,
        character_id = "", modifiers = x.Modifiers, random_seed = x.RandomSeed, allow_duplicate_characters = x.AllowDuplicateCharacters,
        character_policy = x.CharacterPolicy, timer_kind = x.TimerKind, victory_rule = x.VictoryRule,
        allow_spectators = false, visibility = x.Visibility, coordination_mode = x.CoordinationMode };
    private EntertainmentRoom ApplyRoom(RoomDto x)
    {
        var room = new EntertainmentRoom(x.Code, x.HostPlayerId, FromServerRules(x.Rules),
            x.Members.Select(m => new EntertainmentRoomMember(m.PlayerId, m.DisplayName, m.Team, m.IsHost, m.IsReady, m.CharacterId)).ToArray(), x.CreatedAt,
            x.CoordinationMode == "p2p" ? EntertainmentCoordinationMode.SteamP2P : EntertainmentCoordinationMode.Server, x.State);
        CurrentRoom = room;
        RoomChanged?.Invoke(room);
        return room;
    }
    private RaceParty ApplyParty(PartyDto x)
    {
        var identityId = (_identity?.PlatformId ?? 0).ToString();
        var party = new RaceParty(x.Id, x.LeaderPlayerId, x.Kind == "ranked" ? QueueKind.Ranked : QueueKind.Casual,
            (TeamSize)x.TeamSize,
            x.Members.Select(m => new RaceParticipant(m.PlayerId, m.DisplayName,
                string.IsNullOrWhiteSpace(m.CharacterId) ? "Ironclad" : m.CharacterId, m.PlayerId == identityId)).ToArray());
        CurrentParty = party;
        PartyChanged?.Invoke(party);
        return party;
    }
    private MatchAssignment BuildEntertainmentAssignment(EntertainmentRoom room, string firstHost, string firstLobby, string secondLobby, string secondHost = "")
    {
        var identityId = (_identity?.PlatformId ?? 0).ToString();
        var firstMembers = room.Members.Where(x => x.Team == 1).ToArray();
        var secondMembers = room.Members.Where(x => x.Team == 2).ToArray();
        secondHost = string.IsNullOrEmpty(secondHost) ? secondMembers.FirstOrDefault()?.PlayerId ?? "" : secondHost;
        var firstTeam = new RaceTeam("room-" + room.Code + "-1", "Blue", firstMembers.Select(x =>
            new RaceParticipant(x.PlayerId, x.DisplayName, x.CharacterId, x.PlayerId == identityId, x.IsReady)).ToArray());
        var secondTeam = new RaceTeam("room-" + room.Code + "-2", "Red", secondMembers.Select(x =>
            new RaceParticipant(x.PlayerId, x.DisplayName, x.CharacterId, x.PlayerId == identityId, x.IsReady)).ToArray());
        var localFirst = firstMembers.Any(x => x.PlayerId == identityId);
        var characterIds = room.Members.ToDictionary(x => x.PlayerId, x => x.CharacterId);
        return new MatchAssignment("fun-" + room.Code, "fun-" + room.Code, RaceRuntimeInfo.GameVersion, QueueKind.Entertainment,
            room.Rules.TeamSize, room.Rules, localFirst ? firstTeam : secondTeam, localFirst ? secondTeam : firstTeam,
            room.Rules.TeamSize == TeamSize.One ? firstMembers.First().CharacterId : "", room.Code,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, characterIds, firstHost, secondHost, firstLobby, secondLobby);
    }
    private static SettlementSide MapSide(SettlementSideDto x, string name) => new(x.TeamId, name, ParseOutcome(x.Outcome), x.HighestFloor,
        x.HighestFloorEnteredMs, x.CompletionMs, x.RestartCount, x.EventSlUsed, x.CombatSlUsed);
    private static ParticipantOutcome ParseOutcome(string value) => value switch { "finished" => ParticipantOutcome.Finished, "score_locked" => ParticipantOutcome.ScoreLocked,
        "surrendered" => ParticipantOutcome.Surrendered, "forfeited" => ParticipantOutcome.Forfeited, "timed_out" => ParticipantOutcome.TimedOut, _ => ParticipantOutcome.Active };
    private static FinishReason ParseFinishReason(string value) => value switch { "boss_completion" => FinishReason.BossCompletion, "highest_floor" => FinishReason.HighestFloor,
        "earlier_floor_entry" => FinishReason.EarlierFloorEntry, "random_tiebreak" => FinishReason.RandomTiebreak, "surrender" => FinishReason.Surrender,
        "disconnect" => FinishReason.Disconnect, "integrity_failure" => FinishReason.IntegrityFailure, "timeout" => FinishReason.Timeout, _ => FinishReason.SeriesVictory };
    private static RankSnapshot MapRank(RatingDto rating, RankedPool pool) => new(pool, rating.Tier, rating.Tier == "Legend" ? 0 : rating.Division,
        rating.Points, rating.Wins, rating.Losses, Math.Max(0, 10 - rating.Games), rating.LeaderboardRank);

    private static PlayerProfile ToPlayerProfile(ProfileDto profile, bool local = false) => new(
        profile.Id, profile.DisplayName, "", MapRank(profile.Solo, RankedPool.Solo), MapRank(profile.Team, RankedPool.Team),
        string.IsNullOrWhiteSpace(profile.FavoriteCharacter) ? "Ironclad" : profile.FavoriteCharacter,
        profile.WinRate, TimeSpan.FromMilliseconds(profile.BestTimeMs),
        (profile.RecentMatches ?? Array.Empty<HistoryDto>()).Select(x => new MatchHistoryEntry(
            x.MatchId, ParseKind(x.Kind), (TeamSize)x.TeamSize, x.Victory,
            TimeSpan.FromMilliseconds(x.RunTimeMs), string.IsNullOrEmpty(x.Character) ? "Ironclad" : x.Character,
            x.PlayedAt, x.RatingDelta)).ToArray(),
        local);

    private static QueueKind ParseKind(string value) => value switch { "ranked" => QueueKind.Ranked, "casual" => QueueKind.Casual, _ => QueueKind.Entertainment };
    private static FriendEntry MapSocial(SocialDto x) => new(x.PlayerId, x.DisplayName,
        x.Relationship == "incoming" ? FriendPresence.Request : x.Relationship == "outgoing" ? FriendPresence.RequestSent :
        x.InRace ? FriendPresence.InRace : x.Online ? FriendPresence.Online : FriendPresence.Offline,
        x.InRace ? "race" : x.Online ? "online" : x.Relationship, "", x.Tier);
    private void SetQueue(QueueSnapshot snapshot) { CurrentQueue = snapshot; QueueChanged?.Invoke(snapshot); }

    public async ValueTask DisposeAsync()
    {
        _socketLifetime?.Cancel();
        if (_socket is not null) _socket.Dispose();
        _ticketProvider.Dispose(); _http.Dispose(); _authenticationLock.Dispose(); _socketWriteLock.Dispose(); _integrityLock.Dispose();
        await Task.CompletedTask;
    }

    private sealed class NoOpSessionLauncher : IRaceSessionLauncher { public Task LaunchAsync(QueueRequest request, RaceTeam localTeam, RaceTeam opponentTeam, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed record AuthResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken = "");
    private sealed record CredentialCache(string PlayerId, string RefreshToken);
    private sealed record ClockResponse([property: JsonPropertyName("server_unix_ms")] long ServerUnixMs);
    private sealed record QueueJoinResponse(string State, AssignmentDto? Assignment);
    private sealed record AssignmentDto(string MatchId, string GameId, string GameVersion, string Kind, int TeamSize, string FirstTeamId, string SecondTeamId,
        string[] FirstPlayerIds, string[] SecondPlayerIds, ServerRulesDto Rules, string SessionNonce, long StartedAtMs, bool LegendSeries,
        Dictionary<string, string>? CharacterIds = null, string FirstSteamHostPlayerId = "", string SecondSteamHostPlayerId = "",
        string FirstSteamLobbyId = "", string SecondSteamLobbyId = "");
    private sealed record ServerRulesDto(int TeamSize, string Seed, int Ascension, long TimeLimitMs, int EventSlLimit, int CombatSlLimit, string CharacterId,
        string[] Modifiers, bool RandomSeed = false, bool AllowDuplicateCharacters = true, string CharacterPolicy = "", string TimerKind = "",
        string VictoryRule = "", bool AllowSpectators = false, string Visibility = "", string CoordinationMode = "server");
    private sealed record IntegrityManifestDto(string GameVersion, string ManifestVersion, IntegrityFileDto[] GameFiles, IntegrityFileDto[] AllowedModFiles, string[] AllowedModIds, string Signature);
    private sealed record IntegrityFileDto(string Path, string Sha256, long Size);
    private sealed record IntegrityVerdictDto(bool Accepted, string Code, string Detail);
    private sealed record RoomDto(string Code, string HostPlayerId, ServerRulesDto Rules, RoomMemberDto[] Members, DateTimeOffset CreatedAt,
        string CoordinationMode = "server", string State = "waiting");
    private sealed record RoomMemberDto(string PlayerId, string DisplayName, int Team, bool IsHost, bool IsReady = false, string CharacterId = "Ironclad");
    private sealed record EntertainmentLobbyRequiredDto(RoomDto Room, int Team, string HostPlayerId);
    private sealed record EntertainmentLaunchDto(RoomDto Room, string FirstSteamHostPlayerId, string SecondSteamHostPlayerId,
        string FirstSteamLobbyId, string SecondSteamLobbyId);
    private sealed record ProfileDto(string Id, string DisplayName, RatingDto Solo, RatingDto Team, string FavoriteCharacter = "Ironclad", long BestTimeMs = 0, double WinRate = 0, HistoryDto[]? RecentMatches = null);
    private sealed record RatingDto(string Tier, int Points, int Games, int HiddenRating, int Wins = 0, int Losses = 0, int Division = 4, int LeaderboardRank = 0);
    private sealed record HistoryDto(string MatchId, string Kind, int TeamSize, bool Victory, long RunTimeMs, string Character, DateTimeOffset PlayedAt, int RatingDelta);
    private sealed record LeaderboardDto(int Position, string PlayerId, string DisplayName, string Tier, int Rating, int Wins, int Losses, long BestTimeMs = 0);
    private sealed record SocialDto(string PlayerId, string DisplayName, string Relationship, string Tier, bool Online, bool InRace);
    private sealed record RaceInviteDto(string PlayerId, string DisplayName, string RoomCode = "", string PartyId = "", string Kind = "casual", int TeamSize = 1);
    private sealed record PartyDto(string Id, string LeaderPlayerId, string Kind, int TeamSize, PartyMemberDto[] Members);
    private sealed record PartyMemberDto(string PlayerId, string DisplayName, string CharacterId = "Ironclad");
    private sealed record SettlementDto(string MatchId, string GameId, string WinnerTeamId, string Reason, SettlementSideDto First, SettlementSideDto Second, string AuditDetail, DateTimeOffset CompletedAt, LegendGameDto[]? SeriesGames, Dictionary<string, int>? VisibleRatingDeltas);
    private sealed record LegendGameDto(int GameNumber, string GameId, string CharacterId, string WinnerTeamId, string Reason, long ElapsedMs);
    private sealed record SettlementSideDto(string TeamId, string Outcome, int HighestFloor, long HighestFloorEnteredMs, long? CompletionMs, int RestartCount, int EventSlUsed, int CombatSlUsed);
}
