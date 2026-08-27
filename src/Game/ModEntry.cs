using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using Sts2SpireRace.UI;
using Sts2SpireRace.Core;
using Sts2SpireRace.Replay;

namespace Sts2SpireRace.Game;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public static void Initialize()
    {
        _ = RaceServiceRegistry.Services;
        new Harmony("MCC.sts2-spire-race").PatchAll(typeof(ModEntry).Assembly);
        Log.Info($"[SpireRace] Competitive client initialized for {RaceRuntimeInfo.GameVersion}.");
    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class MainMenuInjectionPatch
{
    [HarmonyPrefix]
    private static void Prefix(NMainMenu __instance)
    {
        try
        {
            RaceUnlockIntegration.UnlockCurrentProfile();
            ReplayMod.TryInitialize();
            if (__instance.GetNodeOrNull<RaceUiController>("SpireRaceController") is not null)
                return;
            var container = __instance.GetNode<Control>("MainMenuTextButtons");
            var multiplayer = container.GetNode<NMainMenuTextButton>("MultiplayerButton");
            var sourceLabel = multiplayer.GetChild<MegaLabel>(0);
            var label = (MegaLabel)sourceLabel.Duplicate();
            label.Name = "Label";
            label.Text = RaceTextCatalog.Get("main_menu.race");

            var button = new NMainMenuTextButton
            {
                Name = "SpireRaceButton",
                CustomMinimumSize = multiplayer.CustomMinimumSize,
                SizeFlagsHorizontal = multiplayer.SizeFlagsHorizontal,
                SizeFlagsVertical = multiplayer.SizeFlagsVertical,
                FocusMode = Control.FocusModeEnum.All
            };
            button.AddChild(label);
            container.AddChild(button);
            container.MoveChild(button, multiplayer.GetIndex() + 1);

            var controller = new RaceUiController { Name = "SpireRaceController" };
            __instance.AddChild(controller);
            controller.Configure(__instance, label);
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => controller.OpenHub()));
            Log.Info("[SpireRace] Main-menu entry installed.");
        }
        catch (Exception exception)
        {
            Log.Error($"[SpireRace] Failed to inject main-menu entry: {exception}");
        }
    }
}

internal static class RaceUnlockIntegration
{
    private static object? _lastUnlockedProgress;

    public static void UnlockCurrentProfile()
    {
        var progress = SaveManager.Instance.Progress;
        if (ReferenceEquals(progress, _lastUnlockedProgress))
            return;
        var result = new UnlockConsoleCmd().Process(null, ["all"]);
        _lastUnlockedProgress = progress;
        Log.Info($"[SpireRace] Automatically unlocked all progression for the active profile: {result}");
    }
}
