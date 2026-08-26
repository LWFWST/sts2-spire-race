using System.Collections.Concurrent;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Runs;
using Sts2SpireRace.Core;
using Sts2SpireRace.UI;

namespace Sts2SpireRace.Game;

[HarmonyPatch(typeof(NRun), nameof(NRun._Ready))]
internal static class RaceRunReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix(NRun __instance)
    {
        var match = RaceActiveSession.Current ?? (RaceServiceRegistry.Services as IRaceMatchService)?.CurrentMatch;
        if (match is null)
        {
            Log.Info("[SpireRace] NRun ready without an active race session; HUD not attached.");
            return;
        }
        try { NDevConsole.Instance.HideConsole(); } catch (InvalidOperationException) { }
        var field = AccessTools.Field(typeof(NRun), "_state");
        if (field?.GetValue(__instance) is not RunState state)
        {
            Log.Error("[SpireRace] NRun ready, but its RunState could not be resolved.");
            return;
        }
        var integration = new RaceRunIntegration { Name = "SpireRaceRunIntegration" };
        integration.Configure(state, match);
        __instance.AddChild(integration);
        integration.InitializeRaceRun();
        Log.Info($"[SpireRace] Race run integration attached for {match.MatchId}/{match.GameId}.");
    }
}

public sealed partial class RaceRunIntegration : Node
{
    private RunState _state = null!;
    private MatchAssignment _match = null!;
    private IRaceMatchService _matches = null!;
    private IRaceClockService? _clock;
    private ProgressCheckpoint? _checkpoint;
    private bool _deathShown;
    private bool _finishedReported;
    private bool _initialized;
    private RaceRunHud? _hud;
    private bool IsP2PEntertainment => _match.Kind == QueueKind.Entertainment && _match.Rules.CoordinationMode == "p2p";

    public void Configure(RunState state, MatchAssignment match)
    {
        _state = state;
        _match = match;
        _matches = (IRaceMatchService)RaceServiceRegistry.Services;
        _clock = RaceServiceRegistry.Services as IRaceClockService;
    }

    public override void _Ready() => InitializeRaceRun();

    public void InitializeRaceRun()
    {
        if (_initialized)
            return;
        _initialized = true;
        var match = _matches.CurrentMatch ?? _match;
        _checkpoint = new ProgressCheckpoint(match.MatchId, match.GameId, match.LocalTeam.Id,
            RaceTelemetrySequence.Next(match.GameId), 0, 0, false, null, ParticipantOutcome.Active,
            RaceTelemetrySequence.Restarts(match.GameId), RaceTelemetrySequence.EventSlUsed(match.GameId), RaceTelemetrySequence.CombatSlUsed(match.GameId));
        if (RacePendingSave.TryConsume(match.GameId, out var savedCategory))
        {
            RaceTelemetrySequence.IncrementSl(match.GameId, savedCategory);
            _checkpoint = _checkpoint with
            {
                EventSlUsed = RaceTelemetrySequence.EventSlUsed(match.GameId),
                CombatSlUsed = RaceTelemetrySequence.CombatSlUsed(match.GameId)
            };
            _ = _matches.ResumeSavedRunAsync($"resume:{match.GameId}:{Guid.NewGuid():N}");
        }
        RunManager.Instance.RoomEntered += OnRoomEntered;
        _matches.MatchSettled += OnMatchSettled;
        _hud = new RaceRunHud { Name = "SpireRaceHud" };
        _hud.Configure(match, _clock, _matches);
        NRun.Instance!.GlobalUi.AddChild(_hud);
        _hud.Build();
        _hud.UpdateProgress(_checkpoint.Floor, _checkpoint.EventSlUsed, _checkpoint.CombatSlUsed);
        OnRoomEntered();
        Log.Info($"[SpireRace] In-run HUD is visible for {match.MatchId}/{match.GameId}.");
    }

    public override void _ExitTree()
    {
        RunManager.Instance.RoomEntered -= OnRoomEntered;
        _matches.MatchSettled -= OnMatchSettled;
    }

    public override void _Process(double delta)
    {
        if (_checkpoint is null || _finishedReported)
            return;
        if (RunManager.Instance.WinTime > 0)
        {
            _finishedReported = true;
            var elapsed = ElapsedMilliseconds();
            _ = ReportAsync(_checkpoint with
            {
                Sequence = RaceTelemetrySequence.Next(_checkpoint.GameId),
                FinalBossDefeated = true,
                CompletedAtMilliseconds = elapsed,
                Outcome = ParticipantOutcome.Finished
            });
            return;
        }
        if (_state.IsGameOver && !_deathShown)
            ShowDeathChoice();
    }

    internal void ShowDeathChoice()
    {
        if (_deathShown || _finishedReported || !IsInsideTree() || NRun.Instance?.GlobalUi is null)
            return;
        _deathShown = true;
        var overlay = new RaceDeathChoiceOverlay { Name = "SpireRaceDeathChoice", ZIndex = 1000 };
        overlay.Configure(this, RaceRules.DeathDecisionSeconds);
        NRun.Instance.GlobalUi.AddChild(overlay);
        overlay.Build();
    }

    private void OnMatchSettled(SettlementSnapshot settlement)
    {
        Callable.From(() => RaceSettlementOverlay.Show(_matches, settlement, _match)).CallDeferred();
    }

    public async Task KeepScoreAsync()
    {
        if (_checkpoint is null) return;
        _finishedReported = true;
        await _matches.ChooseDeathActionAsync(false);
        await ReportAsync(_checkpoint with
        {
            Sequence = RaceTelemetrySequence.Next(_checkpoint.GameId),
            Outcome = ParticipantOutcome.ScoreLocked
        });
    }

    public async Task RestartAsync()
    {
        if (_checkpoint is null) return;
        var match = _matches.CurrentMatch ?? _match;
        var restarts = RaceTelemetrySequence.IncrementRestarts(match.GameId);
        await _matches.ChooseDeathActionAsync(true);
        await ReportAsync(_checkpoint with
        {
            Sequence = RaceTelemetrySequence.Next(_checkpoint.GameId),
            RestartCount = restarts,
            Outcome = ParticipantOutcome.Active,
            FinalBossDefeated = false,
            CompletedAtMilliseconds = null
        });
        await NGame.Instance!.ReturnToMainMenu();
        await RaceServiceRegistry.Services.SessionLauncher.LaunchAsync(match);
    }

    private void OnRoomEntered()
    {
        if (_checkpoint is null)
            return;
        var floor = _state.TotalFloor;
        if (floor <= _checkpoint.Floor)
            return;
        _checkpoint = _checkpoint with
        {
            Sequence = RaceTelemetrySequence.Next(_checkpoint.GameId),
            Floor = floor,
            FloorEnteredAtMilliseconds = ElapsedMilliseconds()
        };
        _hud?.UpdateProgress(_checkpoint.Floor, _checkpoint.EventSlUsed, _checkpoint.CombatSlUsed);
        _ = ReportAsync(_checkpoint);
    }

    private async Task ReportAsync(ProgressCheckpoint checkpoint)
    {
        _checkpoint = checkpoint;
        await _matches.ReportProgressAsync(checkpoint, $"{checkpoint.GameId}:{checkpoint.Sequence}");
    }

    private long ElapsedMilliseconds()
    {
        if (IsP2PEntertainment)
            return Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _match.StartedAtUnixMilliseconds);
        if (_clock?.CurrentClock is { IsSynchronized: true } snapshot)
            return Math.Max(0, snapshot.ElapsedMilliseconds +
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.ServerUnixMilliseconds));
        return RunManager.Instance.RunTime * 1000;
    }
}

[HarmonyPatch(typeof(NRun), nameof(NRun.ShowGameOverScreen))]
internal static class RaceGameOverScreenPatch
{
    [HarmonyPostfix]
    private static void Postfix(NRun __instance)
    {
        if (RaceActiveSession.Current is null)
            return;
        var integration = __instance.GetNodeOrNull<RaceRunIntegration>("SpireRaceRunIntegration");
        if (integration is not null)
            Callable.From(integration.ShowDeathChoice).CallDeferred();
    }
}

[HarmonyPatch(typeof(NDevConsole), nameof(NDevConsole._Input))]
internal static class RaceDevConsoleInputPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NDevConsole __instance)
    {
        if (RaceActiveSession.Current is null)
            return true;
        if (__instance.Visible)
            __instance.HideConsole();
        return false;
    }
}

public sealed partial class RaceRunHud : Control
{
    private MatchAssignment _match = null!;
    private IRaceClockService? _clock;
    private IRaceMatchService _matches = null!;
    private MegaCrit.Sts2.addons.mega_text.MegaLabel _label = null!;
    private long _snapshotAtTicks;
    private ServerClockSnapshot _snapshot = new(0, 0, 0, 0, false);
    private int _floor;
    private int _eventSlUsed;
    private int _combatSlUsed;
    private bool _built;
    private Godot.Timer? _refreshTimer;
    private long _localStartedAtTicks;

    public void Configure(MatchAssignment match, IRaceClockService? clock, IRaceMatchService matches) { _match = match; _clock = clock; _matches = matches; }

    public void UpdateProgress(int floor, int eventSlUsed, int combatSlUsed)
    {
        _floor = floor;
        _eventSlUsed = eventSlUsed;
        _combatSlUsed = combatSlUsed;
        Refresh();
    }

    public override void _Ready() => Build();

    public void Build()
    {
        if (_built)
            return;
        _built = true;
        _localStartedAtTicks = checked((long)Time.GetTicksMsec());
        SetAnchorsPreset(LayoutPreset.TopWide);
        OffsetLeft = 370;
        OffsetTop = 150;
        OffsetRight = -370;
        OffsetBottom = 206;
        ZIndex = 500;
        MouseFilter = MouseFilterEnum.Pass;
        var panel = RaceUiAssets.Panel(new Color("173943"), 12);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        panel.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(panel);
        var row = new HBoxContainer();
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.KeepSize, 6);
        row.AddThemeConstantOverride("separation", 10);
        panel.AddChild(row);
        _label = RaceUiAssets.Label(string.Empty, 19, StsColors.gold, HorizontalAlignment.Center, true);
        _label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(_label);
        var surrender = RaceUiAssets.Button(RaceTextCatalog.Get("hud.surrender"), () => RaceSurrenderOverlay.Show(_matches), 17, new Vector2(150, 44));
        surrender.NormalTint = new Color("763d42");
        row.AddChild(surrender);
        if (_clock is not null)
        {
            _clock.ClockChanged += OnClockChanged;
            OnClockChanged(_clock.CurrentClock);
        }
        _refreshTimer = new Godot.Timer
        {
            Name = "RealtimeClockRefresh",
            WaitTime = 1.0 / 60.0,
            OneShot = false,
            Autostart = true
        };
        _refreshTimer.Timeout += Refresh;
        AddChild(_refreshTimer);
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_clock is not null) _clock.ClockChanged -= OnClockChanged;
        if (_refreshTimer is not null) _refreshTimer.Timeout -= Refresh;
    }

    private void OnClockChanged(ServerClockSnapshot snapshot) { _snapshot = snapshot; _snapshotAtTicks = checked((long)Time.GetTicksMsec()); }
    private void Refresh()
    {
        if (_label is null) return;
        var now = checked((long)Time.GetTicksMsec());
        var elapsed = _snapshot.IsSynchronized
            ? _snapshot.ElapsedMilliseconds + now - _snapshotAtTicks
            : _match.Kind == QueueKind.Entertainment && _match.Rules.CoordinationMode == "p2p"
                ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _match.StartedAtUnixMilliseconds)
                : Math.Max(0, now - _localStartedAtTicks);
        var eventRemaining = Math.Max(0, _match.Rules.EventSlLimit - _eventSlUsed);
        var combatRemaining = Math.Max(0, _match.Rules.CombatSlLimit - _combatSlUsed);
        _label.SetTextAutoSize($"RACE  {RaceRules.FormatElapsed(elapsed)}     F{_floor}     A{_match.Rules.Ascension}     SL  {eventRemaining}E / {combatRemaining}C");
    }
}

public sealed partial class RaceDeathChoiceOverlay : Control
{
    private RaceRunIntegration _owner = null!;
    private int _seconds;
    private MegaCrit.Sts2.addons.mega_text.MegaLabel _countdown = null!;
    private double _accumulator;
    private bool _built;

    public void Configure(RaceRunIntegration owner, int seconds) { _owner = owner; _seconds = seconds; }

    public override void _Ready() => Build();

    public void Build()
    {
        if (_built)
            return;
        _built = true;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.72f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var panel = RaceUiAssets.Panel(new Color("263f43"), 18);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.Position = new Vector2(-360, -170);
        panel.Size = new Vector2(720, 340);
        AddChild(panel);
        var content = new VBoxContainer();
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.KeepSize, 30);
        content.AddThemeConstantOverride("separation", 18);
        panel.AddChild(content);
        content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("death.title"), 36, StsColors.gold, HorizontalAlignment.Center, true));
        content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("death.body"), 22, StsColors.cream, HorizontalAlignment.Center));
        _countdown = RaceUiAssets.Label(string.Empty, 21, StsColors.gold, HorizontalAlignment.Center);
        content.AddChild(_countdown);
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        actions.AddThemeConstantOverride("separation", 20);
        var keep = RaceUiAssets.Button(RaceTextCatalog.Get("death.keep"), () => _ = KeepAsync(), 23);
        var restart = RaceUiAssets.Button(RaceTextCatalog.Get("death.restart"), () => _ = RestartAsync(), 23);
        actions.AddChild(keep); actions.AddChild(restart); content.AddChild(actions);
        keep.GrabFocus(); RefreshCountdown();
    }

    public override void _Process(double delta)
    {
        _accumulator += delta;
        if (_accumulator < 1) return;
        _accumulator -= 1; _seconds--; RefreshCountdown();
        if (_seconds <= 0) _ = KeepAsync();
    }

    private void RefreshCountdown() => _countdown.SetTextAutoSize(RaceTextCatalog.Format("death.countdown", Math.Max(0, _seconds)));
    private async Task KeepAsync() { SetProcess(false); await _owner.KeepScoreAsync(); QueueFree(); }
    private async Task RestartAsync() { SetProcess(false); await _owner.RestartAsync(); QueueFree(); }
}

public sealed partial class RaceSettlementOverlay : Control
{
    private IRaceMatchService _matches = null!;
    private SettlementSnapshot _settlement = null!;
    private bool _built;

    public static void Show(IRaceMatchService matches, SettlementSnapshot settlement, MatchAssignment? match = null)
    {
        var globalUi = NRun.Instance?.GlobalUi;
        if (globalUi is null || globalUi.GetNodeOrNull<Node>("SpireRaceSettlement") is not null)
            return;
        var overlay = new RaceSettlementOverlay { Name = "SpireRaceSettlement", ZIndex = 1000 };
        overlay.Configure(matches, settlement, match);
        globalUi.AddChild(overlay);
        overlay.Build();
    }

    private MatchAssignment? _match;

    public void Configure(IRaceMatchService matches, SettlementSnapshot settlement, MatchAssignment? match = null)
    {
        _matches = matches;
        _settlement = settlement;
        _match = match;
    }

    public override void _Ready() => Build();

    public void Build()
    {
        if (_built)
            return;
        _built = true;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.75f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var teamMatch = _match ?? _matches.CurrentMatch;
        var victory = teamMatch is not null && _settlement.WinnerTeamId == teamMatch.LocalTeam.Id;
        var panel = RaceUiAssets.Panel(new Color("223c43"), 18);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.Position = new Vector2(-380, -220);
        panel.Size = new Vector2(760, 440);
        AddChild(panel);
        var content = new VBoxContainer();
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.KeepSize, 30);
        content.AddThemeConstantOverride("separation", 18);
        panel.AddChild(content);
        content.AddChild(RaceUiAssets.Label(
            RaceTextCatalog.Get(victory ? "result.victory" : "result.defeat"),
            46, victory ? StsColors.gold : new Color("cf6a70"), HorizontalAlignment.Center, true));
        content.AddChild(RaceUiAssets.Label(
            $"{RaceTextCatalog.Get("result.reason")}：{SettlementReason(_settlement.Reason)}",
            21, StsColors.cream, HorizontalAlignment.Center));
        content.AddChild(RaceUiAssets.Label(
            $"{SideSummary(RaceTextCatalog.Get("result.local_time"), _settlement.Local)}\n{SideSummary(RaceTextCatalog.Get("result.enemy_time"), _settlement.Opponent)}",
            19, StsColors.cream, HorizontalAlignment.Center));
        content.AddChild(RaceUiAssets.Label(
            RaceTextCatalog.Format("result.attempts", _settlement.Local.RestartCount, _settlement.Local.EventSlUsed, _settlement.Local.CombatSlUsed),
            18, StsColors.lightGray, HorizontalAlignment.Center));
        if (_settlement.VisibleRatingDelta != 0)
            content.AddChild(RaceUiAssets.Label(
                RaceTextCatalog.Format("result.rating", _settlement.VisibleRatingDelta), 22, StsColors.gold, HorizontalAlignment.Center));
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        actions.AddThemeConstantOverride("separation", 20);
        var menu = RaceUiAssets.Button(RaceTextCatalog.Get("common.to_menu"), () => _ = ReturnToMenuAsync(), 22);
        actions.AddChild(menu);
        content.AddChild(actions);
        menu.GrabFocus();
    }

    private async Task ReturnToMenuAsync()
    {
        RaceActiveSession.Clear();
        QueueFree();
        if (NGame.Instance is not null)
            await NGame.Instance.ReturnToMainMenu();
    }

    private static string SettlementReason(FinishReason reason) =>
        RaceTextCatalog.Get($"result.reason.{reason.ToString().ToLowerInvariant()}");

    private static string SideSummary(string label, SettlementSide side) => side.CompletionMilliseconds is { } elapsed
        ? $"{label}  {RaceUiAssets.FormatTime(TimeSpan.FromMilliseconds(elapsed))}"
        : $"{label}  {RaceTextCatalog.Format("result.floor", side.HighestFloor)}";
}

internal static class RaceTelemetrySequence
{
    private static readonly ConcurrentDictionary<string, long> Sequences = new();
    private static readonly ConcurrentDictionary<string, int> RestartCounts = new();
    private static readonly ConcurrentDictionary<string, int> EventSlCounts = new();
    private static readonly ConcurrentDictionary<string, int> CombatSlCounts = new();
    public static long Next(string gameId) => Sequences.AddOrUpdate(gameId, 1, (_, value) => value + 1);
    public static int Restarts(string gameId) => RestartCounts.GetValueOrDefault(gameId);
    public static int IncrementRestarts(string gameId) => RestartCounts.AddOrUpdate(gameId, 1, (_, value) => value + 1);
    public static int EventSlUsed(string gameId) => EventSlCounts.GetValueOrDefault(gameId);
    public static int CombatSlUsed(string gameId) => CombatSlCounts.GetValueOrDefault(gameId);
    public static void IncrementSl(string gameId, SlCategory category)
    {
        var counts = category == SlCategory.Combat ? CombatSlCounts : EventSlCounts;
        counts.AddOrUpdate(gameId, 1, (_, value) => value + 1);
    }
}
