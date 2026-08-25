using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace Sts2SpireRace.UI;

public sealed partial class RaceTextureButton : NButton
{
    private MegaLabel _label = null!;
    private TextureRect _background = null!;
    private Color _normalTint = new("41636f");
    private Color _focusTint = new("658c96");
    private Tween? _tween;

    public event Action? Activated;

    public string ButtonText { get; set; } = string.Empty;
    public int FontSize { get; set; } = 24;
    public bool Compact { get; set; }
    public Color NormalTint { get => _normalTint; set => _normalTint = value; }
    public Color FocusTint { get => _focusTint; set => _focusTint = value; }

    public override void _Ready()
    {
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        PivotOffset = Size * 0.5f;

        _background = new TextureRect
        {
            Name = "Background",
            Texture = RaceUiAssets.Texture(RaceUiAssets.ActionButtonTexture),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            SelfModulate = _normalTint
        };
        _background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_background);

        _label = RaceUiAssets.Label(ButtonText, FontSize, StsColors.cream, HorizontalAlignment.Center);
        _label.Name = "Label";
        _label.MouseFilter = MouseFilterEnum.Ignore;
        _label.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.KeepSize, Compact ? 4 : 10);
        AddChild(_label);
        ConnectSignals();
    }

    public void SetText(string text)
    {
        ButtonText = text;
        if (IsNodeReady())
            _label.SetTextAutoSize(text);
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        _tween?.Kill();
        _background.SelfModulate = _focusTint;
        _tween = CreateTween().SetParallel();
        _tween.TweenProperty(this, "scale", Vector2.One * 1.025f, 0.08);
        _tween.TweenProperty(_label, "self_modulate", StsColors.gold, 0.08);
    }

    protected override void OnUnfocus()
    {
        _tween?.Kill();
        _tween = CreateTween().SetParallel();
        _tween.TweenProperty(this, "scale", Vector2.One, 0.25).SetEase(Tween.EaseType.Out);
        _tween.TweenProperty(_background, "self_modulate", _normalTint, 0.25);
        _tween.TweenProperty(_label, "self_modulate", Colors.White, 0.25);
    }

    protected override void OnPress()
    {
        base.OnPress();
        Scale = Vector2.One * 0.97f;
    }

    protected override void OnRelease()
    {
        Activated?.Invoke();
        OnUnfocus();
    }

    protected override void OnDisable()
    {
        Modulate = StsColors.quarterTransparentWhite;
    }

    protected override void OnEnable()
    {
        Modulate = Colors.White;
    }
}
