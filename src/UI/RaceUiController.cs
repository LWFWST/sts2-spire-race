using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2SpireRace.Core;
using Sts2SpireRace.Game;

namespace Sts2SpireRace.UI;

public sealed partial class RaceUiController : Node
{
    private static bool _startupNavigationConsumed;
    private static bool _startupAuthenticationAttempted;
    private static string? _lastAutoOpenedSettlementKey;
    private NMainMenu _mainMenu = null!;
    private MegaCrit.Sts2.addons.mega_text.MegaLabel? _mainMenuLabel;
    private LocManager.LocaleChangeCallback? _localeCallback;
    private Action<RaceInvite>? _inviteCallback;
    private Action<EntertainmentRoom?>? _roomCallback;

    public IRaceServices Services { get; private set; } = null!;
    public RaceRuleSet EntertainmentRules { get; set; } = RaceRules.EntertainmentDefault();
    public EntertainmentRoom? CurrentEntertainmentRoom { get; set; }
    public bool CanEditEntertainmentRules { get; set; } = true;
    public string LocalPlatformId { get; set; } = string.Empty;
    public int EntertainmentScrollPosition { get; set; }

    public void Configure(NMainMenu mainMenu, MegaCrit.Sts2.addons.mega_text.MegaLabel mainMenuLabel)
    {
        _mainMenu = mainMenu;
        _mainMenuLabel = mainMenuLabel;
        Services = RaceServiceRegistry.Services;
        _localeCallback = RefreshMainMenuLabel;
        LocString.SubscribeToLocaleChange(_localeCallback);
        RefreshMainMenuLabel();
        _inviteCallback = invite => Callable.From(() => OpenConfirm(
            RaceTextCatalog.Get("invite.title"),
            string.IsNullOrWhiteSpace(invite.RoomCode)
                ? RaceTextCatalog.Format("invite.body", invite.DisplayName)
                : RaceTextCatalog.Format("invite.room_body", invite.DisplayName, invite.RoomCode),
            () => AcceptInviteAsync(invite))).CallDeferred();
        Services.InviteReceived += _inviteCallback;
        if (Services is IRaceEntertainmentRoomService rooms)
        {
            _roomCallback = room => Callable.From(() =>
            {
                if (room is null || room.CoordinationMode != EntertainmentCoordinationMode.SteamP2P)
                    return;
                CurrentEntertainmentRoom = room;
                EntertainmentRules = room.Rules;
                if (_mainMenu.SubmenuStack.Peek() is not RacePage)
                    OpenEntertainment();
            }).CallDeferred();
            rooms.RoomChanged += _roomCallback;
        }
        if (!_startupAuthenticationAttempted)
        {
            _startupAuthenticationAttempted = true;
            _ = InitializeAtStartupAsync();
        }
        var performStartupNavigation = !_startupNavigationConsumed;
        _startupNavigationConsumed = true;
        if (performStartupNavigation && Services.CurrentQueue.State is QueueState.Draft)
            Callable.From(OpenQueue).CallDeferred();
        var settlementKey = Services.CurrentQueue.State switch
        {
            QueueState.FinishPending when Services is IRaceMatchService { CurrentMatch: { } match } => $"pending:{match.MatchId}",
            QueueState.Completed when Services.CurrentQueue.Result is { } result => $"completed:{result.MatchId}",
            _ => null
        };
        if (settlementKey is not null && settlementKey != _lastAutoOpenedSettlementKey)
        {
            _lastAutoOpenedSettlementKey = settlementKey;
            Callable.From(OpenQueue).CallDeferred();
        }
        if (!performStartupNavigation)
            return;
        if (OS.GetCmdlineArgs().Contains("--spire-race-smoke-test"))
            Callable.From(() => { _ = RunSmokeTestAsync(); }).CallDeferred();
        else if (OS.GetCmdlineArgs().Contains("--spire-race-preview-leaderboard"))
            Callable.From(OpenLeaderboard).CallDeferred();
        else if (OS.GetCmdlineArgs().Contains("--spire-race-preview-titles"))
            Callable.From(OpenTitles).CallDeferred();
        else if (OS.GetCmdlineArgs().Contains("--spire-race-preview-entertainment"))
        {
            EntertainmentRules = EntertainmentRules with { Ascension = RaceRules.MaxAscension };
            Callable.From(OpenEntertainment).CallDeferred();
        }
        else if (OS.GetCmdlineArgs().Contains("--spire-race-preview"))
            Callable.From(OpenHub).CallDeferred();
    }

    private async Task AcceptInviteAsync(RaceInvite invite)
    {
        await Services.RespondToInviteAsync(invite.PlayerId, true);
        if (!string.IsNullOrWhiteSpace(invite.RoomCode))
            Callable.From(OpenEntertainment).CallDeferred();
        else if (!string.IsNullOrWhiteSpace(invite.PartyId) && Services is IRacePartyService parties)
        {
            for (var attempt = 0; attempt < 30 && parties.CurrentParty?.Id != invite.PartyId; attempt++)
                await Task.Delay(100);
            if (parties.CurrentParty?.Id == invite.PartyId)
                Callable.From(() => OpenLobby(invite.PartyKind, invite.PartyTeamSize)).CallDeferred();
        }
    }

    public override void _ExitTree()
    {
        if (_localeCallback is not null)
            LocString.UnsubscribeToLocaleChange(_localeCallback);
        if (_inviteCallback is not null)
            Services.InviteReceived -= _inviteCallback;
        if (_roomCallback is not null && Services is IRaceEntertainmentRoomService rooms)
            rooms.RoomChanged -= _roomCallback;
    }

    public void OpenHub() => Open(RaceTextCatalog.Get("hub.title"), RaceScreens.BuildHub);

    public void Open(string title, Action<RacePage> builder)
    {
        var page = new RacePage().Configure(this, title, builder);
        page.Name = $"SpireRacePage_{Time.GetTicksMsec()}";
        page.Visible = false;
        _mainMenu.SubmenuStack.AddChild(page);
        _mainMenu.SubmenuStack.Push(page);
    }

    public void OpenModeSelection(QueueKind kind, bool teamOnly = false) =>
        _ = OpenAuthenticatedAsync(() => OpenModeSelectionPage(kind, teamOnly));

    private void OpenModeSelectionPage(QueueKind kind, bool teamOnly) =>
        Open(RaceTextCatalog.Get(kind == QueueKind.Ranked ? "mode.title.ranked" : "mode.title.casual"),
            page => RaceScreens.BuildModeSelection(page, kind, teamOnly));

    public void OpenLobby(QueueKind kind, TeamSize size, RaceRuleSet? rules = null) =>
        Open(RaceTextCatalog.Get("lobby.title"), page => RaceScreens.BuildLobby(page, kind, size, rules ?? RaceRules.CompetitiveDefault(size)));

    public void OpenQueue() => Open(RaceTextCatalog.Get("queue.title"), RaceScreens.BuildQueue);
    public void OpenRanked() => _ = OpenAuthenticatedAsync(() => Open(RaceTextCatalog.Get("rank.title"), RaceScreens.BuildRanked));
    public void OpenEntertainment() => Open(RaceTextCatalog.Get("fun.title"), RaceScreens.BuildEntertainment);
    public void OpenProfile(string? playerId = null) => Open(RaceTextCatalog.Get("profile.title"), page => RaceScreens.BuildProfile(page, playerId));
    public void OpenFriends() => Open(RaceTextCatalog.Get("friends.title"), RaceScreens.BuildFriends);
    public void OpenLeaderboard() => Open(RaceTextCatalog.Get("leaderboard.title"), RaceScreens.BuildLeaderboard);
    public void OpenTitles() => OpenComingSoon(RaceTextCatalog.Get("titles.title"), RaceTextCatalog.Get("coming_soon.titles"));
    public void OpenActivity() => OpenComingSoon(RaceTextCatalog.Get("activity.title"), RaceTextCatalog.Get("coming_soon.activity"));
    public void OpenSettings() => Open(RaceTextCatalog.Get("settings.title"), RaceScreens.BuildSettings);
    private void OpenComingSoon(string title, string body) =>
        Open(title, page => RaceScreens.BuildComingSoon(page, body));
    public void OpenDetails(string title, params string[] paragraphs) =>
        Open(title, page => RaceScreens.BuildDetails(page, paragraphs));
    public void OpenConfirm(string title, string body, Func<Task> confirmed) =>
        Open(title, page => RaceScreens.BuildConfirm(page, body, confirmed));
    public void CloseTop() => _mainMenu.SubmenuStack.Pop();

    public void ShowServerNotice(Exception exception, Uri? serverUri = null)
    {
        var uri = serverUri ?? RaceRuntimeInfo.ServerUri;
        var betaNotice = RaceTextCatalog.Get("auth.beta_access_required");
        var body = RaceRuntimeInfo.IsOfficialServer(uri) && exception.Message == betaNotice
            ? betaNotice
            : RaceTextCatalog.Format("auth.steam_login_failed", exception.Message);
        OpenDetails(RaceTextCatalog.Get("auth.notice_title"), body);
    }

    private async Task InitializeAtStartupAsync()
    {
        try
        {
            var identity = await Services.IdentityProvider.GetLocalIdentityAsync();
            if (identity.PlatformId == 0 && !RaceRuntimeInfo.DevelopmentAuthentication)
                throw new InvalidOperationException(RaceTextCatalog.Get("auth.steam_required"));
            if (Services.ConfiguredServerUri is null)
                return;
            await Services.AuthenticateAsync();
            if (Services is RemoteRaceServices remote)
                await remote.WarmupAsync();
        }
        catch (Exception exception)
        {
            Callable.From(() => ShowServerNotice(exception)).CallDeferred();
        }
    }

    private async Task OpenAuthenticatedAsync(Action open)
    {
        try
        {
            await Services.AuthenticateAsync();
            Callable.From(open).CallDeferred();
        }
        catch (Exception exception)
        {
            Callable.From(() => ShowServerNotice(exception)).CallDeferred();
        }
    }

    private void RefreshMainMenuLabel()
    {
        if (_mainMenuLabel is not null && GodotObject.IsInstanceValid(_mainMenuLabel))
            _mainMenuLabel.SetTextAutoSize(RaceTextCatalog.Get("main_menu.race"));
    }

    private async Task RunSmokeTestAsync()
    {
        try
        {
            await FramesAsync(3);
            await SmokePageAsync("hub", OpenHub);
            await SmokePageAsync("casual mode selection", () => OpenModeSelection(QueueKind.Casual));
            await SmokePageAsync("ranked team selection", () => OpenModeSelection(QueueKind.Ranked, teamOnly: true));
            foreach (var size in Enum.GetValues<TeamSize>())
                await SmokePageAsync($"{(int)size}v{(int)size} lobby", () => OpenLobby(QueueKind.Casual, size));
            await SmokePageAsync("ranked", OpenRanked);
            await SmokePageAsync("entertainment rules", OpenEntertainment);
            await SmokePageAsync("profile", () => OpenProfile());
            await SmokePageAsync("friends", OpenFriends);
            await SmokePageAsync("leaderboard", OpenLeaderboard);
            await SmokePageAsync("titles", OpenTitles);
            await SmokePageAsync("activity", OpenActivity);

            await Services.JoinQueueAsync(new QueueRequest(
                QueueKind.Casual,
                TeamSize.Four,
                null,
                RaceRules.CompetitiveDefault(TeamSize.Four)));
            OpenQueue();
            await Task.Delay(1600);
            await Services.ConfirmMatchAsync(true);
            await FramesAsync(2);
            await Services.SetLocalTeamReadyAsync(true);
            await Task.Delay(1100);
            if (Services.CurrentQueue.State != QueueState.Completed)
                throw new InvalidOperationException($"Queue smoke flow ended in {Services.CurrentQueue.State}.");
            _mainMenu.SubmenuStack.Pop();
            await FramesAsync(2);

            Log.Info("[SpireRace] SMOKE TEST PASSED: all pages and the 4v4 queue flow rendered.");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            Log.Error($"[SpireRace] SMOKE TEST FAILED: {exception}");
            GetTree().Quit(2);
        }
    }

    private async Task SmokePageAsync(string name, Action open)
    {
        open();
        await FramesAsync(4);
        if (_mainMenu.SubmenuStack.Peek() is not RacePage)
            throw new InvalidOperationException($"{name} did not enter the native submenu stack.");
        Log.Info($"[SpireRace] Smoke rendered {name}.");
        _mainMenu.SubmenuStack.Pop();
        await FramesAsync(2);
    }

    private async Task FramesAsync(int count)
    {
        for (var i = 0; i < count; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
