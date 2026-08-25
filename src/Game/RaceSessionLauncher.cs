using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Logging;
using Sts2SpireRace.Core;

namespace Sts2SpireRace.Game;

public sealed class RaceSessionLauncher : IRaceSessionLauncher, IRaceSteamLobbyCoordinator, IRaceEntertainmentP2PLauncher
{
    private readonly Dictionary<string, NSubmenu> _pendingSteamScreens = new();

    public Task LaunchAsync(QueueRequest request, RaceTeam localTeam, RaceTeam opponentTeam, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task LaunchAsync(MatchAssignment assignment, CancellationToken cancellationToken = default)
    {
        if (NGame.Instance is null)
            throw new InvalidOperationException("The game scene is not available.");
        if (assignment.TeamSize != TeamSize.One)
        {
            await LaunchSteamTeamAsync(assignment, cancellationToken);
            return;
        }
        var character = ModelDb.AllCharacters.FirstOrDefault(x =>
            string.Equals(x.Id.Entry, assignment.SharedCharacterId, StringComparison.OrdinalIgnoreCase));
        if (character is null)
            throw new InvalidOperationException($"Unknown server-selected character: {assignment.SharedCharacterId}");

        RaceActiveSession.Begin(assignment);
        Log.Info($"[SpireRace] Cached active race session {assignment.MatchId}/{assignment.GameId} before run launch.");
        await NGame.Instance.Transition.FadeOut();
        cancellationToken.ThrowIfCancellationRequested();
        var modifiers = ResolveModifiers(assignment.Rules.Modifiers);
        await NGame.Instance.StartNewSingleplayerRun(
            character,
            shouldSave: true,
            ActModel.GetDefaultList(),
            modifiers,
            assignment.Rules.Seed,
            modifiers.Count == 0 ? GameMode.Standard : GameMode.Custom,
            assignment.Rules.Ascension);
    }

    public async Task<ulong> CreateTeamLobbyAsync(MatchAssignment assignment, CancellationToken cancellationToken = default)
    {
        if (assignment.TeamSize == TeamSize.One)
            throw new InvalidOperationException("A Steam cooperative lobby is not used for 1v1.");
        cancellationToken.ThrowIfCancellationRequested();
        var service = new NetHostGameService(PeerVersionInfo.LocalDefault());
        var error = await service.StartSteamHost((int)assignment.TeamSize);
        if (error.HasValue)
            throw new InvalidOperationException($"Steam lobby creation failed: {error.Value.GetReason()}");
        var raw = service.GetRawLobbyIdentifier();
        if (!ulong.TryParse(raw, out var lobbyId) || lobbyId == 0)
        {
            service.Disconnect(NetError.InternalError, true);
            throw new InvalidOperationException("Steam returned an invalid lobby identifier.");
        }
        var game = NGame.Instance ?? throw new InvalidOperationException("The game scene is not available.");
        var mainMenu = game.MainMenu ?? throw new InvalidOperationException("The main menu is not available.");
        NSubmenu screen;
        if (assignment.Kind == QueueKind.Entertainment)
        {
            var custom = mainMenu.SubmenuStack.GetSubmenuType<NCustomRunScreen>();
            custom.InitializeMultiplayerAsHost(service, (int)assignment.TeamSize);
            custom.Lobby.SyncAscensionChange(assignment.Rules.Ascension);
            custom.Lobby.SetSeed(assignment.Rules.RandomSeed ? null : assignment.Rules.Seed);
            custom.Lobby.SetModifiers(ResolveModifiers(assignment.Rules.Modifiers));
            screen = custom;
        }
        else
        {
            var standard = mainMenu.SubmenuStack.GetSubmenuType<NCharacterSelectScreen>();
            standard.InitializeMultiplayerAsHost(service, (int)assignment.TeamSize);
            standard.Lobby.SyncAscensionChange(assignment.Rules.Ascension);
            screen = standard;
        }
        _pendingSteamScreens[assignment.MatchId] = screen;
        return lobbyId;
    }

    public async Task LaunchDirectHostAsync(RaceRuleSet rules, CancellationToken cancellationToken = default)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("The game scene is not available.");
        var mainMenu = game.MainMenu ?? throw new InvalidOperationException("The main menu is not available.");
        var service = new NetHostGameService(PeerVersionInfo.LocalDefault());
        var error = await service.StartSteamHost(Math.Clamp((int)rules.TeamSize * 2, 2, 4));
        if (error.HasValue) throw new InvalidOperationException($"Steam lobby creation failed: {error.Value.GetReason()}");
        cancellationToken.ThrowIfCancellationRequested();
        var screen = mainMenu.SubmenuStack.GetSubmenuType<NCustomRunScreen>();
        screen.InitializeMultiplayerAsHost(service, Math.Clamp((int)rules.TeamSize * 2, 2, 4));
        screen.Lobby.SyncAscensionChange(rules.Ascension);
        screen.Lobby.SetSeed(rules.RandomSeed ? null : rules.Seed);
        screen.Lobby.SetModifiers(ResolveModifiers(rules.Modifiers));
        mainMenu.SubmenuStack.Push(screen);
        Log.Info("[SpireRace] Opened a direct Steam P2P entertainment lobby without race-server match coordination.");
    }

    private async Task LaunchSteamTeamAsync(MatchAssignment assignment, CancellationToken cancellationToken)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("The game scene is not available.");
        var mainMenu = game.MainMenu ?? throw new InvalidOperationException("The main menu is not available.");
        var localPlayer = assignment.LocalTeam.Participants.FirstOrDefault(x => x.IsLocal)
            ?? throw new InvalidOperationException("The local Steam player is not present in the assigned team.");
        var firstTeam = assignment.LocalTeam.Participants.Any(x => x.Id == assignment.FirstSteamHostPlayerId);
        var lobbyText = firstTeam ? assignment.FirstSteamLobbyId : assignment.SecondSteamLobbyId;
        if (!ulong.TryParse(lobbyText, out var lobbyId) || lobbyId == 0)
            throw new InvalidOperationException("The race server did not provide this team's Steam lobby.");

        if (assignment.Kind != QueueKind.Entertainment)
            RaceActiveSession.Begin(assignment);
        game.DebugSeedOverride = assignment.Rules.Seed;
        var isHost = localPlayer.Id == assignment.FirstSteamHostPlayerId || localPlayer.Id == assignment.SecondSteamHostPlayerId;
        if (isHost)
        {
            if (!_pendingSteamScreens.Remove(assignment.MatchId, out var hostScreen))
                throw new InvalidOperationException("The prepared Steam host was not found.");
            mainMenu.SubmenuStack.Push(hostScreen);
        }
        else
        {
            var join = new JoinFlow(new NetClientGameService(PeerVersionInfo.LocalDefault()));
            var result = await join.Begin(SteamClientConnectionInitializer.FromLobby(lobbyId), game.GetTree());
            cancellationToken.ThrowIfCancellationRequested();
            if (result.sessionState != RunSessionState.InLobby || !result.joinResponse.HasValue)
                throw new InvalidOperationException("The assigned Steam lobby is no longer accepting players.");
            if (assignment.Kind == QueueKind.Entertainment)
            {
                var custom = mainMenu.SubmenuStack.GetSubmenuType<NCustomRunScreen>();
                custom.InitializeMultiplayerAsClient(join.NetService, result.joinResponse.Value);
                mainMenu.SubmenuStack.Push(custom);
            }
            else
            {
                var standard = mainMenu.SubmenuStack.GetSubmenuType<NCharacterSelectScreen>();
                standard.InitializeMultiplayerAsClient(join.NetService, result.joinResponse.Value);
                mainMenu.SubmenuStack.Push(standard);
            }
        }
        Log.Info($"[SpireRace] Joined original Steam cooperative lobby {lobbyId} for race {assignment.MatchId}.");
    }

    private static IReadOnlyList<ModifierModel> ResolveModifiers(IEnumerable<string> ids)
    {
        var result = new List<ModifierModel>();
        var canonical = ModelDb.GoodModifiers.Concat(ModelDb.BadModifiers).ToArray();
        foreach (var encoded in ids)
        {
            var pieces = encoded.Split(':', 2);
            var source = canonical.FirstOrDefault(x => x.Id.Entry.Equals(pieces[0], StringComparison.OrdinalIgnoreCase));
            if (source is null)
                continue;
            var modifier = source.ToMutable();
            if (modifier is CharacterCards characterCards)
            {
                if (pieces.Length != 2)
                    continue;
                var extraCharacter = ModelDb.AllCharacters.FirstOrDefault(x => x.Id.Entry.Equals(pieces[1], StringComparison.OrdinalIgnoreCase));
                if (extraCharacter is null)
                    continue;
                characterCards.CharacterModel = extraCharacter.Id;
            }
            result.Add(modifier);
        }
        return result;
    }
}
