using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace ZapretSmart.App.Theming;

/// <summary>
/// Тема целиком превращается в один словарь ресурсов: наши кисти Zs* и ключи Fluent, которые иначе красили бы
/// кнопки, поля и переключатели в системные цвета. Словарь подменяется в Application.Resources.MergedDictionaries
/// одним действием, и все {DynamicResource} перечитываются разом.
/// </summary>
public static class ThemeResources
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New, monospace");

    /// <summary>Inter из пакета Avalonia.Fonts.Inter. FontFamily.Default тут не годится: он отдаёт системный шрифт, а не Inter.</summary>
    public static readonly FontFamily Sans = new("fonts:Inter#Inter, $Default");

    private static ResourceDictionary? _applied;

    public static void Apply(Application app, AppTheme theme)
    {
        var dict = Build(theme);
        var merged = app.Resources.MergedDictionaries;
        var i = _applied is null ? -1 : merged.IndexOf(_applied);
        if (i >= 0) merged[i] = dict;
        else merged.Add(dict);
        _applied = dict;
        app.RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public static ResourceDictionary Build(AppTheme theme)
    {
        var p = theme.Palette;
        var d = new ResourceDictionary();
        var toward = theme.IsLight ? Colors.Black : Colors.White;

        var raisedHover = Mix(p.SurfaceRaised, p.Text, 0.07);
        var raisedPressed = Mix(p.SurfaceRaised, p.Background, 0.5);
        var borderStrong = Mix(p.Border, p.Text, 0.18);
        var accentHover = Mix(p.Accent, toward, 0.12);
        var accentPressed = Mix(p.Accent, p.Background, 0.18);
        var dangerHover = WithAlpha(p.Danger, 0.12);
        var input = theme.IsLight ? p.Background : Mix(p.Background, p.Surface, 0.35);

        // Свои кисти.
        d["ZsBackground"] = B(p.Background);
        d["ZsSurface"] = B(WithAlpha(p.Surface, theme.PanelOpacity));
        d["ZsSidebar"] = B(WithAlpha(p.Surface, theme.PanelOpacity < 1 ? theme.PanelOpacity - 0.14 : 1));
        d["ZsSurfaceRaised"] = B(p.SurfaceRaised);
        d["ZsBorder"] = B(p.Border);
        d["ZsBorderStrong"] = B(borderStrong);
        d["ZsText"] = B(p.Text);
        d["ZsTextMuted"] = B(p.TextMuted);
        d["ZsAccent"] = B(p.Accent);
        d["ZsAccentHover"] = B(accentHover);
        d["ZsAccentPressed"] = B(accentPressed);
        d["ZsAccentText"] = B(p.AccentText);
        d["ZsAccentSoft"] = B(WithAlpha(p.Accent, theme.IsLight ? 0.12 : 0.16));
        d["ZsDanger"] = B(p.Danger);
        d["ZsDangerSoft"] = B(dangerHover);
        d["ZsWarning"] = B(p.Warning);
        d["ZsWarningSoft"] = B(WithAlpha(p.Warning, 0.14));
        d["ZsHover"] = B(WithAlpha(p.Text, 0.05));
        d["ZsCode"] = B(theme.IsLight ? p.Background : Mix(p.Background, p.Surface, 0.2));
        d["ZsCardRadius"] = new CornerRadius(theme.CardRadius);
        d["ZsControlRadius"] = new CornerRadius(theme.ControlRadius);
        d["ZsFont"] = theme.Monospace ? Mono : Sans;
        d["ZsMonoFont"] = Mono;

        // Акцент Fluent: прогресс, выделение текста, фокус.
        d["SystemAccentColor"] = p.Accent;
        d["SystemAccentColorLight1"] = Mix(p.Accent, Colors.White, 0.15);
        d["SystemAccentColorLight2"] = Mix(p.Accent, Colors.White, 0.3);
        d["SystemAccentColorLight3"] = Mix(p.Accent, Colors.White, 0.45);
        d["SystemAccentColorDark1"] = Mix(p.Accent, Colors.Black, 0.15);
        d["SystemAccentColorDark2"] = Mix(p.Accent, Colors.Black, 0.3);
        d["SystemAccentColorDark3"] = Mix(p.Accent, Colors.Black, 0.45);
        d["ControlCornerRadius"] = new CornerRadius(theme.ControlRadius);
        d["OverlayCornerRadius"] = new CornerRadius(theme.ControlRadius + 2);

        // Кнопки без класса: второстепенные действия.
        d["ButtonBackground"] = B(p.SurfaceRaised);
        d["ButtonBackgroundPointerOver"] = B(raisedHover);
        d["ButtonBackgroundPressed"] = B(raisedPressed);
        d["ButtonBackgroundDisabled"] = B(WithAlpha(p.SurfaceRaised, 0.5));
        d["ButtonForeground"] = B(p.Text);
        d["ButtonForegroundPointerOver"] = B(p.Text);
        d["ButtonForegroundPressed"] = B(p.Text);
        d["ButtonForegroundDisabled"] = B(WithAlpha(p.TextMuted, 0.6));
        d["ButtonBorderBrush"] = B(p.Border);
        d["ButtonBorderBrushPointerOver"] = B(borderStrong);
        d["ButtonBorderBrushPressed"] = B(p.Border);
        d["ButtonBorderBrushDisabled"] = B(WithAlpha(p.Border, 0.5));
        d["ButtonBorderThemeThickness"] = new Thickness(1);

        // Поля ввода. Фокус: рамка акцентом той же толщины, без скачка раскладки.
        foreach (var state in new[] { "", "PointerOver", "Focused" })
        {
            d["TextControlBackground" + state] = B(input);
            d["TextControlForeground" + state] = B(p.Text);
            d["TextControlPlaceholderForeground" + state] = B(p.TextMuted);
        }
        d["TextControlBackgroundDisabled"] = B(WithAlpha(input, 0.5));
        d["TextControlForegroundDisabled"] = B(p.TextMuted);
        d["TextControlPlaceholderForegroundDisabled"] = B(WithAlpha(p.TextMuted, 0.6));
        d["TextControlBorderBrush"] = B(p.Border);
        d["TextControlBorderBrushPointerOver"] = B(borderStrong);
        d["TextControlBorderBrushFocused"] = B(p.Accent);
        d["TextControlBorderBrushDisabled"] = B(WithAlpha(p.Border, 0.5));
        d["TextControlBorderThemeThickness"] = new Thickness(1);
        d["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        d["TextControlSelectionHighlightColor"] = B(WithAlpha(p.Accent, 0.4));
        d["TextControlButtonForeground"] = B(p.TextMuted);
        d["TextControlButtonForegroundPointerOver"] = B(p.Text);

        d["ComboBoxBackground"] = B(input);
        d["ComboBoxBackgroundPointerOver"] = B(input);
        d["ComboBoxBackgroundPressed"] = B(input);
        d["ComboBoxBackgroundUnfocused"] = B(input);
        d["ComboBoxBackgroundDisabled"] = B(WithAlpha(input, 0.5));
        d["ComboBoxBorderBrush"] = B(p.Border);
        d["ComboBoxBorderBrushPointerOver"] = B(borderStrong);
        d["ComboBoxBorderBrushPressed"] = B(p.Accent);
        d["ComboBoxBorderBrushDisabled"] = B(WithAlpha(p.Border, 0.5));
        d["ComboBoxForeground"] = B(p.Text);
        d["ComboBoxForegroundFocused"] = B(p.Text);
        d["ComboBoxForegroundFocusedPressed"] = B(p.Text);
        d["ComboBoxForegroundDisabled"] = B(p.TextMuted);
        d["ComboBoxPlaceHolderForeground"] = B(p.TextMuted);
        d["ComboBoxDropDownGlyphForeground"] = B(p.TextMuted);
        d["ComboBoxDropDownGlyphForegroundDisabled"] = B(WithAlpha(p.TextMuted, 0.5));
        d["ComboBoxDropDownBackground"] = B(p.SurfaceRaised);
        d["ComboBoxDropDownBorderBrush"] = B(p.Border);
        d["ComboBoxItemForeground"] = B(p.Text);
        d["ComboBoxItemForegroundPointerOver"] = B(p.Text);
        d["ComboBoxItemForegroundPressed"] = B(p.Text);
        d["ComboBoxItemForegroundSelected"] = B(p.Text);
        d["ComboBoxItemForegroundSelectedPointerOver"] = B(p.Text);
        d["ComboBoxItemForegroundSelectedPressed"] = B(p.Text);
        d["ComboBoxItemBackgroundPointerOver"] = B(WithAlpha(p.Text, 0.06));
        d["ComboBoxItemBackgroundPressed"] = B(WithAlpha(p.Text, 0.1));
        d["ComboBoxItemBackgroundSelected"] = B(WithAlpha(p.Accent, 0.18));
        d["ComboBoxItemBackgroundSelectedPointerOver"] = B(WithAlpha(p.Accent, 0.24));
        d["ComboBoxItemBackgroundSelectedPressed"] = B(WithAlpha(p.Accent, 0.28));

        d["AutoCompleteBoxSuggestionsListBackground"] = B(p.SurfaceRaised);
        d["AutoCompleteBoxSuggestionsListBorderBrush"] = B(p.Border);

        // Флажки: галочка цветом текста на акценте, а не белая (на светлом бирюзовом белое не читается).
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            d["CheckBoxForegroundChecked" + state] = B(state == "Disabled" ? p.TextMuted : p.Text);
            d["CheckBoxForegroundUnchecked" + state] = B(state == "Disabled" ? p.TextMuted : p.Text);
            d["CheckBoxCheckGlyphForegroundChecked" + state] = B(p.AccentText);
            d["CheckBoxBackgroundChecked" + state] = Brushes.Transparent;
            d["CheckBoxBackgroundUnchecked" + state] = Brushes.Transparent;
            d["CheckBoxBorderBrushChecked" + state] = Brushes.Transparent;
            d["CheckBoxBorderBrushUnchecked" + state] = Brushes.Transparent;
        }
        d["CheckBoxCheckBackgroundFillChecked"] = B(p.Accent);
        d["CheckBoxCheckBackgroundFillCheckedPointerOver"] = B(accentHover);
        d["CheckBoxCheckBackgroundFillCheckedPressed"] = B(accentPressed);
        d["CheckBoxCheckBackgroundFillCheckedDisabled"] = B(WithAlpha(p.Accent, 0.4));
        d["CheckBoxCheckBackgroundFillUnchecked"] = B(input);
        d["CheckBoxCheckBackgroundFillUncheckedPointerOver"] = B(input);
        d["CheckBoxCheckBackgroundFillUncheckedPressed"] = B(raisedPressed);
        d["CheckBoxCheckBackgroundFillUncheckedDisabled"] = B(WithAlpha(input, 0.5));
        d["CheckBoxCheckBackgroundStrokeUnchecked"] = B(p.TextMuted);
        d["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = B(p.Text);
        d["CheckBoxCheckBackgroundStrokeUncheckedPressed"] = B(p.TextMuted);
        d["CheckBoxCheckBackgroundStrokeUncheckedDisabled"] = B(WithAlpha(p.TextMuted, 0.4));
        d["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = B(accentHover);
        d["CheckBoxCheckBackgroundStrokeCheckedPressed"] = B(accentPressed);
        d["CheckBoxCheckBackgroundStrokeCheckedDisabled"] = B(WithAlpha(p.Accent, 0.4));

        d["ToggleSwitchContentForeground"] = B(p.Text);
        d["ToggleSwitchFillOn"] = B(p.Accent);
        d["ToggleSwitchFillOnPointerOver"] = B(accentHover);
        d["ToggleSwitchFillOnPressed"] = B(accentPressed);
        d["ToggleSwitchFillOnDisabled"] = B(WithAlpha(p.Accent, 0.4));
        d["ToggleSwitchStrokeOn"] = B(p.Accent);
        d["ToggleSwitchStrokeOnPointerOver"] = B(accentHover);
        d["ToggleSwitchStrokeOnPressed"] = B(accentPressed);
        d["ToggleSwitchKnobFillOn"] = B(p.AccentText);
        d["ToggleSwitchKnobFillOnPointerOver"] = B(p.AccentText);
        d["ToggleSwitchKnobFillOnPressed"] = B(p.AccentText);
        d["ToggleSwitchFillOff"] = Brushes.Transparent;
        d["ToggleSwitchFillOffPointerOver"] = B(WithAlpha(p.Text, 0.05));
        d["ToggleSwitchFillOffPressed"] = B(WithAlpha(p.Text, 0.1));
        d["ToggleSwitchStrokeOff"] = B(p.TextMuted);
        d["ToggleSwitchStrokeOffPointerOver"] = B(p.Text);
        d["ToggleSwitchStrokeOffPressed"] = B(p.TextMuted);
        d["ToggleSwitchKnobFillOff"] = B(p.TextMuted);
        d["ToggleSwitchKnobFillOffPointerOver"] = B(p.Text);
        d["ToggleSwitchKnobFillOffPressed"] = B(p.TextMuted);

        d["FlyoutPresenterBackground"] = B(p.SurfaceRaised);
        d["FlyoutBorderThemeBrush"] = B(p.Border);
        d["ToolTipBackground"] = B(p.SurfaceRaised);
        d["ToolTipForeground"] = B(p.Text);
        d["ToolTipBorderBrush"] = B(p.Border);
        d["TabItemHeaderSelectedPipeFill"] = B(p.Accent);
        return d;
    }

    public static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * c.A), c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Контраст по WCAG 2.x: 1..21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static ImmutableSolidColorBrush B(Color c) => new(c);
}
