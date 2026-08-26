using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using Sts2SpireRace.Core;
using Sts2SpireRace.UI;

namespace Sts2SpireRace.Game;

[HarmonyPatch(typeof(NPauseMenu), "OnSaveAndQuitButtonPressed", [typeof(NButton)])]
internal static class RaceSaveAndQuitPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NPauseMenu __instance, NButton __0)
    {
        if (RaceActiveSession.Current is not { } active || RaceServiceRegistry.Services is not IRaceMatchService matches)
            return true;
        if (active.Kind == QueueKind.Entertainment && active.Rules.CoordinationMode == "p2p")
            return true;
        _ = HandleAsync(__instance, matches);
        return false;
    }

    private static async Task HandleAsync(NPauseMenu menu, IRaceMatchService matches)
    {
        var category = NRun.Instance?.CombatRoom is null ? SlCategory.Event : SlCategory.Combat;
        try
        {
            await matches.RequestSaveAndQuitAsync(category, confirmForfeit: false);
            RacePendingSave.Set(matches.CurrentMatch!.GameId, category);
            await CloseOriginalMenuAsync(menu);
        }
        catch (InvalidOperationException)
        {
            var overlay = new RaceSlExhaustedOverlay { Name = "SpireRaceSlExhausted" };
            overlay.ZIndex = 1000;
            overlay.Configure(menu, matches, category);
            NRun.Instance!.GlobalUi.AddChild(overlay);
            overlay.Build();
        }
    }

    internal static async Task CloseOriginalMenuAsync(NPauseMenu menu)
    {
        if (AccessTools.Method(typeof(NPauseMenu), "CloseToMenu").Invoke(menu, null) is Task task)
            await task;
    }
}

[HarmonyPatch(typeof(NPauseMenu), "OnGiveUpButtonPressed", [typeof(NButton)])]
internal static class RaceSurrenderPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NButton __0)
    {
        if (RaceActiveSession.Current is null || RaceServiceRegistry.Services is not IRaceMatchService matches)
            return true;
        Log.Info("[SpireRace] Original Give Up button intercepted as race surrender.");
        RaceSurrenderOverlay.Show(matches);
        return false;
    }
}

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.Initialize))]
internal static class RacePauseMenuInitializePatch
{
    [HarmonyPostfix]
    private static void Postfix(NPauseMenu __instance)
    {
        if (RaceActiveSession.Current is null)
            return;
        if (AccessTools.Field(typeof(NPauseMenu), "_giveUpButton").GetValue(__instance) is NPauseMenuButton button)
        {
            button.Visible = true;
            button.Enable();
            Log.Info("[SpireRace] Give Up button enabled for active race.");
        }
    }
}

[HarmonyPatch(typeof(NAbandonRunConfirmPopup), "OnYesButtonPressed", [typeof(NButton)])]
internal static class RaceAbandonConfirmPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NAbandonRunConfirmPopup __instance)
    {
        if (RaceActiveSession.Current is null || RaceServiceRegistry.Services is not IRaceMatchService matches)
            return true;
        Log.Info("[SpireRace] Original abandon confirmation intercepted as race surrender.");
        __instance.QueueFree();
        _ = SurrenderAndReturnAsync(matches);
        return false;
    }

    private static async Task SurrenderAndReturnAsync(IRaceMatchService matches)
    {
        var match = matches.CurrentMatch ?? RaceActiveSession.Current;
        var settlement = await RaceSettlementWaiter.SurrenderAndWaitAsync(matches);
        if (settlement is not null)
            Callable.From(() => RaceSettlementOverlay.Show(matches, settlement, match)).CallDeferred();
    }
}

public sealed partial class RaceSurrenderOverlay : Control
{
    private IRaceMatchService _matches = null!;
    private bool _built;
    public static void Show(IRaceMatchService matches)
    {
        var globalUi = NRun.Instance?.GlobalUi;
        if (globalUi is null || globalUi.GetNodeOrNull<Node>("SpireRaceSurrender") is not null)
            return;
        var overlay = new RaceSurrenderOverlay { Name = "SpireRaceSurrender", ZIndex = 1000 };
        overlay.Configure(matches);
        globalUi.AddChild(overlay);
        overlay.Build();
    }
    public void Configure(IRaceMatchService matches) => _matches = matches;
    public override void _Ready() => Build();

    public void Build()
    {
        if (_built)
            return;
        _built = true;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Stop;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.75f), MouseFilter = MouseFilterEnum.Stop }; shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(shade);
        var panel = RaceUiAssets.Panel(new Color("4a292c"), 18); panel.SetAnchorsPreset(LayoutPreset.Center); panel.Position = new Vector2(-340, -140); panel.Size = new Vector2(680, 280); AddChild(panel);
        var content = new VBoxContainer(); content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.KeepSize, 30); content.AddThemeConstantOverride("separation", 18); panel.AddChild(content);
        var team = (_matches.CurrentMatch ?? RaceActiveSession.Current)!.TeamSize != TeamSize.One;
        content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get(team ? "surrender.vote.title" : "surrender.title"), 34, StsColors.gold, HorizontalAlignment.Center, true));
        content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get(team ? "surrender.vote.body" : "surrender.body"), 21, StsColors.cream, HorizontalAlignment.Center));
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; actions.AddThemeConstantOverride("separation", 20);
        var cancel = RaceUiAssets.Button(RaceTextCatalog.Get("common.cancel"), QueueFree, 23);
        var confirm = RaceUiAssets.Button(RaceTextCatalog.Get("common.confirm"), () => _ = ConfirmAsync(), 23);
        actions.AddChild(cancel); actions.AddChild(confirm); content.AddChild(actions); cancel.GrabFocus();
    }
    private async Task ConfirmAsync()
    {
        var match = _matches.CurrentMatch ?? RaceActiveSession.Current;
        var teamSize = match?.TeamSize ?? TeamSize.One;
        if (teamSize == TeamSize.One)
        {
            var settlement = await RaceSettlementWaiter.SurrenderAndWaitAsync(_matches);
            QueueFree();
            if (settlement is not null)
                Callable.From(() => RaceSettlementOverlay.Show(_matches, settlement, match)).CallDeferred();
            return;
        }
        await _matches.VoteSurrenderAsync(true);
        QueueFree();
    }
}

internal static class RaceSettlementWaiter
{
    public static async Task<SettlementSnapshot?> SurrenderAndWaitAsync(IRaceMatchService matches)
    {
        if (matches.CurrentSettlement is { } existing)
            return existing;
        var completion = new TaskCompletionSource<SettlementSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Settled(SettlementSnapshot value) => completion.TrySetResult(value);
        matches.MatchSettled += Settled;
        try
        {
            await matches.VoteSurrenderAsync(true);
            if (matches.CurrentSettlement is { } immediate)
                return immediate;
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { return matches.CurrentSettlement; }
        }
        finally
        {
            matches.MatchSettled -= Settled;
        }
    }
}

public sealed partial class RaceSlExhaustedOverlay : Control
{
    private NPauseMenu _menu = null!;
    private IRaceMatchService _matches = null!;
    private SlCategory _category;
    private bool _built;

    public void Configure(NPauseMenu menu, IRaceMatchService matches, SlCategory category)
    {
        _menu = menu; _matches = matches; _category = category;
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
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(shade);
        var panel = RaceUiAssets.Panel(new Color("4a292c"), 18);
        panel.SetAnchorsPreset(LayoutPreset.Center); panel.Position = new Vector2(-360, -150); panel.Size = new Vector2(720, 300); AddChild(panel);
        var content = new VBoxContainer(); content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.KeepSize, 30); content.AddThemeConstantOverride("separation", 20); panel.AddChild(content);
        content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("sl.exhausted.title"), 34, StsColors.gold, HorizontalAlignment.Center, true));
        content.AddChild(RaceUiAssets.Label(RaceTextCatalog.Get("sl.exhausted.body"), 21, StsColors.cream, HorizontalAlignment.Center));
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; actions.AddThemeConstantOverride("separation", 20);
        var cancel = RaceUiAssets.Button(RaceTextCatalog.Get("common.cancel"), QueueFree, 23);
        var forfeit = RaceUiAssets.Button(RaceTextCatalog.Get("sl.forfeit"), () => _ = ForfeitAsync(), 23);
        actions.AddChild(cancel); actions.AddChild(forfeit); content.AddChild(actions); cancel.GrabFocus();
    }

    private async Task ForfeitAsync()
    {
        await _matches.RequestSaveAndQuitAsync(_category, confirmForfeit: true);
        QueueFree();
        await RaceSaveAndQuitPatch.CloseOriginalMenuAsync(_menu);
    }
}

internal static class RacePendingSave
{
    private static readonly Dictionary<string, SlCategory> PendingGames = [];
    private static readonly object Sync = new();
    public static void Set(string gameId, SlCategory category) { lock (Sync) PendingGames[gameId] = category; }
    public static bool TryConsume(string gameId, out SlCategory category)
    {
        lock (Sync)
        {
            if (!PendingGames.Remove(gameId, out category))
            {
                category = SlCategory.Event;
                return false;
            }
            return true;
        }
    }
}
