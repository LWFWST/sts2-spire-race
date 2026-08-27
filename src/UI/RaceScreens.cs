using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using Sts2SpireRace.Core;
using Sts2SpireRace.Game;
using Sts2SpireRace.Replay;

namespace Sts2SpireRace.UI;

public static class RaceScreens
{
    private static readonly Color BluePanel = new(0.10f, 0.29f, 0.36f, 0.90f);
    private static readonly Color RedPanel = new(0.38f, 0.18f, 0.20f, 0.90f);
    private static readonly Color DarkPanel = new(0.10f, 0.12f, 0.16f, 0.92f);
    private static readonly Color GoldPanel = new(0.36f, 0.29f, 0.13f, 0.90f);

    public static void BuildHub(RacePage page)
    {
        SetServiceStatus(page);
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, 48), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var subtitle = RaceUiAssets.Label(RaceTextCatalog.Get("hub.subtitle"), 24, StsColors.cream, HorizontalAlignment.Center);
        subtitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(subtitle);
        header.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("spectate.title"), page.Controller.OpenSpectate, 18, new Vector2(190, 46)));
        page.Content.AddChild(header);

        var cards = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        cards.AddThemeConstantOverride("separation", 54);
        page.Content.AddChild(cards);

        var casual = RaceUiAssets.ParchmentCard(
            RaceTextCatalog.Get("hub.casual.title"), RaceTextCatalog.Get("hub.casual.description"),
            RaceUiAssets.StandardIcon, new Color("b79b6d"), () => page.Controller.OpenModeSelection(QueueKind.Casual));
        var ranked = RaceUiAssets.ParchmentCard(
            RaceTextCatalog.Get("hub.ranked.title"), RaceTextCatalog.Get("hub.ranked.description"),
            RaceUiAssets.DailyIcon, new Color("5786a3"), page.Controller.OpenRanked);
        var fun = RaceUiAssets.ParchmentCard(
            RaceTextCatalog.Get("hub.fun.title"), RaceTextCatalog.Get("hub.fun.description"),
            RaceUiAssets.CustomIcon, new Color("9a6774"), page.Controller.OpenEntertainment);
        cards.AddChild(casual);
        cards.AddChild(ranked);
        cards.AddChild(fun);
        page.SetInitialFocus(casual);

        var lower = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            CustomMinimumSize = new Vector2(0, 116),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        lower.AddThemeConstantOverride("separation", 16);
        page.Content.AddChild(lower);
        lower.AddChild(RaceUiAssets.ShortCard(RaceTextCatalog.Get("hub.profile"), RaceUiAssets.ProfileIcon, new Color("315d69"), () => page.Controller.OpenProfile()));
        lower.AddChild(RaceUiAssets.ShortCard(RaceTextCatalog.Get("hub.friends"), RaceUiAssets.FriendsIcon, new Color("476a56"), page.Controller.OpenFriends));
        lower.AddChild(RaceUiAssets.ShortCard(RaceTextCatalog.Get("hub.leaderboard"), RaceUiAssets.LeaderboardIcon, new Color("715c2d"), page.Controller.OpenLeaderboard));
        lower.AddChild(RaceUiAssets.ShortCard(RaceTextCatalog.Get("hub.titles"), RaceUiAssets.TitleIcon, new Color("6d493d"), page.Controller.OpenTitles));
        lower.AddChild(RaceUiAssets.ShortCard(RaceTextCatalog.Get("hub.activity"), RaceUiAssets.ActivityIcon, new Color("4f4f79"), page.Controller.OpenActivity));
        lower.AddChild(RaceUiAssets.ShortCard(RaceTextCatalog.Get("hub.settings"), RaceUiAssets.ProfileIcon, new Color("33415c"), page.Controller.OpenSettings));
    }

    public static void BuildModeSelection(RacePage page, QueueKind kind, bool teamOnly)
    {
        page.Status.SetTextAutoSize(RaceTextCatalog.Get("lobby.rule"));
        var row = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 24);
        page.Content.AddChild(row);

        foreach (var size in Enum.GetValues<TeamSize>().Where(size => !teamOnly || size != TeamSize.One))
        {
            var section = new VBoxContainer { CustomMinimumSize = new Vector2(280, 430), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            section.AddThemeConstantOverride("separation", 16);
            row.AddChild(section);
            var button = RaceUiAssets.Button(RaceTextCatalog.Get($"mode.{(int)size}v{(int)size}"),
                () => page.Controller.OpenLobby(kind, size), 34, new Vector2(270, 126));
            button.NormalTint = size == TeamSize.One ? new Color("486c79") : new Color("765e35");
            button.FocusTint = size == TeamSize.One ? new Color("6f9eaa") : new Color("a88748");
            section.AddChild(button);
            section.AddChild(RaceUiAssets.Label(
                RaceTextCatalog.Get(size == TeamSize.One ? "mode.solo.description" : "mode.team.description"),
                23, StsColors.cream, HorizontalAlignment.Center));
            section.AddChild(RaceUiAssets.Label(
                size == TeamSize.One ? TierLabel(RankedPool.Solo) : TierLabel(RankedPool.Team),
                20, StsColors.gold, HorizontalAlignment.Center));
            page.SetInitialFocus(button);
        }
    }

    public static void BuildLobby(RacePage page, QueueKind kind, TeamSize size, RaceRuleSet rules)
    {
        page.Status.SetTextAutoSize(RaceTextCatalog.Get("lobby.rule"));
        _ = LoadLobbyAsync(page, kind, size, rules);
    }

    private sealed class LobbyState(QueueKind kind, TeamSize size, RaceRuleSet rules, string localName, string localId)
    {
        public QueueKind Kind { get; } = kind;
        public TeamSize Size { get; } = size;
        public RaceRuleSet Rules { get; set; } = rules;
        public string LocalName { get; } = localName;
        public string LocalId { get; } = localId;
    }

    private static async Task LoadLobbyAsync(RacePage page, QueueKind kind, TeamSize size, RaceRuleSet rules)
    {
        var identity = await page.Controller.Services.IdentityProvider.GetLocalIdentityAsync();
        var state = new LobbyState(kind, size, rules, identity.DisplayName, identity.PlatformId.ToString());
        if (page.Controller.Services is IRacePartyService parties)
        {
            void Changed(RaceParty? _) => Defer(page, () => RenderLobby(page, state));
            parties.PartyChanged += Changed;
            page.AddCleanup(() =>
            {
                parties.PartyChanged -= Changed;
                if (parties.CurrentParty is not null)
                    _ = parties.LeavePartyAsync();
            });
            try
            {
                await parties.OpenPartyLobbyAsync(kind, size);
            }
            catch (Exception exception)
            {
                Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
            }
        }
        Defer(page, () => RenderLobby(page, state));
    }

    private static void RenderLobby(RacePage page, LobbyState state)
    {
        if (!IsAlive(page))
            return;
        page.ClearContent();
        var party = (page.Controller.Services as IRacePartyService)?.CurrentParty;
        var selectedCharacter = party?.Members.FirstOrDefault(x => x.IsLocal)?.CharacterId;
        if (selectedCharacter is null || !IsPlayableCharacter(selectedCharacter))
            selectedCharacter = IsPlayableCharacter(state.Rules.CharacterPolicy) ? state.Rules.CharacterPolicy : "Ironclad";
        var summary = RaceUiAssets.Label(
            $"{RaceTextCatalog.Get($"mode.{(int)state.Size}v{(int)state.Size}")}   ·   {QueueKindName(state.Kind)}   ·   {RaceTextCatalog.Get("lobby.random_seed")}",
            23, StsColors.gold, HorizontalAlignment.Center);
        summary.CustomMinimumSize = new Vector2(0, 44);
        page.Content.AddChild(summary);

        var localNames = party is { TeamSize: var partySize } && partySize == state.Size
            ? party.Members.Select(x => $"{x.DisplayName}  ·  {CharacterName(x.CharacterId)}").Concat(Enumerable.Repeat(RaceTextCatalog.Get("lobby.empty"), (int)state.Size)).Take((int)state.Size).ToArray()
            : new[] { state.LocalName }.Concat(Enumerable.Repeat(RaceTextCatalog.Get("lobby.empty"), (int)state.Size)).Take((int)state.Size).ToArray();
        page.Content.AddChild(TeamPanel(RaceTextCatalog.Get("lobby.your_team"), localNames, true));
        var occupied = party?.Members.Count ?? 1;
        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Format("lobby.party_members", occupied, (int)state.Size), 19,
            occupied == (int)state.Size ? StsColors.gold : StsColors.lightGray, HorizontalAlignment.Center));

        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("lobby.choose_character"), 22, StsColors.gold, HorizontalAlignment.Center, true));
        var characters = ActionRow();
        page.Content.AddChild(characters);
        foreach (var character in PlayableCharacters)
        {
            var captured = character;
            var button = RaceUiAssets.Button(
                (captured == selectedCharacter ? "◆ " : string.Empty) + CharacterName(captured),
                () =>
                {
                    state.Rules = state.Rules with { CharacterPolicy = captured };
                    if (page.Controller.Services is IRacePartyService partyService)
                        _ = partyService.SetPartyCharacterAsync(captured);
                    RenderLobby(page, state);
                },
                19,
                new Vector2(190, 54));
            button.NormalTint = captured == selectedCharacter ? new Color("765e35") : new Color("315d69");
            characters.AddChild(button);
            if (captured == selectedCharacter) page.SetInitialFocus(button);
        }

        var actions = ActionRow();
        page.Content.AddChild(actions);
        var invite = RaceUiAssets.Button(RaceTextCatalog.Get("common.invite"),
            () => _ = ShowInvitePickerAsync(page, state));
        var queue = RaceUiAssets.Button(RaceTextCatalog.Get("lobby.queue"),
            () =>
            {
                _ = BeginQueueAsync(page, new QueueRequest(state.Kind, state.Size, state.Kind == QueueKind.Ranked ? RaceRules.PoolFor(state.Size) : null,
                    state.Rules with { CharacterPolicy = selectedCharacter }, selectedCharacter));
            },
            25, new Vector2(250, 66));
        queue.SetEnabled(party is null || party.LeaderPlayerId == state.LocalId);
        queue.NormalTint = new Color("7b632c");
        queue.FocusTint = new Color("b59643");
        actions.AddChild(invite);
        actions.AddChild(queue);
        page.SetInitialFocus(invite);
    }

    private static async Task BeginQueueAsync(RacePage page, QueueRequest request)
    {
        Defer(page, page.Controller.OpenQueue);
        try
        {
            await page.Controller.Services.JoinQueueAsync(request);
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    private static async Task ShowInvitePickerAsync(RacePage page, LobbyState state)
    {
        try
        {
            var friends = await page.Controller.Services.GetFriendsAsync();
            Defer(page, () =>
            {
                if (!IsAlive(page)) return;
                page.ClearContent();
                page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("lobby.invite_friend"), 30, StsColors.gold, HorizontalAlignment.Center, true));
                var list = ScrollList(page.Content);
                var available = friends.Where(x => x.Presence is FriendPresence.Online or FriendPresence.InRace or FriendPresence.Offline).ToArray();
                foreach (var friend in available)
                {
                    var captured = friend;
                    var row = OptionRow($"{captured.DisplayName}   ·   {TierName(captured.RankTier)}");
                    var invite = RaceUiAssets.Button(RaceTextCatalog.Get("common.invite"), () =>
                    {
                        _ = page.Controller.Services.InviteAsync(captured.Id);
                        RenderLobby(page, state);
                        page.Status.SetTextAutoSize(RaceTextCatalog.Format("lobby.invited_friend", captured.DisplayName));
                    }, 20, new Vector2(210, 50));
                    row.AddChild(invite);
                    list.AddChild(row);
                    page.SetInitialFocus(invite);
                }
                if (available.Length == 0)
                    list.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("friends.empty"), 22, StsColors.lightGray, HorizontalAlignment.Center));
                var back = RaceUiAssets.Button(RaceTextCatalog.Get("common.back"), () => RenderLobby(page, state));
                var actions = ActionRow(); actions.AddChild(back); page.Content.AddChild(actions);
            });
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    public static void BuildQueue(RacePage page)
    {
        void Changed(QueueSnapshot snapshot) => Defer(page, () => RenderQueue(page, snapshot));
        page.Controller.Services.QueueChanged += Changed;
        IRaceMatchService? matchService = page.Controller.Services as IRaceMatchService;
        void DraftChanged(LegendDraftPrompt? _) => Defer(page, () => RenderQueue(page, page.Controller.Services.CurrentQueue));
        if (matchService is not null) matchService.LegendDraftChanged += DraftChanged;
        page.AddCleanup(() =>
        {
            page.Controller.Services.QueueChanged -= Changed;
            if (matchService is not null) matchService.LegendDraftChanged -= DraftChanged;
            if (page.Controller.Services.CurrentQueue.State is QueueState.Searching or QueueState.MatchFound or QueueState.ReadyCheck)
                _ = page.Controller.Services.CancelQueueAsync();
        });
        RenderQueue(page, page.Controller.Services.CurrentQueue);
    }

    private static void RenderQueue(RacePage page, QueueSnapshot snapshot)
    {
        if (!IsAlive(page))
            return;
        page.ClearContent();
        page.SetBackVisible(snapshot.State is QueueState.Idle or QueueState.Searching or QueueState.Completed or QueueState.FinishPending);
        SetServiceStatus(page);
        switch (snapshot.State)
        {
            case QueueState.Searching:
                BuildCenteredState(page, RaceTextCatalog.Get("queue.searching"), "[  ·  ·  ·  ]",
                    RaceTextCatalog.Get("common.cancel"), () => _ = page.Controller.Services.CancelQueueAsync());
                break;
            case QueueState.MatchFound:
                RenderReadyCheck(page, snapshot);
                break;
            case QueueState.ReadyCheck:
                RenderReadyCheck(page, snapshot);
                break;
            case QueueState.Lobby:
                RenderMatchedLobby(page, snapshot);
                break;
            case QueueState.Draft:
                RenderLegendDraft(page, page.Controller.Services as IRaceMatchService);
                break;
            case QueueState.Starting:
                BuildCenteredState(page, RaceTextCatalog.Get("queue.starting"), "▲", null, null);
                break;
            case QueueState.FinishPending:
                RenderFinishPending(page);
                break;
            case QueueState.Completed:
                RenderResult(page, snapshot.Result!);
                break;
            default:
                var stateText = snapshot.Detail switch
                {
                    "declined" => RaceTextCatalog.Get("queue.declined"),
                    "cancelled" or "" => RaceTextCatalog.Get("queue.cancelled"),
                    "connection_lost" => RaceTextCatalog.Get("queue.connection_lost"),
                    "opponent_disconnected" => RaceTextCatalog.Get("queue.opponent_disconnected"),
                    "connection_timeout" => RaceTextCatalog.Get("queue.connection_timeout"),
                    "launch_failed" => RaceTextCatalog.Get("queue.launch_failed"),
                    _ => RaceTextCatalog.Format("queue.failed", snapshot.Detail)
                };
                BuildCenteredState(page,
                    stateText, "—", RaceTextCatalog.Get("common.back"), page.Controller.CloseTop);
                break;
        }
    }

    private static void RenderLegendDraft(RacePage page, IRaceMatchService? service)
    {
        var entertainmentDraft = service?.CurrentMatch?.Kind == QueueKind.Entertainment ||
            page.Controller.CurrentEntertainmentRoom?.Rules is { BestOf: 3 };
        if (service?.CurrentLegendDraft is not { } prompt)
        {
            BuildCenteredState(page, RaceTextCatalog.Get(entertainmentDraft ? "fun.bo3.waiting" : "legend.waiting"), "◆", null, null);
            return;
        }
        page.Content.AddChild(RaceUiAssets.Label(
            RaceTextCatalog.Get(prompt.IsBanPhase
                ? entertainmentDraft ? "fun.bo3.ban.title" : "legend.ban.title"
                : "legend.pick.title"),
            37, StsColors.gold, HorizontalAlignment.Center, true));
        page.Content.AddChild(RaceUiAssets.Label(
            RaceTextCatalog.Get(prompt.IsBanPhase ? "legend.ban.body" : "legend.pick.body"),
            21, StsColors.cream, HorizontalAlignment.Center));
        var selected = new List<string>();
        var characters = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        characters.AddThemeConstantOverride("separation", 12);
        page.Content.AddChild(characters);
        RaceTextureButton? submit = null;
        foreach (var character in prompt.AvailableCharacters)
        {
            var captured = character;
            var button = RaceUiAssets.Button(CharacterName(character), () =>
            {
                if (!prompt.IsBanPhase)
                {
                    if (prompt.IsLocalSelector) _ = SelectLegendCharacterAsync(page, service, captured);
                    return;
                }
                if (!selected.Remove(captured) && selected.Count < 2) selected.Add(captured);
                submit?.SetEnabled(selected.Count == 2);
                page.Status.SetTextAutoSize(RaceTextCatalog.Format("legend.ban.selected", selected.Count));
            }, 21, new Vector2(210, 76));
            button.SetEnabled(prompt.IsBanPhase || prompt.IsLocalSelector);
            characters.AddChild(button);
            page.SetInitialFocus(button);
        }
        if (prompt.IsBanPhase)
        {
            var actions = ActionRow();
            submit = RaceUiAssets.Button(RaceTextCatalog.Get("legend.ban.submit"), () =>
            {
                if (selected.Count == 2) _ = SubmitLegendBansAsync(page, service, selected[0], selected[1]);
            }, 23, new Vector2(260, 64));
            submit.SetEnabled(false); actions.AddChild(submit); page.Content.AddChild(actions);
        }
        else if (!prompt.IsLocalSelector)
            page.Status.SetTextAutoSize(RaceTextCatalog.Get("legend.pick.waiting"));
    }

    private static async Task SubmitLegendBansAsync(RacePage page, IRaceMatchService service, string first, string second)
    {
        try
        {
            await service.SubmitLegendBansAsync(first, second);
            Defer(page, () => page.Status.SetTextAutoSize(RaceTextCatalog.Get("legend.ban.waiting")));
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    private static async Task SelectLegendCharacterAsync(RacePage page, IRaceMatchService service, string character)
    {
        try { await service.SelectLegendCharacterAsync(character); }
        catch (Exception exception) { Defer(page, () => page.Status.SetTextAutoSize(exception.Message)); }
    }

    private static void RenderReadyCheck(RacePage page, QueueSnapshot snapshot)
    {
        var title = RaceUiAssets.Label(RaceTextCatalog.Get("queue.ready_check"), 40, StsColors.gold, HorizontalAlignment.Center, true);
        title.CustomMinimumSize = new Vector2(0, 90);
        page.Content.AddChild(title);
        var teams = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        teams.AddThemeConstantOverride("separation", 30);
        teams.AddChild(TeamPanel(RaceTextCatalog.Get("lobby.your_team"), snapshot.LocalTeam!.Participants.Select(x => x.DisplayName), true));
        page.Content.AddChild(teams);
        var actions = ActionRow();
        var accept = RaceUiAssets.Button(RaceTextCatalog.Get("queue.accept"), () => _ = page.Controller.Services.ConfirmMatchAsync(true), 28);
        accept.NormalTint = new Color("47724c");
        actions.AddChild(accept);
        page.Content.AddChild(actions);
        page.SetInitialFocus(accept);
    }

    private static void RenderMatchedLobby(RacePage page, QueueSnapshot snapshot)
    {
        var teams = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        teams.AddThemeConstantOverride("separation", 30);
        teams.AddChild(TeamPanel(RaceTextCatalog.Get("lobby.your_team"), snapshot.LocalTeam!.Participants.Select(x => $"{x.DisplayName}  {(x.IsReady ? "✓" : "…")}"), true));
        page.Content.AddChild(teams);
        var actions = ActionRow();
        var ready = RaceUiAssets.Button(RaceTextCatalog.Get("queue.team_ready"), () => _ = page.Controller.Services.SetLocalTeamReadyAsync(true), 28, new Vector2(270, 70));
        ready.NormalTint = new Color("47724c");
        actions.AddChild(ready);
        page.Content.AddChild(actions);
        page.SetInitialFocus(ready);
    }

    private static void RenderResult(RacePage page, RaceResult result)
    {
        page.Content.AddChild(RaceUiAssets.Label(
            RaceTextCatalog.Get(result.Victory ? "result.victory" : "result.defeat"),
            58, result.Victory ? StsColors.gold : new Color("cf6a70"), HorizontalAlignment.Center, true));
        if (result.Settlement is { } settlement)
            page.Content.AddChild(RaceUiAssets.Label(
                $"{RaceTextCatalog.Get("result.reason")}：{SettlementReason(settlement.Reason)}",
                23, StsColors.cream, HorizontalAlignment.Center));
        var teams = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        teams.AddThemeConstantOverride("separation", 36);
        teams.AddChild(ResultTeamPanel(RaceTextCatalog.Get("result.local_time"), result.LocalTeam, true, result.Settlement?.Local));
        teams.AddChild(ResultTeamPanel(RaceTextCatalog.Get("result.enemy_time"), result.OpponentTeam, false, result.Settlement?.Opponent));
        page.Content.AddChild(teams);
        if (result.RatingDelta != 0)
            page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Format("result.rating", result.RatingDelta), 26, StsColors.gold, HorizontalAlignment.Center));
        if (result.Settlement is { SeriesGames.Count: > 0 } series)
        {
            var localWins = series.SeriesGames.Count(x => x.WinnerTeamId == series.Local.TeamId);
            var opponentWins = series.SeriesGames.Count - localWins;
            var line = string.Join("     ", series.SeriesGames.Select(x =>
                $"G{x.GameNumber} {(x.WinnerTeamId == series.Local.TeamId ? RaceTextCatalog.Get("result.local_side") : RaceTextCatalog.Get("result.opponent_side"))}" +
                (string.IsNullOrWhiteSpace(x.CharacterId) ? string.Empty : $"  {CharacterName(x.CharacterId)}") +
                $"  {RaceRules.FormatElapsed(x.ElapsedMilliseconds)}"));
            page.Content.AddChild(RaceUiAssets.Label($"BO3  {localWins} : {opponentWins}     {line}", 19, StsColors.cream, HorizontalAlignment.Center));
        }
        var actions = ActionRow();
        var again = RaceUiAssets.Button(RaceTextCatalog.Get("result.rematch"), () =>
        {
            _ = page.Controller.Services.CancelQueueAsync();
            if (page.Controller.Services.CurrentQueue.Request?.Kind == QueueKind.Entertainment)
                page.Controller.OpenEntertainment();
            else
                page.Controller.OpenModeSelection(result.LocalTeam.Participants.Count == 1 ? QueueKind.Ranked : QueueKind.Casual);
        });
        actions.AddChild(again);
        page.Content.AddChild(actions);
        page.SetInitialFocus(again);
    }

    private static void RenderFinishPending(RacePage page)
    {
        page.Content.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        page.Content.AddChild(RaceUiAssets.Label("◈", 76, StsColors.gold, HorizontalAlignment.Center, true));
        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("queue.finished_pending.title"), 40, StsColors.cream, HorizontalAlignment.Center, true));
        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("queue.finished_pending.body"), 22, StsColors.lightGray, HorizontalAlignment.Center));
        page.Content.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        page.Status.SetTextAutoSize(RaceTextCatalog.Get("queue.finished_pending.status"));
    }

    public static void BuildRanked(RacePage page)
    {
        SetServiceStatus(page);
        _ = LoadRankedAsync(page);
    }

    private static async Task LoadRankedAsync(RacePage page)
    {
        var profile = await page.Controller.Services.GetLocalProfileAsync();
        Defer(page, () =>
        {
            if (!IsAlive(page)) return;
            page.ClearContent();
            var columns = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            columns.AddThemeConstantOverride("separation", 34);
            page.Content.AddChild(columns);
            columns.AddChild(RankPanel(profile.SoloRank, () => page.Controller.OpenLobby(QueueKind.Ranked, TeamSize.One), RaceTextCatalog.Get("rank.start_solo")));
            columns.AddChild(RankPanel(profile.TeamRank, () => page.Controller.OpenModeSelection(QueueKind.Ranked, teamOnly: true), RaceTextCatalog.Get("rank.start_team")));
            var rewards = RaceUiAssets.PanelSection(columns, GoldPanel, 22, 14);
            rewards.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("rank.rewards"), 31, StsColors.gold, HorizontalAlignment.Center, true));
            foreach (var reward in new[] { "Bronze · Season Mark", "Gold · Rank Banner", "Diamond · Title Seal", "Legend · Spire Crown" })
                rewards.AddChild(RaceUiAssets.Label("◆  " + LocalizeTierWords(reward), 22, StsColors.cream));
            page.SetInitialFocus(FindDescendant<RaceTextureButton>(columns.GetChild(0))
                ?? throw new InvalidOperationException("Rank action button was not created."));
        });
    }

    public static void BuildEntertainment(RacePage page)
    {
        SetServiceStatus(page);
        if (page.Controller.Services is IRaceEntertainmentRoomService rooms)
        {
            page.Controller.CurrentEntertainmentRoom = rooms.CurrentRoom;
            void Changed(EntertainmentRoom? room) => Defer(page, () =>
            {
                page.Controller.CurrentEntertainmentRoom = room;
                if (room is not null) page.Controller.EntertainmentRules = room.Rules;
                RenderEntertainment(page, page.Controller.EntertainmentScrollPosition);
            });
            void Exited(string reason) => Defer(page, () =>
            {
                page.Controller.CurrentEntertainmentRoom = null;
                page.Controller.CanEditEntertainmentRules = true;
                page.Controller.EntertainmentScrollPosition = 0;
                RenderEntertainment(page);
                if (reason == "host_closed")
                    page.Status.SetTextAutoSize(RaceTextCatalog.Get("fun.room_closed_by_host"));
            });
            rooms.RoomChanged += Changed;
            rooms.RoomExited += Exited;
            page.AddCleanup(() =>
            {
                rooms.RoomChanged -= Changed;
                rooms.RoomExited -= Exited;
                if (rooms.CurrentRoom is { State: "waiting" })
                    _ = rooms.LeaveRoomAsync();
            });
        }
        _ = ResolveEntertainmentHostAsync(page);
        RenderEntertainment(page);
    }

    private static async Task ResolveEntertainmentHostAsync(RacePage page)
    {
        var identity = await page.Controller.Services.IdentityProvider.GetLocalIdentityAsync();
        Defer(page, () =>
        {
            page.Controller.LocalPlatformId = identity.PlatformId.ToString();
            page.Controller.CanEditEntertainmentRules = page.Controller.CurrentEntertainmentRoom is null ||
                page.Controller.CurrentEntertainmentRoom.HostPlayerId == identity.PlatformId.ToString();
            RenderEntertainment(page, page.Controller.EntertainmentScrollPosition);
        });
    }

    private static void RenderEntertainment(RacePage page, int restoreScroll = 0)
    {
        if (!IsAlive(page)) return;
        page.ClearContent();
        var rules = RaceRules.NormalizeEntertainment(page.Controller.EntertainmentRules);
        var room = page.Controller.CurrentEntertainmentRoom;
        var canEdit = room is null || page.Controller.CanEditEntertainmentRules;

        if (room is not null)
        {
            var localSpectator = room.Spectators?.FirstOrDefault(x => x.PlayerId == page.Controller.LocalPlatformId);
            var roomHeader = RaceUiAssets.Label(
                $"{RaceTextCatalog.Get("fun.room_code")}：{room.Code}   ·   {(canEdit ? RaceTextCatalog.Get("fun.host") : localSpectator is not null ? RaceTextCatalog.Get("spectate.spectator") : RaceTextCatalog.Get("fun.guest"))}",
                25, StsColors.gold, HorizontalAlignment.Center, true);
            roomHeader.CustomMinimumSize = new Vector2(0, 38);
            page.Content.AddChild(roomHeader);

            var teams = new HBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 74 + (int)rules.TeamSize * 48)
            };
            teams.AddThemeConstantOverride("separation", 24);
            string MemberLabel(EntertainmentRoomMember member) =>
                $"{member.DisplayName}  {CharacterName(member.CharacterId)}  {(member.IsReady ? "✓" : "…")}{(member.IsHost ? "  ◆" : string.Empty)}";
            var first = room.Members.Where(x => x.Team == 1).Select(MemberLabel).ToList();
            var second = room.Members.Where(x => x.Team == 2).Select(MemberLabel).ToList();
            while (first.Count < (int)rules.TeamSize) first.Add(RaceTextCatalog.Get("lobby.empty"));
            while (second.Count < (int)rules.TeamSize) second.Add(RaceTextCatalog.Get("lobby.empty"));
            teams.AddChild(CompactRoomTeamPanel(RaceTextCatalog.Get("fun.team_one"), first.Take((int)rules.TeamSize), true));
            teams.AddChild(CompactRoomTeamPanel(RaceTextCatalog.Get("fun.team_two"), second.Take((int)rules.TeamSize), false));
            page.Content.AddChild(teams);

            if (room.Spectators is { Count: > 0 })
            {
                var spectatorNames = room.Spectators.Select(x =>
                    $"{x.DisplayName}  ·  {RaceTextCatalog.Format("spectate.watching_team", x.WatchingTeam)}");
                var spectatorPanel = RaceUiAssets.Panel(DarkPanel, 10);
                spectatorPanel.CustomMinimumSize = new Vector2(0, 48);
                var spectatorLabel = RaceUiAssets.Label(
                    $"{RaceTextCatalog.Get("spectate.seats")}  {room.Spectators.Count}/{rules.SpectatorSlots}   ·   {string.Join("    ", spectatorNames)}",
                    18, StsColors.cream, HorizontalAlignment.Center);
                spectatorLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 8);
                spectatorPanel.AddChild(spectatorLabel);
                page.Content.AddChild(spectatorPanel);
            }

            var localMember = room.Members.FirstOrDefault(x => x.PlayerId == page.Controller.LocalPlatformId);
            if (localMember is not null)
            {
                var characterRow = ActionRow();
                foreach (var character in PlayableCharacters)
                {
                    var captured = character;
                    var characterButton = RaceUiAssets.Button(
                        (localMember.CharacterId == captured ? "◆ " : string.Empty) + CharacterName(captured),
                        () => _ = SetEntertainmentMemberAsync(page, captured, false), 17, new Vector2(170, 46));
                    characterButton.SetEnabled(room.State == "waiting" && !localMember.IsReady &&
                        (room.Rules.TeamSize != TeamSize.One || canEdit));
                    characterRow.AddChild(characterButton);
                }
                page.Content.AddChild(characterRow);
            }

            var roomActions = ActionRow();
            roomActions.CustomMinimumSize = new Vector2(0, 52);
            if (localSpectator is not null)
            {
                roomActions.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("spectate.team_one"),
                    () => _ = SetSpectatorTargetAsync(page, 1), 18, new Vector2(190, 48)));
                roomActions.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("spectate.team_two"),
                    () => _ = SetSpectatorTargetAsync(page, 2), 18, new Vector2(190, 48)));
                if (room.State != "waiting")
                    roomActions.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("spectate.watch_live"),
                        page.Controller.OpenSpectate, 18, new Vector2(210, 48)));
                roomActions.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("fun.leave"),
                    () => _ = LeaveEntertainmentRoomAsync(page), 19, new Vector2(210, 48)));
                page.Content.AddChild(roomActions);
            }
            else
            {
            var readyButton = RaceUiAssets.Button(RaceTextCatalog.Get(localMember?.IsReady == true ? "fun.unready" : "fun.ready"),
                () =>
                {
                    if (localMember is not null)
                        _ = SetEntertainmentMemberAsync(page, localMember.CharacterId, !localMember.IsReady);
                },
                19, new Vector2(190, 48));
            readyButton.SetEnabled(localMember is not null && room.State == "waiting");
            roomActions.AddChild(readyButton);
            var switchButton = RaceUiAssets.Button(RaceTextCatalog.Get("fun.switch_team"), () => _ = SwitchEntertainmentTeamAsync(page), 19, new Vector2(190, 48));
            switchButton.SetEnabled(localMember?.IsReady != true && room.State == "waiting");
            roomActions.AddChild(switchButton);
            var inviteButton = RaceUiAssets.Button(RaceTextCatalog.Get("common.invite"),
                () => _ = InviteEntertainmentFriendsAsync(page), 19, new Vector2(190, 48));
            inviteButton.SetEnabled(room.State == "waiting");
            roomActions.AddChild(inviteButton);
            if (canEdit)
            {
                var allReady = room.Members.Count == (int)rules.TeamSize * 2 && room.Members.All(x => x.IsReady) &&
                    room.Members.Count(x => x.Team == 1) == (int)rules.TeamSize && room.Members.Count(x => x.Team == 2) == (int)rules.TeamSize;
                var start = RaceUiAssets.Button(RaceTextCatalog.Get("fun.start"), () => _ = StartEntertainmentRoomAsync(page), 20, new Vector2(190, 48));
                start.SetEnabled(allReady && room.State == "waiting");
                roomActions.AddChild(start);
            }
            roomActions.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("fun.leave"), () => _ = LeaveEntertainmentRoomAsync(page), 19, new Vector2(210, 48)));
            page.Content.AddChild(roomActions);
            }
            if (!canEdit)
                page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("fun.host_only"), 18, StsColors.lightGray, HorizontalAlignment.Center));
        }

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        page.Content.AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(root);

        void Apply(RaceRuleSet next)
        {
            var position = scroll.ScrollVertical;
            page.Controller.EntertainmentScrollPosition = position;
            page.Controller.EntertainmentRules = next;
            RenderEntertainment(page, position);
            if (room is not null && canEdit && page.Controller.Services is IRaceEntertainmentRoomService roomService)
                _ = UpdateEntertainmentRoomRulesAsync(page, roomService, next);
        }

        AddRulesSection(root, RaceTextCatalog.Get("fun.section.room"));
        AddOption(root, RaceTextCatalog.Get("fun.team_size"), RaceTextCatalog.Get($"mode.{(int)rules.TeamSize}v{(int)rules.TeamSize}"), () =>
        {
            var minimum = room?.Members.GroupBy(x => x.Team).Select(x => x.Count()).DefaultIfEmpty(1).Max() ?? 1;
            var next = (int)rules.TeamSize;
            do { next = next % 4 + 1; } while (next < minimum);
            Apply(rules with { TeamSize = (TeamSize)next });
        }, page);
        AddOption(root, RaceTextCatalog.Get("fun.connection_mode"), RaceTextCatalog.Get(
            rules.CoordinationMode == "p2p" ? "fun.connection_p2p" : "fun.connection_server"), () =>
        {
            var nextMode = rules.CoordinationMode == "p2p" ? "server" : "p2p";
            Apply(rules with
            {
                CoordinationMode = nextMode,
                SpectatorSlots = nextMode == "p2p" ? 0 : rules.SpectatorSlots
            });
        }, page);
        AddOption(root, RaceTextCatalog.Get("fun.seed_mode"), RaceTextCatalog.Get(rules.RandomSeed ? "fun.random" : "fun.fixed"), () =>
        {
            Apply(rules with { RandomSeed = !rules.RandomSeed });
        }, page);

        if (!rules.RandomSeed)
        {
            var seedRow = OptionRow(RaceTextCatalog.Get("fun.fixed"));
            var seedInput = RaceUiAssets.LineEdit("SEED", rules.Seed);
            seedInput.Editable = canEdit;
            var committedSeed = rules.Seed;
            seedInput.TextChanged += text =>
            {
                var next = page.Controller.EntertainmentRules with { Seed = text };
                page.Controller.EntertainmentRules = next;
            };
            void CommitSeed(string text)
            {
                if (text == committedSeed) return;
                committedSeed = text;
                var next = page.Controller.EntertainmentRules with { Seed = text };
                page.Controller.EntertainmentRules = next;
                if (room is not null && canEdit && page.Controller.Services is IRaceEntertainmentRoomService roomService)
                    _ = UpdateEntertainmentRoomRulesAsync(page, roomService, next);
            }
            seedInput.TextSubmitted += CommitSeed;
            seedInput.FocusExited += () => CommitSeed(seedInput.Text);
            seedRow.AddChild(seedInput);
            root.AddChild(seedRow);
        }

        AddRulesSection(root, RaceTextCatalog.Get("fun.section.run"));
        AddOption(root, RaceTextCatalog.Get("fun.ascension"), $"A{rules.Ascension}", () =>
        {
            Apply(rules with { Ascension = (rules.Ascension + 1) % (RaceRules.MaxAscension + 1) });
        }, page);
        AddOption(root, RaceTextCatalog.Get("fun.timer"), RaceTextCatalog.Get(rules.TimerKind == "game_time" ? "fun.game_time" : "fun.real_time"), () =>
        {
            Apply(rules with { TimerKind = rules.TimerKind == "game_time" ? "real_time" : "game_time" });
        }, page);
        AddOption(root, RaceTextCatalog.Get("fun.sl_timer_mode"), RaceTextCatalog.Get(
            rules.SlTimerMode == "pause_on_save" ? "fun.sl_timer_paused" : "fun.sl_timer_continuous"), () =>
        {
            Apply(rules with { SlTimerMode = rules.SlTimerMode == "pause_on_save" ? "continuous" : "pause_on_save" });
        }, page);
        AddOption(root, RaceTextCatalog.Get("fun.time_limit"), $"{rules.TimeLimitMinutes} min", () =>
        {
            var values = new[] { 60, 90, 120, 180, 240, 360 };
            var next = values[(Array.IndexOf(values, rules.TimeLimitMinutes) + 1 + values.Length) % values.Length];
            Apply(rules with { TimeLimitMinutes = next });
        }, page);
        AddOption(root, RaceTextCatalog.Get("fun.event_sl"), rules.EventSlLimit.ToString(), () =>
        {
            Apply(rules with { EventSlLimit = (rules.EventSlLimit + 1) % 10 });
        }, page);
        AddOption(root, RaceTextCatalog.Get("fun.combat_sl"), rules.CombatSlLimit.ToString(), () =>
        {
            Apply(rules with { CombatSlLimit = (rules.CombatSlLimit + 1) % 10 });
        }, page);
        AddOption(root, RaceTextCatalog.Get("fun.series_length"), RaceTextCatalog.Get(rules.BestOf == 3 ? "fun.bo3" : "fun.bo1"), () =>
        {
            Apply(rules with { BestOf = rules.BestOf == 3 ? 1 : 3 });
        }, page);

        if (rules.BestOf == 3)
        {
            AddRulesSection(root, RaceTextCatalog.Get("fun.series_seeds"));
            var seeds = (rules.SeriesSeeds ?? Array.Empty<string>()).ToList();
            while (seeds.Count < 3) seeds.Add(string.Empty);
            if (!rules.RandomSeed && string.IsNullOrWhiteSpace(seeds[0])) seeds[0] = rules.Seed;
            for (var index = 0; index < 3; index++)
            {
                var capturedIndex = index;
                var seedRow = OptionRow(RaceTextCatalog.Format("fun.series_seed", index + 1));
                var seedInput = RaceUiAssets.LineEdit("RANDOM", seeds[index]);
                seedInput.Editable = canEdit;
                var committedSeed = seeds[index];
                seedInput.TextChanged += text =>
                {
                    var nextSeeds = (page.Controller.EntertainmentRules.SeriesSeeds ?? Array.Empty<string>()).ToList();
                    while (nextSeeds.Count < 3) nextSeeds.Add(string.Empty);
                    nextSeeds[capturedIndex] = text.Trim();
                    var next = RaceRules.NormalizeEntertainment(page.Controller.EntertainmentRules with
                    {
                        RandomSeed = false,
                        Seed = nextSeeds[0],
                        SeriesSeeds = nextSeeds
                    });
                    page.Controller.EntertainmentRules = next;
                };
                void CommitSeriesSeed(string text)
                {
                    text = text.Trim();
                    if (text == committedSeed) return;
                    committedSeed = text;
                    var nextSeeds = (page.Controller.EntertainmentRules.SeriesSeeds ?? Array.Empty<string>()).ToList();
                    while (nextSeeds.Count < 3) nextSeeds.Add(string.Empty);
                    nextSeeds[capturedIndex] = text;
                    var next = RaceRules.NormalizeEntertainment(page.Controller.EntertainmentRules with
                    {
                        RandomSeed = false,
                        Seed = nextSeeds[0],
                        SeriesSeeds = nextSeeds
                    });
                    page.Controller.EntertainmentRules = next;
                    if (room is not null && canEdit && page.Controller.Services is IRaceEntertainmentRoomService roomService)
                        _ = UpdateEntertainmentRoomRulesAsync(page, roomService, next);
                }
                seedInput.TextSubmitted += CommitSeriesSeed;
                seedInput.FocusExited += () => CommitSeriesSeed(seedInput.Text);
                seedRow.AddChild(seedInput);
                root.AddChild(seedRow);
            }
        }

        AddRulesSection(root, RaceTextCatalog.Get("fun.section.access"));
        AddOption(root, RaceTextCatalog.Get("fun.visibility"), RaceTextCatalog.Get($"fun.{rules.Visibility}"), () =>
        {
            var next = rules.Visibility == "friends" ? "public" : rules.Visibility == "public" ? "private" : "friends";
            Apply(rules with { Visibility = next });
        }, page);
        if (rules.CoordinationMode != "p2p")
        {
            AddOption(root, RaceTextCatalog.Get("spectate.seats"), rules.SpectatorSlots.ToString(), () =>
            {
                Apply(rules with { SpectatorSlots = (rules.SpectatorSlots + 1) % 9 });
            }, page);
        }

        AddRulesSection(root, RaceTextCatalog.Get("fun.modifiers"));
        foreach (var modifier in OriginalModifierChoices())
        {
            var captured = modifier;
            var selected = rules.Modifiers.Contains(captured.Id, StringComparer.OrdinalIgnoreCase);
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 70), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 14);
            var copy = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            copy.AddThemeConstantOverride("separation", 2);
            copy.AddChild(RaceUiAssets.Label(captured.Title, 20, selected ? StsColors.gold : StsColors.cream));
            var description = RaceUiAssets.Label(captured.Description, 16, StsColors.lightGray);
            description.ClipText = true;
            copy.AddChild(description);
            row.AddChild(copy);
            var toggle = RaceUiAssets.Button(OnOff(selected), () =>
            {
                var modifiers = rules.Modifiers.ToList();
                if (selected)
                    modifiers.RemoveAll(x => string.Equals(x, captured.Id, StringComparison.OrdinalIgnoreCase));
                else
                {
                    if (IsExclusiveDeckModifier(captured.Id))
                        modifiers.RemoveAll(IsExclusiveDeckModifier);
                    modifiers.Add(captured.Id);
                }
                Apply(rules with { Modifiers = modifiers });
            }, 18, new Vector2(180, 50));
            toggle.SetEnabled(canEdit);
            row.AddChild(toggle);
            root.AddChild(row);
        }

        if (room is null)
        {
            if (rules.CoordinationMode != "p2p")
            {
                var joinRow = OptionRow(RaceTextCatalog.Get("fun.room_code"));
                var roomCode = RaceUiAssets.LineEdit("ABC234");
                roomCode.MaxLength = 6;
                joinRow.AddChild(roomCode);
                var join = RaceUiAssets.Button(RaceTextCatalog.Get("fun.join"), () => _ = JoinEntertainmentRoomAsync(page, roomCode.Text), 20, new Vector2(180, 50));
                joinRow.AddChild(join);
                joinRow.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("spectate.join_seat"),
                    () => _ = JoinEntertainmentSpectatorAsync(page, roomCode.Text), 18, new Vector2(220, 50)));
                root.AddChild(joinRow);
            }
            var create = RaceUiAssets.Button(RaceTextCatalog.Get("fun.create"), () => _ = CreateEntertainmentRoomAsync(page), 25, new Vector2(360, 66));
            create.NormalTint = new Color("76552f");
            root.AddChild(create);
            page.SetInitialFocus(create);
        }
        if (restoreScroll > 0)
            Defer(scroll, () => scroll.ScrollVertical = restoreScroll);
    }

    private static async Task CreateEntertainmentRoomAsync(RacePage page)
    {
        try
        {
            RaceRules.Validate(page.Controller.EntertainmentRules);
            if (page.Controller.EntertainmentRules.CoordinationMode != "p2p")
                await page.Controller.Services.AuthenticateAsync();
            if (page.Controller.Services is not IRaceEntertainmentRoomService rooms)
                throw new InvalidOperationException("Entertainment room service is unavailable.");
            var room = await rooms.CreateRoomAsync(page.Controller.EntertainmentRules);
            page.Controller.CurrentEntertainmentRoom = room;
            Defer(page, () =>
            {
                page.Controller.CanEditEntertainmentRules = true;
                page.Status.SetTextAutoSize(RaceTextCatalog.Format("fun.room_created", room.Code));
                RenderEntertainment(page);
            });
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    private static async Task InviteEntertainmentFriendsAsync(RacePage page)
    {
        try
        {
            if (page.Controller.Services is IRaceEntertainmentRoomService rooms)
                await rooms.InviteFriendAsync(string.Empty);
        }
        catch (Exception exception) { Defer(page, () => page.Status.SetTextAutoSize(exception.Message)); }
    }

    private static async Task JoinEntertainmentRoomAsync(RacePage page, string code)
    {
        try
        {
            if (code.Trim().Length != 6)
                throw new InvalidOperationException(RaceTextCatalog.Get("fun.room_code_invalid"));
            if (page.Controller.Services is not IRaceEntertainmentRoomService rooms)
                throw new InvalidOperationException("Entertainment room service is unavailable.");
            var room = await rooms.JoinRoomAsync(code.Trim().ToUpperInvariant());
            page.Controller.CurrentEntertainmentRoom = room;
            var identity = await page.Controller.Services.IdentityProvider.GetLocalIdentityAsync();
            Defer(page, () =>
            {
                page.Controller.EntertainmentRules = room.Rules;
                page.Controller.CanEditEntertainmentRules = room.HostPlayerId == identity.PlatformId.ToString();
                page.Status.SetTextAutoSize(RaceTextCatalog.Format("fun.room_joined", room.Code));
                RenderEntertainment(page);
            });
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    private static async Task JoinEntertainmentSpectatorAsync(RacePage page, string code)
    {
        try
        {
            if (code.Trim().Length != 6)
                throw new InvalidOperationException(RaceTextCatalog.Get("fun.room_code_invalid"));
            if (page.Controller.Services is not IRaceEntertainmentRoomService rooms)
                throw new InvalidOperationException("Entertainment room service is unavailable.");
            var room = await rooms.JoinSpectatorAsync(code.Trim().ToUpperInvariant());
            page.Controller.CurrentEntertainmentRoom = room;
            var identity = await page.Controller.Services.IdentityProvider.GetLocalIdentityAsync();
            Defer(page, () =>
            {
                page.Controller.LocalPlatformId = identity.PlatformId.ToString();
                page.Controller.EntertainmentRules = room.Rules;
                page.Controller.CanEditEntertainmentRules = false;
                page.Status.SetTextAutoSize(RaceTextCatalog.Format("spectate.joined_room", room.Code));
                RenderEntertainment(page);
            });
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    private static async Task SetSpectatorTargetAsync(RacePage page, int team)
    {
        try
        {
            if (page.Controller.Services is IRaceEntertainmentRoomService rooms)
                await rooms.SetSpectatorTargetAsync(team);
        }
        catch (Exception exception) { Defer(page, () => page.Status.SetTextAutoSize(exception.Message)); }
    }

    private static async Task UpdateEntertainmentRoomRulesAsync(RacePage page, IRaceEntertainmentRoomService rooms, RaceRuleSet rules)
    {
        try { await rooms.UpdateRoomRulesAsync(rules); }
        catch (Exception exception) { Defer(page, () => page.Status.SetTextAutoSize(exception.Message)); }
    }

    private static async Task SwitchEntertainmentTeamAsync(RacePage page)
    {
        try
        {
            if (page.Controller.Services is IRaceEntertainmentRoomService rooms) await rooms.SwitchTeamAsync();
        }
        catch (Exception exception) { Defer(page, () => page.Status.SetTextAutoSize(exception.Message)); }
    }

    private static async Task SetEntertainmentMemberAsync(RacePage page, string characterId, bool ready)
    {
        try
        {
            if (page.Controller.Services is IRaceEntertainmentRoomService rooms)
                await rooms.SetRoomMemberAsync(characterId, ready);
        }
        catch (Exception exception) { Defer(page, () => page.Status.SetTextAutoSize(exception.Message)); }
    }

    private static async Task StartEntertainmentRoomAsync(RacePage page)
    {
        try
        {
            if (page.Controller.Services is IRaceEntertainmentRoomService rooms)
            {
                await rooms.StartRoomAsync();
                Defer(page, () => page.Status.SetTextAutoSize(RaceTextCatalog.Get("fun.starting")));
            }
        }
        catch (Exception exception) { Defer(page, () => page.Status.SetTextAutoSize(exception.Message)); }
    }

    private static async Task LeaveEntertainmentRoomAsync(RacePage page)
    {
        try
        {
            if (page.Controller.Services is IRaceEntertainmentRoomService rooms) await rooms.LeaveRoomAsync();
            Defer(page, () =>
            {
                page.Controller.CurrentEntertainmentRoom = null;
                page.Controller.CanEditEntertainmentRules = true;
                page.Controller.EntertainmentScrollPosition = 0;
                RenderEntertainment(page);
            });
        }
        catch (Exception exception) { Defer(page, () => page.Status.SetTextAutoSize(exception.Message)); }
    }

    public static void BuildProfile(RacePage page, string? playerId)
    {
        SetServiceStatus(page);
        _ = LoadProfileAsync(page, playerId);
    }

    private static async Task LoadProfileAsync(RacePage page, string? playerId)
    {
        try
        {
        var profile = playerId is null
            ? await page.Controller.Services.GetLocalProfileAsync()
            : await page.Controller.Services.GetProfileAsync(playerId);
        var identity = playerId is null ? await page.Controller.Services.IdentityProvider.GetLocalIdentityAsync() : null;
        Defer(page, () =>
        {
            try
            {
            if (!IsAlive(page) || profile is null) return;
            page.ClearContent();
            var top = new HBoxContainer { CustomMinimumSize = new Vector2(0, 210), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            top.AddThemeConstantOverride("separation", 24);
            page.Content.AddChild(top);
            var identityBox = RaceUiAssets.PanelSection(top, BluePanel, 22, 8);
            if (identity?.AvatarRgba is { Length: > 0 })
            {
                var image = Image.CreateFromData((int)identity.AvatarWidth, (int)identity.AvatarHeight, false, Image.Format.Rgba8, identity.AvatarRgba);
                var avatar = new TextureRect
                {
                    Texture = ImageTexture.CreateFromImage(image),
                    CustomMinimumSize = new Vector2(96, 96),
                    ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
                };
                identityBox.AddChild(avatar);
            }
            identityBox.AddChild(RaceUiAssets.Label(profile.DisplayName, 34, StsColors.gold, HorizontalAlignment.Center, true));
            identityBox.AddChild(RaceUiAssets.Label($"《{DisplayTitle(profile.EquippedTitle)}》", 21, StsColors.cream, HorizontalAlignment.Center));
            if (identity is not null)
            {
                var authenticated = page.Controller.Services is IRaceAuthService { IsAuthenticated: true };
                identityBox.AddChild(RaceUiAssets.Label(
                    $"Steam  {identity.PlatformId}   ·   {RaceTextCatalog.Get(authenticated ? "profile.authenticated" : "profile.not_authenticated")}",
                    16, authenticated ? new Color("94c39b") : new Color("cf6a70"), HorizontalAlignment.Center));
            }
            top.AddChild(ProfileRankSummary(profile.SoloRank));
            top.AddChild(ProfileRankSummary(profile.TeamRank));

            if (playerId is null)
            {
                var editor = RaceUiAssets.Panel(GoldPanel, 12);
                editor.CustomMinimumSize = new Vector2(0, 76);
                editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                var editRow = new HBoxContainer();
                editRow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 10);
                editRow.AddThemeConstantOverride("separation", 12);
                editor.AddChild(editRow);
                var nameInput = RaceUiAssets.LineEdit(RaceTextCatalog.Get("profile.display_name"), profile.DisplayName);
                nameInput.MaxLength = 24;
                nameInput.CustomMinimumSize = new Vector2(420, 52);
                editRow.AddChild(nameInput);
                var selectedFavorite = IsPlayableCharacter(profile.FavoriteCharacter) ? profile.FavoriteCharacter : "Ironclad";
                RaceTextureButton? favoriteButton = null;
                favoriteButton = RaceUiAssets.Button(
                    RaceTextCatalog.Format("profile.favorite_button", CharacterName(selectedFavorite)),
                    () =>
                    {
                        var index = Array.FindIndex(PlayableCharacters, x => x.Equals(selectedFavorite, StringComparison.OrdinalIgnoreCase));
                        selectedFavorite = PlayableCharacters[(index + 1 + PlayableCharacters.Length) % PlayableCharacters.Length];
                        favoriteButton!.SetText(RaceTextCatalog.Format("profile.favorite_button", CharacterName(selectedFavorite)));
                    }, 18, new Vector2(330, 52));
                editRow.AddChild(favoriteButton);
                var saveProfile = RaceUiAssets.Button(RaceTextCatalog.Get("profile.save"), () =>
                    _ = SaveProfileAsync(page, nameInput.Text, selectedFavorite), 20, new Vector2(220, 52));
                saveProfile.NormalTint = new Color("765e35");
                editRow.AddChild(saveProfile);
                page.Content.AddChild(editor);
                page.SetInitialFocus(nameInput);
            }

            var stats = new HBoxContainer { CustomMinimumSize = new Vector2(0, 70), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            stats.AddThemeConstantOverride("separation", 16);
            stats.AddChild(StatPlaque(RaceTextCatalog.Format("profile.favorite", CharacterName(profile.FavoriteCharacter))));
            stats.AddChild(StatPlaque(RaceTextCatalog.Format("profile.win_rate", profile.WinRate)));
            stats.AddChild(StatPlaque(RaceTextCatalog.Format("profile.best", RaceUiAssets.FormatTime(profile.BestTime))));
            page.Content.AddChild(stats);
            page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("profile.recent"), 30, StsColors.gold, HorizontalAlignment.Left, true));
            var list = ScrollList(page.Content);
            foreach (var match in profile.RecentMatches)
            {
                var line = $"{match.PlayedAt:MM-dd HH:mm}   {(match.Victory ? "WIN" : "LOSS"),-5}   {(int)match.TeamSize}v{(int)match.TeamSize}   {CharacterName(match.Character),-12}   {HistoryProgress(match.Completed, match.HighestFloor, match.RunTime)}   {match.RatingDelta:+#;-#;0}";
                list.AddChild(RaceUiAssets.Button(line, () => page.Controller.OpenMatchDetails(match), 19, new Vector2(0, 52)));
            }
            if (profile.RecentMatches.Count == 0)
                list.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("profile.no_matches"), 20, StsColors.lightGray, HorizontalAlignment.Center));
            else
                page.SetInitialFocus(list.GetChild<Control>(0));
            }
            catch (Exception exception)
            {
                ShowProfileError(page, exception);
            }
        });
        }
        catch (Exception exception)
        {
            Defer(page, () => ShowProfileError(page, exception));
        }
    }

    private static void ShowProfileError(RacePage page, Exception exception)
    {
        if (!IsAlive(page)) return;
        page.ClearContent();
        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("profile.load_failed"), 26, StsColors.gold, HorizontalAlignment.Center, true));
        page.Content.AddChild(RaceUiAssets.Label(exception.Message, 20, StsColors.lightGray, HorizontalAlignment.Center));
        page.Status.SetTextAutoSize(exception.Message);
    }

    private static async Task SaveProfileAsync(RacePage page, string displayName, string favoriteCharacter)
    {
        try
        {
            _ = await page.Controller.Services.UpdateLocalProfileAsync(displayName, favoriteCharacter);
            Defer(page, () =>
            {
                page.Status.SetTextAutoSize(RaceTextCatalog.Get("profile.saved"));
                _ = LoadProfileAsync(page, null);
            });
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    public static void BuildFriends(RacePage page) => _ = LoadFriendsAsync(page);

    private static async Task LoadFriendsAsync(RacePage page)
    {
        try
        {
            var friends = (await page.Controller.Services.GetFriendsAsync()).ToList();
            Defer(page, () => RenderFriendsShell(page, friends));
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    private static void RenderFriendsShell(RacePage page, List<FriendEntry> friends)
    {
        if (!IsAlive(page)) return;
        page.ClearContent();
        var filter = FriendPresence.Online;
        var search = string.Empty;
        var searchResults = new List<FriendEntry>();
        var searchGeneration = 0;
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, 58) };
        header.AddThemeConstantOverride("separation", 10);
        var searchInput = RaceUiAssets.LineEdit(RaceTextCatalog.Get("friends.search"));
        header.AddChild(searchInput);
        var listHolder = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };

        void RenderList()
        {
            foreach (var child in listHolder.GetChildren())
            {
                listHolder.RemoveChild(child);
                child.QueueFree();
            }
            IEnumerable<FriendEntry> query = !string.IsNullOrWhiteSpace(search)
                ? searchResults
                : friends.Where(x => filter switch
                {
                    FriendPresence.Online => x.Presence is FriendPresence.Online or FriendPresence.InRace,
                    FriendPresence.Offline => x.Presence == FriendPresence.Offline,
                    _ => x.Presence is FriendPresence.Request or FriendPresence.RequestSent
                });
            var visible = query.ToArray();
            foreach (var friend in visible)
                listHolder.AddChild(FriendRow(page, friend));
            if (visible.Length == 0)
                listHolder.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get(string.IsNullOrWhiteSpace(search) ? "friends.empty" : "friends.search_empty"),
                    22, StsColors.lightGray, HorizontalAlignment.Center));
        }

        foreach (var item in new[]
                 {
                     (FriendPresence.Online, "friends.online"),
                     (FriendPresence.Offline, "friends.offline"),
                     (FriendPresence.Request, "friends.requests")
                 })
        {
            var captured = item;
            header.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get(item.Item2), () => { filter = captured.Item1; RenderList(); }, 19, new Vector2(150, 52)));
        }
        searchInput.TextChanged += text =>
        {
            search = text.Trim();
            var generation = ++searchGeneration;
            if (search.Length < 2)
            {
                searchResults.Clear();
                RenderList();
                return;
            }
            _ = SearchAsync(search, generation);
        };
        page.Content.AddChild(header);
        var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(listHolder);
        page.Content.AddChild(scroll);
        RenderList();
        page.SetInitialFocus(header.GetChild<Control>(1));
        SetServiceStatus(page);

        async Task SearchAsync(string query, int generation)
        {
            try
            {
                var result = await page.Controller.Services.SearchPlayersAsync(query);
                Defer(page, () =>
                {
                    if (!IsAlive(page) || generation != searchGeneration) return;
                    searchResults.Clear();
                    searchResults.AddRange(result);
                    RenderList();
                });
            }
            catch (Exception exception)
            {
                Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
            }
        }
    }

    private static Control FriendRow(RacePage page, FriendEntry friend)
    {
        var panel = RaceUiAssets.Panel(new Color(0.12f, 0.23f, 0.27f, 0.82f));
        panel.CustomMinimumSize = new Vector2(0, 68);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var row = new HBoxContainer();
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 9);
        row.AddThemeConstantOverride("separation", 10);
        panel.AddChild(row);
        var presence = friend.Presence == FriendPresence.InRace ? "● RACE" : friend.Presence == FriendPresence.Online ? "● ONLINE" :
            friend.Presence == FriendPresence.Request ? "◆ REQUEST" : friend.Presence == FriendPresence.RequestSent ? "◇ PENDING" :
            friend.Presence == FriendPresence.SearchResult ? "" : "○ OFFLINE";
        var text = RaceUiAssets.Label($"{friend.DisplayName}   《{DisplayTitle(friend.EquippedTitle)}》   {TierName(friend.RankTier)}   {presence}", 21, friend.Presence == FriendPresence.Offline ? StsColors.lightGray : StsColors.cream);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(text);
        row.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("common.view"), () => page.Controller.OpenProfile(friend.Id), 18, new Vector2(110, 48)));
        if (friend.Presence == FriendPresence.InRace)
            row.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("spectate.watch"), page.Controller.OpenSpectate, 18, new Vector2(110, 48)));
        if (friend.Presence == FriendPresence.SearchResult)
            row.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("common.add"), () => _ = FriendAction(
                async () => await page.Controller.Services.SendFriendRequestAsync(friend.Id), "friends.request_sent"), 18, new Vector2(110, 48)));
        else if (friend.Presence == FriendPresence.Request)
        {
            row.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("common.accept"), () => _ = FriendAction(async () => await page.Controller.Services.AcceptRequestAsync(friend.Id), "friends.accepted"), 18, new Vector2(110, 48)));
            row.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("common.decline"), () => _ = FriendAction(async () => await page.Controller.Services.DeclineRequestAsync(friend.Id), "friends.declined"), 18, new Vector2(110, 48)));
        }
        else if (friend.Presence == FriendPresence.RequestSent)
            row.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("common.remove"), () => _ = FriendAction(
                async () => await page.Controller.Services.RemoveFriendAsync(friend.Id), "friends.removed"), 18, new Vector2(110, 48)));
        else if (friend.Presence != FriendPresence.Offline)
            row.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("common.invite"), () => _ = FriendAction(async () => await page.Controller.Services.InviteAsync(friend.Id), "friends.invited"), 18, new Vector2(110, 48)));
        else
            row.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("common.remove"), () =>
                page.Controller.OpenConfirm(
                    RaceTextCatalog.Get("friends.remove_title"),
                    RaceTextCatalog.Format("friends.remove_body", friend.DisplayName),
                    async () => await FriendAction(async () => await page.Controller.Services.RemoveFriendAsync(friend.Id), "friends.removed")), 18, new Vector2(110, 48)));
        return panel;

        async Task FriendAction(Func<Task> action, string message)
        {
            await action();
            var updated = await page.Controller.Services.GetFriendsAsync();
            Defer(page, () =>
            {
                RenderFriendsShell(page, updated.ToList());
                page.Status.SetTextAutoSize(RaceTextCatalog.Get(message));
            });
        }
    }

    public static void BuildLeaderboard(RacePage page)
    {
        var pool = RankedPool.Solo;
        var friendsOnly = false;
        var history = false;
        var currentPage = 0;
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, 58) };
        header.AddThemeConstantOverride("separation", 10);
        page.Content.AddChild(header);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.Content.AddChild(LeaderboardColumns(
            RaceTextCatalog.Get("leaderboard.rank"),
            RaceTextCatalog.Get("leaderboard.player"),
            RaceTextCatalog.Get("leaderboard.tier"),
            RaceTextCatalog.Get("leaderboard.rating"),
            RaceTextCatalog.Get("leaderboard.win_rate"),
            RaceTextCatalog.Get("leaderboard.best_time"),
            StsColors.gold,
            19));
        var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(list);
        page.Content.AddChild(scroll);
        var pager = ActionRow();
        page.Content.AddChild(pager);
        RaceTextureButton? poolFilter = null;
        RaceTextureButton? scopeFilter = null;
        RaceTextureButton? seasonFilter = null;

        async Task Refresh()
        {
            var rows = await page.Controller.Services.QueryAsync(pool, friendsOnly, history);
            Defer(page, () =>
            {
                if (!IsAlive(page)) return;
                var maxPage = rows.Count == 0 ? 0 : (rows.Count - 1) / 10;
                currentPage = Math.Clamp(currentPage, 0, maxPage);
                poolFilter?.SetText(TierLabel(pool));
                scopeFilter?.SetText(RaceTextCatalog.Get(friendsOnly ? "leaderboard.friends" : "leaderboard.global"));
                seasonFilter?.SetText(RaceTextCatalog.Get(history ? "leaderboard.history" : "leaderboard.current"));
                Clear(list); Clear(pager);
                foreach (var entry in rows.Skip(currentPage * 10).Take(10))
                {
                    list.AddChild(LeaderboardEntryRow(page, entry));
                }
                if (rows.Count == 0)
                    list.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("leaderboard.empty"), 23, StsColors.lightGray, HorizontalAlignment.Center));
                if (currentPage > 0)
                    pager.AddChild(RaceUiAssets.Button("◀", () => { currentPage--; _ = Refresh(); }, 24, new Vector2(80, 48)));
                if ((currentPage + 1) * 10 < rows.Count)
                    pager.AddChild(RaceUiAssets.Button("▶", () => { currentPage++; _ = Refresh(); }, 24, new Vector2(80, 48)));
            });
        }

        poolFilter = RaceUiAssets.Button(TierLabel(pool), () => { pool = pool == RankedPool.Solo ? RankedPool.Team : RankedPool.Solo; currentPage = 0; _ = Refresh(); }, 19, new Vector2(180, 52));
        scopeFilter = RaceUiAssets.Button(RaceTextCatalog.Get("leaderboard.global"), () => { friendsOnly = !friendsOnly; currentPage = 0; _ = Refresh(); }, 19, new Vector2(180, 52));
        seasonFilter = RaceUiAssets.Button(RaceTextCatalog.Get("leaderboard.current"), () => { history = !history; currentPage = 0; _ = Refresh(); }, 19, new Vector2(210, 52));
        header.AddChild(poolFilter);
        header.AddChild(scopeFilter);
        header.AddChild(seasonFilter);
        page.SetInitialFocus(header.GetChild<Control>(0));
        _ = Refresh();
    }

    public static void BuildTitles(RacePage page)
    {
        _ = LoadTitlesAsync(page, 0);
        async Task LoadTitlesAsync(RacePage target, int filter)
        {
            var titles = await target.Controller.Services.GetTitlesAsync();
            Defer(target, () => RenderTitles(target, titles, filter));
        }
    }

    private static void RenderTitles(RacePage page, IReadOnlyList<TitleDefinition> titles, int filter)
    {
        if (!IsAlive(page)) return;
        page.ClearContent();
        var header = ActionRow();
        page.Content.AddChild(header);
        var keys = new[] { "titles.all", "titles.unlocked", "titles.locked" };
        for (var i = 0; i < keys.Length; i++)
        {
            var captured = i;
            header.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get(keys[i]), () =>
            {
                var next = captured;
                _ = ReloadTitles(page, next);
            }, 19, new Vector2(150, 50)));
        }
        var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 12);
        scroll.AddChild(grid);
        page.Content.AddChild(scroll);
        IEnumerable<TitleDefinition> query = titles;
        if (filter == 1) query = query.Where(x => x.IsUnlocked);
        if (filter == 2) query = query.Where(x => !x.IsUnlocked);
        foreach (var title in query)
        {
            var card = new VBoxContainer
            {
                CustomMinimumSize = new Vector2(0, 176),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            grid.AddChild(card);
            var panel = RaceUiAssets.PanelSection(card, title.IsEquipped ? GoldPanel : DarkPanel, 16, 6);
            var titleLabel = RaceUiAssets.Label($"《{DisplayTitle(title.Name)}》", 24, title.IsUnlocked ? StsColors.gold : StsColors.lightGray, HorizontalAlignment.Center, true);
            titleLabel.CustomMinimumSize = new Vector2(0, 36);
            panel.AddChild(titleLabel);
            var condition = RaceUiAssets.Label(title.IsUnlocked ? LocalizeTitleDescription(title) : LocalizeUnlockCondition(title), 17, StsColors.cream, HorizontalAlignment.Center);
            condition.CustomMinimumSize = new Vector2(0, 44);
            panel.AddChild(condition);
            var button = RaceUiAssets.Button(RaceTextCatalog.Get(title.IsEquipped ? "common.unequip" : title.IsUnlocked ? "common.equip" : "common.locked"),
                () => _ = EquipTitle(page, title.IsEquipped ? string.Empty : title.Id), 17, new Vector2(130, 44));
            button.SetEnabled(title.IsUnlocked);
            panel.AddChild(button);
            page.SetInitialFocus(button);
        }

        async Task ReloadTitles(RacePage target, int nextFilter)
        {
            var updated = await target.Controller.Services.GetTitlesAsync();
            Defer(target, () => RenderTitles(target, updated, nextFilter));
        }

        async Task EquipTitle(RacePage target, string id)
        {
            await target.Controller.Services.EquipTitleAsync(id);
            var updated = await target.Controller.Services.GetTitlesAsync();
            Defer(target, () => RenderTitles(target, updated, filter));
        }
    }

    public static void BuildActivity(RacePage page) => _ = ReloadActivity(page);

    private static async Task ReloadActivity(RacePage page)
    {
        var activity = await page.Controller.Services.GetCurrentActivityAsync();
        Defer(page, () => RenderActivity(page, activity));
    }

    private static void RenderActivity(RacePage page, ActivityDefinition activity)
    {
        if (!IsAlive(page)) return;
        page.ClearContent();
        var days = Math.Max(0, (activity.EndsAt - DateTimeOffset.Now).Days);
        page.Content.AddChild(RaceUiAssets.Label(activity.Description, 23, StsColors.cream, HorizontalAlignment.Center));
        page.Content.AddChild(RaceUiAssets.Label($"{RaceTextCatalog.Format("activity.ends", days)}   ·   {RaceTextCatalog.Format("activity.points", activity.CurrentPoints)}", 22, StsColors.gold, HorizontalAlignment.Center));
        var detailActions = ActionRow();
        detailActions.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("activity.rules"), () => page.Controller.OpenDetails(
            RaceTextCatalog.Get("activity.rules"),
            RaceTextCatalog.Get("activity.rules.same_seed"),
            RaceTextCatalog.Get("activity.rules.team_time"),
            RaceTextCatalog.Get("activity.rules.demo")), 18, new Vector2(190, 48)));
        detailActions.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("activity.announcement"), () => page.Controller.OpenDetails(
            RaceTextCatalog.Get("activity.announcement"),
            RaceTextCatalog.Get("activity.announcement.body")), 18, new Vector2(190, 48)));
        page.Content.AddChild(detailActions);
        var columns = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 24);
        page.Content.AddChild(columns);
        var missions = RaceUiAssets.PanelSection(columns, BluePanel, 18, 9);
        missions.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("activity.missions"), 29, StsColors.gold, HorizontalAlignment.Center, true));
        foreach (var mission in activity.Missions)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 78) };
            var label = RaceUiAssets.Label($"{LocalizeMission(mission.Name)}\n{LocalizeMission(mission.Description)}   {mission.Progress}/{mission.Goal}", 18, StsColors.cream);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            var claim = RaceUiAssets.Button(RaceTextCatalog.Get(mission.State == ActivityProgressState.Claimed ? "common.claimed" : "common.claim"),
                () => _ = ClaimMission(page, mission.Id), 17, new Vector2(110, 46));
            claim.SetEnabled(mission.State == ActivityProgressState.Available);
            row.AddChild(claim);
            missions.AddChild(row);
            page.SetInitialFocus(claim);
        }
        var rewards = RaceUiAssets.PanelSection(columns, GoldPanel, 18, 8);
        rewards.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("activity.rewards"), 29, StsColors.gold, HorizontalAlignment.Center, true));
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 9);
        grid.AddThemeConstantOverride("v_separation", 9);
        rewards.AddChild(grid);
        foreach (var reward in activity.Rewards)
        {
            var text = $"{reward.Level:00}  {LocalizeReward(reward.Name)}\n{RaceTextCatalog.Get(reward.State == ActivityProgressState.Claimed ? "common.claimed" : reward.State == ActivityProgressState.Available ? "common.claim" : "common.locked")}";
            var button = RaceUiAssets.Button(text, () => _ = ClaimReward(page, reward.Level), 17, new Vector2(210, 72));
            button.SetEnabled(reward.State == ActivityProgressState.Available);
            grid.AddChild(button);
        }
    }

    private static async Task ClaimMission(RacePage page, string id)
    {
        await page.Controller.Services.ClaimMissionAsync(id);
        await ReloadActivity(page);
    }

    private static async Task ClaimReward(RacePage page, int level)
    {
        await page.Controller.Services.ClaimRewardAsync(level);
        await ReloadActivity(page);
    }

    public static void BuildMatchDetails(RacePage page, MatchHistoryEntry match)
    {
        foreach (var paragraph in HistoryDetails(match))
            page.Content.AddChild(RaceUiAssets.Label(paragraph, 21, StsColors.cream, HorizontalAlignment.Center));
        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("replay.recordings"), 28, StsColors.gold, HorizontalAlignment.Left, true));
        var list = ScrollList(page.Content);
        list.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("replay.loading"), 20, StsColors.lightGray, HorizontalAlignment.Center));
        _ = LoadMatchReplaysAsync(page, match, list);
    }

    private static async Task LoadMatchReplaysAsync(RacePage page, MatchHistoryEntry match, VBoxContainer list)
    {
        try
        {
            var replays = await page.Controller.Services.GetMatchReplaysAsync(match.MatchId);
            Defer(page, () =>
            {
                if (!IsAlive(page) || !IsAlive(list)) return;
                foreach (var child in list.GetChildren()) child.QueueFree();
                var gameOrder = (match.SeriesGames ?? Array.Empty<LegendGameResult>()).Where(x => !string.IsNullOrWhiteSpace(x.GameId))
                    .ToDictionary(x => x.GameId, x => x.GameNumber, StringComparer.Ordinal);
                foreach (var replay in replays.OrderBy(x => gameOrder.GetValueOrDefault(x.GameId, 999))
                             .ThenBy(x => x.GameId).ThenBy(x => x.TeamId).ThenBy(x => x.DisplayName))
                {
                    var captured = replay;
                    var game = gameOrder.TryGetValue(captured.GameId, out var gameNumber) ? $"G{gameNumber}   ·   " : string.Empty;
                    var local = !string.IsNullOrWhiteSpace(match.LocalTeamId) && captured.TeamId == match.LocalTeamId;
                    var side = RaceTextCatalog.Get(local ? "replay.local_side" : "replay.opponent_side");
                    var status = RaceTextCatalog.Get(captured.IsLive ? "replay.live" : captured.Completed ? "replay.complete" : "replay.partial");
                    var row = OptionRow($"{game}{side}   ·   {captured.DisplayName}   ·   {CharacterName(captured.CharacterId)}   ·   {status}");
                    var watch = RaceUiAssets.Button(RaceTextCatalog.Get("replay.watch"), () =>
                        _ = StartReplayAsync(page, captured, captured.IsLive), 18, new Vector2(190, 48));
                    watch.SetEnabled(captured.EventCount > 0);
                    row.AddChild(watch);
                    list.AddChild(row);
                    page.SetInitialFocus(watch);
                }
                if (replays.Count == 0)
                    list.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("replay.none"), 20, StsColors.lightGray, HorizontalAlignment.Center));
            });
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    public static void BuildSpectate(RacePage page)
    {
        SetServiceStatus(page);
        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("spectate.description"), 21, StsColors.cream, HorizontalAlignment.Center));
        var list = ScrollList(page.Content);
        list.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("replay.loading"), 20, StsColors.lightGray, HorizontalAlignment.Center));
        _ = LoadSpectatableAsync(page, list);
    }

    private static async Task LoadSpectatableAsync(RacePage page, VBoxContainer list)
    {
        try
        {
            var races = await page.Controller.Services.GetSpectatableRacesAsync();
            Defer(page, () =>
            {
                if (!IsAlive(page) || !IsAlive(list)) return;
                foreach (var child in list.GetChildren()) child.QueueFree();
                foreach (var race in races)
                {
                    var captured = race;
                    var source = captured.IsLegendPublic
                        ? RaceTextCatalog.Get("spectate.legend")
                        : RaceTextCatalog.Get("spectate.friend");
                    var row = OptionRow($"{captured.DisplayName}   ·   {CharacterName(captured.CharacterId)}   ·   {source}");
                    var watch = RaceUiAssets.Button(RaceTextCatalog.Get("spectate.watch_live"), () =>
                        _ = StartLiveSpectateAsync(page, captured), 18, new Vector2(220, 48));
                    row.AddChild(watch);
                    list.AddChild(row);
                    page.SetInitialFocus(watch);
                }
                if (races.Count == 0)
                    list.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("spectate.none"), 20, StsColors.lightGray, HorizontalAlignment.Center));
            });
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    private static async Task StartReplayAsync(RacePage page, RaceReplaySummary replay, bool live)
    {
        try
        {
            page.Status.SetTextAutoSize(RaceTextCatalog.Get("replay.preparing"));
            if (!ReplayMod.TryInitialize())
                throw new InvalidOperationException(ReplayMod.InitializationError ?? RaceTextCatalog.Get("replay.unavailable"));
            await RaceReplayCloudCoordinator.WatchAsync(replay, live);
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    private static async Task StartLiveSpectateAsync(RacePage page, SpectatableRace race)
    {
        try
        {
            page.Status.SetTextAutoSize(RaceTextCatalog.Get("replay.preparing"));
            if (!ReplayMod.TryInitialize())
                throw new InvalidOperationException(ReplayMod.InitializationError ?? RaceTextCatalog.Get("replay.unavailable"));
            await RaceReplayCloudCoordinator.WatchMatchAsync(race);
        }
        catch (Exception exception)
        {
            Defer(page, () => page.Status.SetTextAutoSize(exception.Message));
        }
    }

    public static void BuildDetails(RacePage page, IEnumerable<string> paragraphs)
    {
        var wrapper = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        var box = RaceUiAssets.PanelSection(wrapper, DarkPanel, 42, 22);
        box.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        foreach (var paragraph in paragraphs)
            box.AddChild(RaceUiAssets.Label(paragraph, 25, StsColors.cream, HorizontalAlignment.Center));
        page.Content.AddChild(wrapper);
    }

    public static void BuildConfirm(RacePage page, string body, Func<Task> confirmed)
    {
        page.Content.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        page.Content.AddChild(RaceUiAssets.Label(body, 29, StsColors.cream, HorizontalAlignment.Center, true));
        var actions = ActionRow();
        var confirm = RaceUiAssets.Button(RaceTextCatalog.Get("common.confirm"), () => _ = RunConfirmed(), 24, new Vector2(210, 64));
        actions.AddChild(confirm);
        actions.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("common.cancel"), page.Controller.CloseTop, 24, new Vector2(210, 64)));
        page.Content.AddChild(actions);
        page.Content.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        page.SetInitialFocus(confirm);

        async Task RunConfirmed()
        {
            await confirmed();
            if (IsAlive(page))
                page.Controller.CloseTop();
        }
    }

    public static void BuildSettings(RacePage page)
    {
        SetServiceStatus(page);
        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("settings.server"), 30, StsColors.gold, HorizontalAlignment.Center, true));
        var addressInput = RaceUiAssets.LineEdit("http://127.0.0.1:8080", page.Controller.Services.ConfiguredServerUri?.ToString().TrimEnd('/') ?? string.Empty);
        addressInput.CustomMinimumSize = new Vector2(760, 58);
        addressInput.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        page.Content.AddChild(addressInput);

        var presets = ActionRow();
        presets.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("settings.local"), () => Apply("http://127.0.0.1:8080"), 20, new Vector2(230, 54)));
        presets.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("settings.official"), () => Apply(RaceRuntimeInfo.OfficialServerUrl), 20, new Vector2(230, 54)));
        presets.AddChild(RaceUiAssets.Button(RaceTextCatalog.Get("settings.disconnect"), Disconnect, 20, new Vector2(230, 54)));
        page.Content.AddChild(presets);

        var actions = ActionRow();
        var save = RaceUiAssets.Button(RaceTextCatalog.Get("settings.save"), () => Apply(addressInput.Text), 24, new Vector2(300, 62));
        save.NormalTint = new Color("7b632c");
        save.FocusTint = new Color("b59643");
        actions.AddChild(save);
        page.Content.AddChild(actions);
        page.SetInitialFocus(addressInput);

        async void Apply(string raw)
        {
            var url = raw.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var serverUri) ||
                (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(serverUri.Host))
            {
                page.Status.SetTextAutoSize(RaceTextCatalog.Get("settings.invalid"));
                return;
            }
            addressInput.Text = serverUri.AbsoluteUri.TrimEnd('/');
            page.Status.SetTextAutoSize(RaceTextCatalog.Get("settings.connecting"));
            try
            {
                await page.Controller.Services.ChangeServerAsync(serverUri);
                RaceRuntimeInfo.SaveServerUrl(serverUri.AbsoluteUri);
                if (IsAlive(page))
                    page.Status.SetTextAutoSize(RaceTextCatalog.Get("settings.saved"));
                if (RaceRuntimeInfo.IsOfficialServer(serverUri))
                    page.Controller.OpenDetails(RaceTextCatalog.Get("auth.notice_title"), RaceTextCatalog.Get("auth.beta_access_required"));
            }
            catch (Exception exception)
            {
                if (IsAlive(page))
                    page.Status.SetTextAutoSize(RaceTextCatalog.Format("settings.failed", exception.Message));
                if (RaceRuntimeInfo.IsOfficialServer(serverUri))
                    page.Controller.ShowServerNotice(exception, serverUri);
            }
        }

        async void Disconnect()
        {
            await page.Controller.Services.ChangeServerAsync(null);
            RaceRuntimeInfo.SaveServerUrl(string.Empty);
            addressInput.Text = string.Empty;
            if (IsAlive(page))
                page.Status.SetTextAutoSize(RaceTextCatalog.Get("settings.disconnected"));
        }
    }

    public static void BuildComingSoon(RacePage page, string body)
    {
        page.Content.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        page.Content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("coming_soon.title"), 38, StsColors.gold, HorizontalAlignment.Center, true));
        page.Content.AddChild(RaceUiAssets.Label(body, 23, StsColors.cream, HorizontalAlignment.Center));
        var actions = ActionRow();
        var ok = RaceUiAssets.Button(RaceTextCatalog.Get("coming_soon.ok"), page.Controller.CloseTop, 22, new Vector2(220, 58));
        actions.AddChild(ok);
        page.Content.AddChild(actions);
        page.Content.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        page.SetInitialFocus(ok);
    }

    private static Control TeamPanel(string title, IEnumerable<string> names, bool local)
    {
        var wrapper = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        var box = RaceUiAssets.PanelSection(wrapper, local ? BluePanel : RedPanel);
        box.AddChild(RaceUiAssets.Label(title, 30, StsColors.gold, HorizontalAlignment.Center, true));
        var index = 1;
        foreach (var name in names)
        {
            var plaque = RaceUiAssets.Panel(new Color(local ? "244b59" : "5c3035"));
            plaque.CustomMinimumSize = new Vector2(0, 62);
            plaque.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            var label = RaceUiAssets.Label($"{index}.  {name}", 22, StsColors.cream);
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 10);
            plaque.AddChild(label);
            box.AddChild(plaque);
            index++;
        }
        return wrapper;
    }

    private static Control CompactRoomTeamPanel(string title, IEnumerable<string> names, bool local)
    {
        var wrapper = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var box = RaceUiAssets.PanelSection(wrapper, local ? BluePanel : RedPanel, 12, 5);
        var heading = RaceUiAssets.Label(title, 24, StsColors.gold, HorizontalAlignment.Center, true);
        heading.CustomMinimumSize = new Vector2(0, 34);
        box.AddChild(heading);
        var index = 1;
        foreach (var name in names)
        {
            var plaque = RaceUiAssets.Panel(new Color(local ? "244b59" : "5c3035"));
            plaque.CustomMinimumSize = new Vector2(0, 43);
            var label = RaceUiAssets.Label($"{index}.   {name}", 19, StsColors.cream);
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 9);
            plaque.AddChild(label);
            box.AddChild(plaque);
            index++;
        }
        return wrapper;
    }

    private static Control ResultTeamPanel(string title, RaceTeam team, bool local, SettlementSide? settlement = null)
    {
        var wrapper = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var box = RaceUiAssets.PanelSection(wrapper, local ? BluePanel : RedPanel, 22, 12);
        box.AddChild(RaceUiAssets.Label(title, 28, StsColors.gold, HorizontalAlignment.Center, true));
        var score = settlement?.CompletionMilliseconds is long completion
            ? RaceRules.FormatElapsed(completion)
            : team.SharedRunTime is { } runTime
                ? RaceUiAssets.FormatTime(runTime)
                : RaceTextCatalog.Format("result.floor", settlement?.HighestFloor ?? 0);
        box.AddChild(RaceUiAssets.Label(score, 48, StsColors.cream, HorizontalAlignment.Center, true));
        if (settlement is not null)
        {
            box.AddChild(RaceUiAssets.Label(
                RaceTextCatalog.Format("result.floor_entry", settlement.HighestFloor, RaceRules.FormatElapsed(settlement.HighestFloorEnteredAtMilliseconds)),
                18, StsColors.lightGray, HorizontalAlignment.Center));
            box.AddChild(RaceUiAssets.Label(
                RaceTextCatalog.Format("result.attempts", settlement.RestartCount, settlement.EventSlUsed, settlement.CombatSlUsed),
                18, StsColors.lightGray, HorizontalAlignment.Center));
            if (settlement.Outcome is ParticipantOutcome.Surrendered or ParticipantOutcome.Forfeited or ParticipantOutcome.TimedOut)
                box.AddChild(RaceUiAssets.Label(SettlementOutcome(settlement.Outcome), 19, new Color("cf6a70"), HorizontalAlignment.Center));
        }
        foreach (var player in team.Participants)
            box.AddChild(RaceUiAssets.Label(player.DisplayName, 21, StsColors.cream, HorizontalAlignment.Center));
        return wrapper;
    }

    private static Control RankPanel(RankSnapshot rank, Action action, string buttonText)
    {
        var wrapper = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var box = RaceUiAssets.PanelSection(wrapper, rank.Pool == RankedPool.Solo ? BluePanel : RedPanel, 24, 10);
        box.AddChild(RaceUiAssets.Label(TierLabel(rank.Pool), 29, StsColors.gold, HorizontalAlignment.Center, true));
        box.AddChild(RaceUiAssets.Label("◆", 70, TierColor(rank.Tier), HorizontalAlignment.Center, true));
        box.AddChild(RaceUiAssets.Label($"{TierName(rank.Tier)} {(rank.Division > 0 ? Roman(rank.Division) : "")}", 34, StsColors.cream, HorizontalAlignment.Center, true));
        box.AddChild(RaceUiAssets.Label(RaceTextCatalog.Format("rank.points", rank.Points), 23, StsColors.gold, HorizontalAlignment.Center));
        box.AddChild(RaceUiAssets.Label(RaceTextCatalog.Format("rank.progress", rank.Points, 100), 18, StsColors.cream, HorizontalAlignment.Center));
        box.AddChild(RaceUiAssets.Label(RaceTextCatalog.Format("rank.record", rank.Wins, rank.Losses), 20, StsColors.cream, HorizontalAlignment.Center));
        box.AddChild(RaceUiAssets.Label(RaceTextCatalog.Format("rank.placement", rank.PlacementGamesRemaining), 18, StsColors.lightGray, HorizontalAlignment.Center));
        box.AddChild(RaceUiAssets.Label(RaceTextCatalog.Format("rank.position", rank.LeaderboardRank), 18, StsColors.lightGray, HorizontalAlignment.Center));
        box.AddChild(RaceUiAssets.Button(buttonText, action, 22, new Vector2(230, 58)));
        return wrapper;
    }

    private static Control ProfileRankSummary(RankSnapshot rank)
    {
        var wrapper = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var box = RaceUiAssets.PanelSection(wrapper, rank.Pool == RankedPool.Solo ? BluePanel : RedPanel, 16, 6);
        box.AddChild(RaceUiAssets.Label(TierLabel(rank.Pool), 21, StsColors.gold, HorizontalAlignment.Center));
        box.AddChild(RaceUiAssets.Label($"◆ {TierName(rank.Tier)} {Roman(rank.Division)}", 28, TierColor(rank.Tier), HorizontalAlignment.Center, true));
        box.AddChild(RaceUiAssets.Label(RaceTextCatalog.Format("rank.points", rank.Points), 18, StsColors.cream, HorizontalAlignment.Center));
        return wrapper;
    }

    private static Control StatPlaque(string text)
    {
        var panel = RaceUiAssets.Panel(new Color(0.13f, 0.21f, 0.24f, 0.84f));
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        panel.CustomMinimumSize = new Vector2(0, 62);
        var label = RaceUiAssets.Label(text, 21, StsColors.cream, HorizontalAlignment.Center);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 8);
        panel.AddChild(label);
        return panel;
    }

    private static T? FindDescendant<T>(Node root) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match)
                return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static void BuildCenteredState(RacePage page, string title, string glyph, string? buttonText, Action? action)
    {
        var spacer = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.Content.AddChild(spacer);
        page.Content.AddChild(RaceUiAssets.Label(glyph, 76, StsColors.gold, HorizontalAlignment.Center, true));
        page.Content.AddChild(RaceUiAssets.Label(title, 38, StsColors.cream, HorizontalAlignment.Center, true));
        if (buttonText is not null && action is not null)
        {
            var row = ActionRow();
            var button = RaceUiAssets.Button(buttonText, action, 24);
            row.AddChild(button);
            page.Content.AddChild(row);
            page.SetInitialFocus(button);
        }
        page.Content.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
    }

    private static HBoxContainer ActionRow()
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, CustomMinimumSize = new Vector2(0, 64) };
        row.AddThemeConstantOverride("separation", 14);
        return row;
    }

    private static Control LeaderboardEntryRow(RacePage page, LeaderboardEntry entry)
    {
        var panel = RaceUiAssets.Panel(BluePanel, 18);
        panel.CustomMinimumSize = new Vector2(0, 58);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var margin = new MarginContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        panel.AddChild(margin);
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var content = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 8);
        margin.AddChild(content);
        var columns = LeaderboardColumns(
            entry.Position.ToString(),
            entry.DisplayName,
            TierName(entry.Tier),
            entry.Rating.ToString(),
            entry.WinRate.ToString("P1"),
            entry.BestTime == TimeSpan.Zero ? "—" : RaceUiAssets.FormatTime(entry.BestTime),
            StsColors.cream,
            18);
        columns.MouseFilter = Control.MouseFilterEnum.Ignore;
        content.AddChild(columns);
        var view = RaceUiAssets.Button(RaceTextCatalog.Get("common.view"), () => page.Controller.OpenDetails(
            RaceTextCatalog.Get("leaderboard.player_detail"),
            $"#{entry.Position}   {entry.DisplayName}",
            $"{TierName(entry.Tier)}   ·   {entry.Rating}",
            RaceTextCatalog.Format("profile.win_rate", entry.WinRate),
            RaceTextCatalog.Format("profile.best", entry.BestTime == TimeSpan.Zero ? "—" : RaceUiAssets.FormatTime(entry.BestTime))), 17, new Vector2(115, 46));
        view.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        content.AddChild(view);
        return panel;
    }

    private static HBoxContainer LeaderboardColumns(
        string rank,
        string player,
        string tier,
        string rating,
        string winRate,
        string bestTime,
        Color color,
        int fontSize)
    {
        var row = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 48),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 12);
        AddColumn(rank, 0.65f, HorizontalAlignment.Center);
        AddColumn(player, 2.8f, HorizontalAlignment.Left);
        AddColumn(tier, 1.25f, HorizontalAlignment.Center);
        AddColumn(rating, 1.0f, HorizontalAlignment.Center);
        AddColumn(winRate, 1.0f, HorizontalAlignment.Center);
        AddColumn(bestTime, 1.15f, HorizontalAlignment.Center);
        return row;

        void AddColumn(string text, float ratio, HorizontalAlignment alignment)
        {
            var label = RaceUiAssets.Label(text, fontSize, color, alignment);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            label.SizeFlagsStretchRatio = ratio;
            label.CustomMinimumSize = new Vector2(92 * ratio, 46);
            label.ClipText = true;
            row.AddChild(label);
        }
    }

    private static HBoxContainer OptionRow(string label)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 58), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);
        var text = RaceUiAssets.Label(label, 21, StsColors.cream);
        text.CustomMinimumSize = new Vector2(330, 52);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(text);
        return row;
    }

    private static void AddRulesSection(Control root, string text)
    {
        var banner = RaceUiAssets.Panel(new Color(0.20f, 0.16f, 0.09f, 0.88f));
        banner.CustomMinimumSize = new Vector2(0, 44);
        banner.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var label = RaceUiAssets.Label(text, 22, StsColors.gold, HorizontalAlignment.Center, true);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 8);
        banner.AddChild(label);
        root.AddChild(banner);
    }

    private static void AddOption(Control root, string label, string value, Action action, RacePage page)
    {
        var row = OptionRow(label);
        var button = RaceUiAssets.Button(value, action, 20, new Vector2(330, 50));
        button.SetEnabled(page.Controller.CurrentEntertainmentRoom is null || page.Controller.CanEditEntertainmentRules);
        row.AddChild(button);
        root.AddChild(row);
        page.SetInitialFocus(button);
    }

    private static VBoxContainer ScrollList(Control parent)
    {
        var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 7);
        scroll.AddChild(list);
        parent.AddChild(scroll);
        return list;
    }

    private static void Clear(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static void Defer(GodotObject owner, Action action) => Callable.From(() =>
    {
        if (GodotObject.IsInstanceValid(owner)) action();
    }).CallDeferred();

    private static bool IsAlive(GodotObject value) => GodotObject.IsInstanceValid(value);
    private static void SetServiceStatus(RacePage page) => page.Status.SetTextAutoSize(
        RaceTextCatalog.Get(page.Controller.Services is DemoRaceServices
            ? "status.demo"
            : page.Controller.Services.ConfiguredServerUri is null ? "status.p2p_only" : "status.online"));
    private static string OnOff(bool value) => RaceTextCatalog.CurrentLanguage == "zhs" ? value ? "开启" : "关闭" : value ? "On" : "Off";

    private sealed record OriginalModifierChoice(string Id, string Title, string Description);

    private static IReadOnlyList<OriginalModifierChoice> OriginalModifierChoices()
    {
        var result = new List<OriginalModifierChoice>();
        foreach (var canonical in ModelDb.GoodModifiers.Concat(ModelDb.BadModifiers))
        {
            if (canonical is CharacterCards characterCards)
            {
                foreach (var character in ModelDb.AllCharacters)
                {
                    var variant = (CharacterCards)characterCards.ToMutable();
                    variant.CharacterModel = character.Id;
                    result.Add(new OriginalModifierChoice(
                        $"{canonical.Id.Entry}:{character.Id.Entry}",
                        variant.Title.GetFormattedText(),
                        variant.Description.GetFormattedText()));
                }
                continue;
            }
            result.Add(new OriginalModifierChoice(canonical.Id.Entry, canonical.Title.GetFormattedText(), canonical.Description.GetFormattedText()));
        }
        return result;
    }

    private static bool IsExclusiveDeckModifier(string id)
    {
        var entry = id.Split(':', 2)[0];
        return entry.Equals("Draft", StringComparison.OrdinalIgnoreCase) ||
               entry.Equals("SealedDeck", StringComparison.OrdinalIgnoreCase) ||
               entry.Equals("Insanity", StringComparison.OrdinalIgnoreCase);
    }
    private static string QueueKindName(QueueKind kind) => RaceTextCatalog.CurrentLanguage == "zhs"
        ? kind switch { QueueKind.Casual => "普通匹配", QueueKind.Ranked => "排位赛", _ => "娱乐模式" }
        : kind switch { QueueKind.Casual => "Casual", QueueKind.Ranked => "Ranked", _ => "Entertainment" };
    private static string TierLabel(RankedPool pool) => RaceTextCatalog.Get(pool == RankedPool.Solo ? "rank.solo" : "rank.team");
    private static string Roman(int division) => division switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => "" };
    private static Color TierColor(string tier) => tier switch
    {
        "Bronze" => new Color("b6744c"), "Silver" => new Color("bdc5cb"), "Gold" => new Color("e1b849"),
        "Platinum" => new Color("6db5a7"), "Diamond" => new Color("70aee8"), "Legend" => new Color("d17ada"), _ => StsColors.gold
    };
    private static string TierName(string tier) => RaceTextCatalog.CurrentLanguage == "zhs" ? tier switch
    {
        "Bronze" => "青铜", "Silver" => "白银", "Gold" => "黄金", "Platinum" => "铂金", "Diamond" => "钻石", "Legend" => "传说", _ => tier
    } : tier;
    private static string LocalizeTierWords(string value)
    {
        foreach (var tier in new[] { "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Legend" })
            value = value.Replace(tier, TierName(tier), StringComparison.Ordinal);
        if (RaceTextCatalog.CurrentLanguage == "zhs")
            value = value.Replace("Season Mark", "赛季印记").Replace("Rank Banner", "排位旗帜").Replace("Title Seal", "称号纹章").Replace("Spire Crown", "尖塔王冠");
        return value;
    }
    private static string HistoryProgress(bool completed, int highestFloor, TimeSpan elapsed)
    {
        if (!completed && highestFloor <= 0 && elapsed == TimeSpan.Zero)
            return RaceTextCatalog.Get("profile.history.unknown");
        if (completed)
            return RaceTextCatalog.Format("profile.history.completed", RaceUiAssets.FormatTime(elapsed));
        return RaceTextCatalog.Format("profile.history.floor", highestFloor, RaceUiAssets.FormatTime(elapsed));
    }

    private static string[] HistoryDetails(MatchHistoryEntry match)
    {
        var paragraphs = new List<string>
        {
            $"ID  {match.MatchId}",
            $"{(match.Victory ? "WIN" : "LOSS")}   ·   {(int)match.TeamSize}v{(int)match.TeamSize}",
            RaceTextCatalog.Format("profile.history.local", CharacterName(match.Character),
                HistoryProgress(match.Completed, match.HighestFloor, match.RunTime)),
            RaceTextCatalog.Format("profile.history.opponents",
                match.OpponentNames is { Count: > 0 } ? string.Join(" / ", match.OpponentNames) : RaceTextCatalog.Get("profile.history.unknown"),
                match.OpponentCharacters is { Count: > 0 }
                    ? string.Join(" / ", match.OpponentCharacters.Select(CharacterName))
                    : RaceTextCatalog.Get("profile.history.unknown")),
            RaceTextCatalog.Format("profile.history.enemy_progress",
                HistoryProgress(match.OpponentCompleted, match.OpponentHighestFloor, match.OpponentRunTime)),
            RaceTextCatalog.Format("result.rating", match.RatingDelta)
        };
        if (match.SeriesGames is { Count: > 0 })
        {
            paragraphs.Add("BO3");
            paragraphs.AddRange(match.SeriesGames.Select(game =>
                $"G{game.GameNumber}   {(game.WinnerTeamId == match.LocalTeamId ? RaceTextCatalog.Get("result.local_side") : RaceTextCatalog.Get("result.opponent_side"))}" +
                $"   {CharacterName(game.CharacterId)}   {RaceRules.FormatElapsed(game.ElapsedMilliseconds)}   {SettlementReason(game.Reason)}"));
        }
        return paragraphs.ToArray();
    }

    private static string CharacterName(string id) => RaceTextCatalog.CurrentLanguage == "zhs" ? id switch
    {
        "Ironclad" => "铁甲战士", "Silent" => "静默猎手", "Defect" => "故障机器人", "Necrobinder" => "亡灵契约师", "Regent" => "储君", _ => id
    } : id;
    private static readonly string[] PlayableCharacters = ["Ironclad", "Silent", "Defect", "Necrobinder", "Regent"];
    private static bool IsPlayableCharacter(string value) => PlayableCharacters.Contains(value, StringComparer.Ordinal);
    private static string SettlementReason(FinishReason reason) => RaceTextCatalog.Get($"result.reason.{reason.ToString().ToLowerInvariant()}");
    private static string SettlementOutcome(ParticipantOutcome outcome) => RaceTextCatalog.Get($"result.outcome.{outcome.ToString().ToLowerInvariant()}");
    private static string DisplayTitle(string title) => RaceTextCatalog.CurrentLanguage == "zhs" ? title switch
    {
        "First Step" => "初登者", "Spire Wind" => "尖塔疾风", "Minute Hand" => "分秒必争", "No Rest" => "永不停歇",
        "Team Heart" => "同心攀登", "Eight as One" => "八人一心", "Chaos Tamer" => "混沌驯服者", "Perfect Line" => "完美路线",
        "Seed Keeper" => "种子守望者", "Comeback" => "逆风翻盘", "Fog Piercer" => "破雾者", "Clock Breaker" => "碎钟者",
        "Unbroken" => "不屈连胜", "Season Pioneer" => "赛季先锋", "Top One Hundred" => "百强竞速者", "Legend of the Spire" => "尖塔传说", _ => title
    } : title;
    private static string LocalizeTitleDescription(TitleDefinition title) => RaceTextCatalog.CurrentLanguage == "zhs" ? $"竞速称号：{DisplayTitle(title.Name)}" : title.Description;
    private static string LocalizeUnlockCondition(TitleDefinition title) => RaceTextCatalog.CurrentLanguage == "zhs" ? $"完成第 {int.Parse(title.Id[^2..])} 项竞速挑战" : title.UnlockCondition;
    private static string LocalizeMission(string value) => RaceTextCatalog.CurrentLanguage == "zhs" ? value switch
    {
        "First Bell" => "第一声钟响", "Complete one race" => "完成一场竞速", "Against Time" => "对抗时间",
        "Finish under 50 minutes" => "在50分钟内完成竞速", "Team Climb" => "团队攀登", "Win three team races" => "赢得三场团队竞速",
        "Many Faces" => "百变阵容", "Race with four characters" => "使用四名不同角色完成竞速", _ => value
    } : value;
    private static string LocalizeReward(string value) => RaceTextCatalog.CurrentLanguage == "zhs" ? value switch
    {
        "Season Mark" => "赛季印记", "Rank Banner" => "排位旗帜", "Title Seal" => "称号纹章", _ => value
    } : value;
}
