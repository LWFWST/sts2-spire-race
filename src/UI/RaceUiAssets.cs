using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2SpireRace.Core;

namespace Sts2SpireRace.UI;

public static class RaceUiAssets
{
    public const string ParchmentCardScene = "res://scenes/ui/submenu_button.tscn";
    public const string BackButtonScene = "res://scenes/ui/back_button.tscn";
    public const string PanelTexture = "res://images/ui/tiny_nine_patch.png";
    public const string ShortPanelTexture = "res://images/packed/common_ui/submenu_panel_short.png";
    public const string ActionButtonTexture = "res://images/ui/reward_screen/reward_skip_button.png";
    public const string BannerTexture = "res://images/packed/run_history/banner.png";
    public const string StandardIcon = "res://images/ui/main_menu/submenu_standard.png";
    public const string DailyIcon = "res://images/ui/main_menu/submenu_daily.png";
    public const string CustomIcon = "res://images/ui/main_menu/submenu_custom.png";
    public const string ProfileIcon = "res://images/packed/main_menu/submenu_stats_icon.png";
    public const string FriendsIcon = "res://images/ui/main_menu/submenu_join.png";
    public const string LeaderboardIcon = "res://images/packed/main_menu/submenu_leaderboards_icon.png";
    public const string TitleIcon = "res://images/packed/main_menu/submenu_trophy_icon.png";
    public const string ActivityIcon = "res://images/ui/main_menu/submenu_daily.png";
    public const string FontRegular = "res://themes/kreon_regular_shared.tres";
    public const string FontBold = "res://themes/kreon_bold_glyph_space_two.tres";
    public const string FontMono = "res://themes/source_code_pro_semibold_shared.tres";

    public static Texture2D Texture(string path) => ResourceLoader.Load<Texture2D>(path);
    public static Font Font(string path) => ResourceLoader.Load<Font>(path);

    public static MegaLabel Label(
        string text,
        int size = 24,
        Color? color = null,
        HorizontalAlignment alignment = HorizontalAlignment.Left,
        bool bold = false)
    {
        var label = new MegaLabel
        {
            Text = text,
            AutoSizeEnabled = false,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeFontOverride(ThemeConstants.Label.Font, Font(bold ? FontBold : FontRegular));
        label.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, size);
        label.AddThemeColorOverride(ThemeConstants.Label.FontColor, color ?? StsColors.cream);
        label.AddThemeColorOverride(ThemeConstants.Label.FontOutlineColor, new Color(0, 0, 0, 0.72f));
        label.AddThemeConstantOverride(ThemeConstants.Label.OutlineSize, size >= 34 ? 8 : 4);
        label.ApplyLocaleFontSubstitution(bold ? FontType.Bold : FontType.Regular, ThemeConstants.Label.Font);
        return label;
    }

    public static NinePatchRect Panel(Color tint, int margin = 14)
    {
        var panel = new NinePatchRect
        {
            Texture = Texture(PanelTexture),
            SelfModulate = tint,
            PatchMarginLeft = margin,
            PatchMarginTop = margin,
            PatchMarginRight = margin,
            PatchMarginBottom = margin,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        return panel;
    }

    public static VBoxContainer PanelSection(Control parent, Color tint, int padding = 18, int separation = 10)
    {
        var panel = Panel(tint);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        panel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        parent.AddChild(panel);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", padding);
        margin.AddThemeConstantOverride("margin_top", padding);
        margin.AddThemeConstantOverride("margin_right", padding);
        margin.AddThemeConstantOverride("margin_bottom", padding);
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.AddChild(margin);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", separation);
        margin.AddChild(box);
        return box;
    }

    public static RaceTextureButton Button(string text, Action action, int fontSize = 24, Vector2? minimum = null)
    {
        var button = new RaceTextureButton
        {
            ButtonText = text,
            FontSize = fontSize,
            CustomMinimumSize = minimum ?? new Vector2(190, 62),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        button.Activated += action;
        return button;
    }

    public static NSubmenuButton ParchmentCard(
        string title,
        string description,
        string iconPath,
        Color tint,
        Action action)
    {
        var card = ResourceLoader.Load<PackedScene>(ParchmentCardScene).Instantiate<NSubmenuButton>();
        card.CustomMinimumSize = new Vector2(310, 580);
        card.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        card.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        card.GetNode<MegaLabel>("%Title").SetTextAutoSize(title);
        card.GetNode<MegaRichTextLabel>("%Description").Text = description;
        card.GetNode<TextureRect>("Icon").Texture = Texture(iconPath);
        var bg = card.GetNode<TextureRect>("BgPanel");
        if (bg.Material is ShaderMaterial source)
        {
            var material = (ShaderMaterial)source.Duplicate();
            material.SetShaderParameter("h", tint.H);
            material.SetShaderParameter("s", Math.Max(0.45f, tint.S));
            material.SetShaderParameter("v", Math.Max(0.55f, tint.V));
            bg.Material = material;
        }
        card.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => action()));
        return card;
    }

    public static Control ShortCard(string title, string iconPath, Color tint, Action action)
    {
        var button = new RaceTextureButton
        {
            Name = title,
            ButtonText = title,
            FontSize = 20,
            CustomMinimumSize = new Vector2(190, 116),
            NormalTint = tint,
            FocusTint = tint.Lightened(0.22f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        button.Activated += action;
        var icon = new TextureRect
        {
            Texture = Texture(iconPath),
            CustomMinimumSize = new Vector2(72, 72),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(8, 20),
            Size = new Vector2(72, 72),
            ZIndex = 2
        };
        button.AddChild(icon);
        return button;
    }

    public static LineEdit LineEdit(string placeholder, string value = "")
    {
        var input = new LineEdit
        {
            PlaceholderText = placeholder,
            Text = value,
            CustomMinimumSize = new Vector2(240, 52),
            ClearButtonEnabled = true
        };
        input.AddThemeFontOverride(ThemeConstants.LineEdit.Font, Font(FontRegular));
        input.AddThemeFontSizeOverride("font_size", 21);
        input.AddThemeColorOverride("font_color", StsColors.cream);
        input.AddThemeColorOverride("caret_color", StsColors.gold);
        input.ApplyLocaleFontSubstitution(FontType.Regular, ThemeConstants.LineEdit.Font);
        return input;
    }

    public static string FormatTime(TimeSpan time) => RaceRules.FormatElapsed((long)time.TotalMilliseconds);
}
