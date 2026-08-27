using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using Sts2SpireRace.Game;
using Sts2SpireRace.UI;

namespace Sts2SpireRace.Replay;

/// <summary>
/// Replay controls built from the same textured panels, fonts and buttons as
/// the rest of Spire Race. The embedded replay engine deliberately does not
/// bring over the standalone Replay Mod's default Godot UI.
/// </summary>
public sealed partial class ReplayControlsOverlay : CanvasLayer
{
    private ReplayPlaybackController _controller = null!;
    private MegaCrit.Sts2.addons.mega_text.MegaLabel _state = null!;
    private RaceTextureButton _playPause = null!;

    public static ReplayControlsOverlay Create(ReplayPlaybackController controller)
    {
        var overlay = new ReplayControlsOverlay { Layer = 120, _controller = controller };
        overlay.Build();
        return overlay;
    }

    private void Build()
    {
        var panel = RaceUiAssets.Panel(new Color("263f43"), 16);
        panel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        panel.OffsetLeft = 70;
        panel.OffsetRight = -70;
        panel.OffsetTop = -132;
        panel.OffsetBottom = -18;
        AddChild(panel);

        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 12);
        root.AddThemeConstantOverride("separation", 7);
        panel.AddChild(root);
        _state = RaceUiAssets.Label(string.Empty, 18, StsColors.gold, HorizontalAlignment.Center, true);
        root.AddChild(_state);
        var actions = ActionRow();
        _playPause = AddButton(actions, RaceTextCatalog.Get("replay.play"), () =>
        {
            if (_controller.IsPlaying) _controller.Pause();
            else TaskHelper.RunSafely(_controller.PlayAsync());
        });
        AddButton(actions, RaceTextCatalog.Get("replay.previous"), () =>
            TaskHelper.RunSafely(_controller.SeekAsync(Math.Max(0, _controller.CurrentMarkerIndex - 1))));
        AddButton(actions, RaceTextCatalog.Get("replay.next"), () =>
            TaskHelper.RunSafely(_controller.SeekAsync(Math.Min(_controller.MarkerCount - 1, _controller.CurrentMarkerIndex + 1))));
        AddButton(actions, RaceTextCatalog.Get("replay.step"), () => TaskHelper.RunSafely(_controller.StepAsync()));
        AddSpeedButtons(actions, _controller.SetSpeed);
        AddButton(actions, RaceTextCatalog.Get("replay.exit"), _controller.ExitToMainMenu);
        root.AddChild(actions);
        _playPause.GrabFocus();
    }

    public void UpdateState(bool playing, int marker, int count, string label, double speed)
    {
        _playPause.SetText(RaceTextCatalog.Get(playing ? "replay.pause" : "replay.play"));
        _state.SetTextAutoSize(RaceTextCatalog.Format("replay.state", marker + 1, count, label, speed));
    }

    private static HBoxContainer ActionRow() => new()
    {
        Alignment = BoxContainer.AlignmentMode.Center,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
    };

    private static RaceTextureButton AddButton(Container row, string text, Action action)
    {
        var button = RaceUiAssets.Button(text, action, 15, new Vector2(100, 38));
        row.AddChild(button);
        return button;
    }

    private static void AddSpeedButtons(Container row, Action<double> setSpeed)
    {
        foreach (var speed in new[] { 0.5, 1.0, 2.0, 4.0 })
        {
            var captured = speed;
            AddButton(row, $"{speed:0.#}×", () => setSpeed(captured), 60);
        }
    }

    private static RaceTextureButton AddButton(Container row, string text, Action action, float width)
    {
        var button = RaceUiAssets.Button(text, action, 15, new Vector2(width, 38));
        row.AddChild(button);
        return button;
    }
}

public sealed partial class RunReplayControlsOverlay : CanvasLayer
{
    private RunReplayPlaybackController _controller = null!;
    private MegaCrit.Sts2.addons.mega_text.MegaLabel _state = null!;
    private RaceTextureButton _playPause = null!;
    private double _guardElapsed;
    private MegaCrit.Sts2.addons.mega_text.MegaLabel? _target;

    public static RunReplayControlsOverlay Create(RunReplayPlaybackController controller)
    {
        var overlay = new RunReplayControlsOverlay { Layer = 120, _controller = controller };
        overlay.Build();
        return overlay;
    }

    private void Build()
    {
        var panel = RaceUiAssets.Panel(new Color("263f43"), 16);
        panel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        panel.OffsetLeft = 70;
        panel.OffsetRight = -70;
        panel.OffsetTop = RaceReplayCloudCoordinator.CanSwitchLiveTarget ? -182 : -140;
        panel.OffsetBottom = -18;
        AddChild(panel);
        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 12);
        root.AddThemeConstantOverride("separation", 7);
        panel.AddChild(root);
        _state = RaceUiAssets.Label(string.Empty, 18, StsColors.gold, HorizontalAlignment.Center, true);
        root.AddChild(_state);
        if (RaceReplayCloudCoordinator.CanSwitchLiveTarget)
        {
            var targets = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            AddButton(targets, RaceTextCatalog.Get("spectate.previous_target"), () =>
                TaskHelper.RunSafely(RaceReplayCloudCoordinator.SwitchLiveTargetAsync(-1)), 150);
            _target = RaceUiAssets.Label(RaceReplayCloudCoordinator.CurrentLiveTarget, 17, StsColors.gold, HorizontalAlignment.Center, true);
            _target.CustomMinimumSize = new Vector2(320, 38);
            targets.AddChild(_target);
            AddButton(targets, RaceTextCatalog.Get("spectate.next_target"), () =>
                TaskHelper.RunSafely(RaceReplayCloudCoordinator.SwitchLiveTargetAsync(1)), 150);
            root.AddChild(targets);
        }
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _playPause = AddButton(actions, RaceTextCatalog.Get("replay.play"), () =>
        {
            if (_controller.IsPlaying) _controller.Pause();
            else TaskHelper.RunSafely(_controller.PlayAsync());
        });
        AddButton(actions, RaceTextCatalog.Get("replay.previous_floor"), () =>
            TaskHelper.RunSafely(_controller.SeekAsync(Math.Max(0, _controller.MarkerIndex - 1))), 135);
        AddButton(actions, RaceTextCatalog.Get("replay.next_floor"), () =>
            TaskHelper.RunSafely(_controller.SeekAsync(Math.Min(_controller.MarkerCount - 1, _controller.MarkerIndex + 1))), 135);
        AddButton(actions, RaceTextCatalog.Get("replay.step"), () => TaskHelper.RunSafely(_controller.StepAsync()));
        foreach (var speed in new[] { 0.5, 1.0, 2.0, 4.0 })
        {
            var captured = speed;
            AddButton(actions, $"{speed:0.#}×", () => _controller.SetSpeed(captured), 60);
        }
        AddButton(actions, RaceTextCatalog.Get("replay.exit"), _controller.Exit, 130);
        root.AddChild(actions);
        _playPause.GrabFocus();
    }

    public override void _Process(double delta)
    {
        _guardElapsed += delta;
        if (_guardElapsed < 0.1) return;
        _guardElapsed = 0;
        if (NRun.Instance is not null) ReplayUiInteractionPolicy.ApplyReadOnlyState(NRun.Instance);
    }

    public void UpdateState(bool playing, RunReplayMarker checkpoint, int checkpointCount,
        int eventIndex, int eventCount, long elapsedMs, long durationMs, string label, double speed, bool canTakeOver)
    {
        _target?.SetTextAutoSize(RaceReplayCloudCoordinator.CurrentLiveTarget);
        _playPause.SetText(RaceTextCatalog.Get(playing ? "replay.pause_after_action" : "replay.play"));
        _state.SetTextAutoSize(RaceTextCatalog.Format("replay.run_state", FormatTime(elapsedMs), FormatTime(durationMs),
            checkpoint.Act, checkpoint.Floor, label, eventIndex, eventCount, checkpoint.Index + 1, checkpointCount, speed));
    }

    private static RaceTextureButton AddButton(Container row, string text, Action action, float width = 100)
    {
        var button = RaceUiAssets.Button(text, action, 16, new Vector2(width, 38));
        row.AddChild(button);
        return button;
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}

public static class BranchStatusOverlay
{
    private static CanvasLayer? _layer;

    public static void Show(string name)
    {
        Hide();
        _layer = new CanvasLayer { Layer = 110 };
        var panel = RaceUiAssets.Panel(new Color("263f43"), 10);
        panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        panel.OffsetLeft = -430;
        panel.OffsetRight = -24;
        panel.OffsetTop = 24;
        panel.OffsetBottom = 76;
        panel.AddChild(RaceUiAssets.Label(name, 18, StsColors.gold, HorizontalAlignment.Center, true));
        _layer.AddChild(panel);
        NGame.Instance?.AddChild(_layer);
    }

    public static void Hide()
    {
        _layer?.QueueFree();
        _layer = null;
    }
}
