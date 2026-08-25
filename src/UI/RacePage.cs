using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace Sts2SpireRace.UI;

public sealed partial class RacePage : NSubmenu
{
    private Action<RacePage>? _builder;
    private readonly List<Action> _cleanup = [];
    private Control? _initialFocus;
    private Control? _backButton;
    private string _title = string.Empty;

    public VBoxContainer Content { get; private set; } = null!;
    public MegaCrit.Sts2.addons.mega_text.MegaLabel Status { get; private set; } = null!;
    public RaceUiController Controller { get; private set; } = null!;

    protected override Control? InitialFocusedControl => _initialFocus;

    public RacePage Configure(RaceUiController controller, string title, Action<RacePage> builder)
    {
        Controller = controller;
        _title = title;
        _builder = builder;
        return this;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        var title = RaceUiAssets.Label(_title, 44, StsColors.gold, HorizontalAlignment.Center, bold: true);
        title.SetAnchorsPreset(LayoutPreset.TopWide);
        title.OffsetLeft = 240;
        title.OffsetTop = 42;
        title.OffsetRight = -240;
        title.OffsetBottom = 104;
        AddChild(title);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 86);
        margin.AddThemeConstantOverride("margin_top", 120);
        margin.AddThemeConstantOverride("margin_right", 86);
        margin.AddThemeConstantOverride("margin_bottom", 102);
        AddChild(margin);
        Content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        Content.AddThemeConstantOverride("separation", 14);
        margin.AddChild(Content);

        Status = RaceUiAssets.Label(string.Empty, 18, StsColors.lightGray, HorizontalAlignment.Center);
        Status.SetAnchorsPreset(LayoutPreset.BottomWide);
        Status.OffsetLeft = 280;
        Status.OffsetTop = -78;
        Status.OffsetRight = -280;
        Status.OffsetBottom = -40;
        AddChild(Status);

        _backButton = ResourceLoader.Load<PackedScene>(RaceUiAssets.BackButtonScene).Instantiate<Control>();
        _backButton.Name = "BackButton";
        AddChild(_backButton);
        _builder?.Invoke(this);
        ConnectSignals();
    }

    public void SetInitialFocus(Control control)
    {
        _initialFocus ??= control;
    }

    public void AddCleanup(Action cleanup) => _cleanup.Add(cleanup);

    public void SetBackVisible(bool visible)
    {
        if (_backButton is not null && GodotObject.IsInstanceValid(_backButton))
            _backButton.Visible = visible;
    }

    public void ClearContent()
    {
        foreach (var child in Content.GetChildren())
        {
            Content.RemoveChild(child);
            child.QueueFree();
        }
        _initialFocus = null;
    }

    public override void OnSubmenuClosed()
    {
        base.OnSubmenuClosed();
        foreach (var cleanup in _cleanup)
            cleanup();
        _cleanup.Clear();
        QueueFree();
    }
}
