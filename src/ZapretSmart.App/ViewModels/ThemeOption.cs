using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.App.Theming;

namespace ZapretSmart.App.ViewModels;

/// <summary>Плитка темы в настройках. Кисти превью — цвета самой темы, а не текущей: иначе все плитки выглядели бы одинаково.</summary>
public sealed partial class ThemeOption(AppTheme theme, Action<AppTheme> select) : ObservableObject
{
    public AppTheme Theme { get; } = theme;
    public string Name => Theme.Name;
    public string Tagline => Theme.Tagline;

    public IBrush BackgroundBrush { get; } = new ImmutableSolidColorBrush(theme.Palette.Background);
    public IBrush SurfaceBrush { get; } = new ImmutableSolidColorBrush(theme.Palette.Surface);
    public IBrush TextBrush { get; } = new ImmutableSolidColorBrush(theme.Palette.Text);
    public IBrush MutedBrush { get; } = new ImmutableSolidColorBrush(theme.Palette.TextMuted);
    public IBrush AccentBrush { get; } = new ImmutableSolidColorBrush(theme.Palette.Accent);

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => select(Theme);
}
