using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ZapretSmart.App.Theming;
using ZapretSmart.App.ViewModels;

namespace ZapretSmart.App.Views;

public partial class MainWindow : Window
{
    /// <summary>ease-out с сильным стартом: движение видно в первый же кадр.</summary>
    private static readonly Easing EaseOut = new SplineEasing(0.23, 1, 0.32, 1);

    private static readonly TimeSpan ThemeFade = TimeSpan.FromMilliseconds(260);

    private readonly Animation _pageEnter;
    private CancellationTokenSource? _pageAnimation;
    private bool _keyboardNavigation;
    private MainWindowViewModel? _vm;
    private bool _logAtBottom = true;
    private CancellationTokenSource? _copiedReset;

    public MainWindow()
    {
        InitializeComponent();
        if (Motion.IsReduced) Classes.Add("reduced-motion");
        _pageEnter = Motion.IsReduced ? Fade(TimeSpan.FromMilliseconds(120)) : RiseAndFade(TimeSpan.FromMilliseconds(180), 6);

        // Вкладки переключают часто: переход короткий и почти без движения, только чтобы смена не была рывком.
        // С клавиатуры не анимируем вовсе: стрелками листают быстро, и анимация только отставала бы от нажатий.
        AddHandler(KeyDownEvent, (_, _) => _keyboardNavigation = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => _keyboardNavigation = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != Tabs || Tabs.SelectedContent is not Visual page) return;
            _pageAnimation?.Cancel();
            _pageAnimation = new CancellationTokenSource();
            if (_keyboardNavigation)
            {
                page.Opacity = 1;
                return;
            }
            _ = _pageEnter.RunAsync(page, _pageAnimation.Token);
        };

        if (!Motion.IsReduced && DeleteButton.Flyout is Flyout flyout)
            flyout.FlyoutPresenterClasses.Add("animated");

        LogScroll.ScrollChanged += (_, _) =>
            _logAtBottom = LogScroll.Offset.Y + LogScroll.Viewport.Height >= LogScroll.Extent.Height - 8;
    }

    /// <summary>Выход из меню трея: закрыть по-настоящему, а не спрятать.</summary>
    public bool ExitRequested { get; set; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Log.CollectionChanged -= OnLogChanged;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Log.CollectionChanged += OnLogChanged;
        ApplyTheme(_vm.Theme, animate: false);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Theme) && _vm is not null)
            ApplyTheme(_vm.Theme, animate: IsVisible);
    }

    /// <summary>
    /// Смена темы: снимок окна в старой теме кладётся поверх и гаснет, под ним уже новая тема.
    /// Так вся палитра меняется одним плавным движением, а не каждый элемент в свой момент.
    /// </summary>
    public void ApplyTheme(AppTheme theme, bool animate)
    {
        Image? overlay = null;
        RenderTargetBitmap? snapshot = null;
        if (animate && Frame.Bounds is { Width: > 0, Height: > 0 } bounds)
        {
            var scale = RenderScaling;
            snapshot = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale)),
                new Vector(96 * scale, 96 * scale));
            snapshot.Render(Frame);
            overlay = new Image
            {
                Source = snapshot, Stretch = Stretch.Fill, Width = bounds.Width, Height = bounds.Height,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
            OverlayHost.Children.Add(overlay);
        }

        ThemeResources.Apply(Application.Current!, theme);
        Backdrop.Kind = theme.Backdrop;
        Backdrop.EffectColor = theme.Effect;

        if (overlay is null) return;
        _ = FadeOutAsync(overlay, snapshot!);
    }

    private async Task FadeOutAsync(Image overlay, RenderTargetBitmap snapshot)
    {
        try
        {
            await new Animation
            {
                Duration = ThemeFade,
                Easing = EaseOut,
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0d) } },
                },
            }.RunAsync(overlay);
        }
        finally
        {
            OverlayHost.Children.Remove(overlay);
            snapshot.Dispose();
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Держимся за последней строкой, только если пользователь сам не прокрутил журнал вверх, чтобы что-то прочитать.
        if (e.Action != NotifyCollectionChangedAction.Add || !_logAtBottom) return;
        Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, _vm.Log));
        CopyLogButton.Content = "Скопировано";
        _copiedReset?.Cancel();
        _copiedReset = new CancellationTokenSource();
        try
        {
            await Task.Delay(1500, _copiedReset.Token);
            CopyLogButton.Content = "Скопировать";
        }
        catch (TaskCanceledException)
        {
        }
    }

    /// <summary>
    /// Сначала удаление, потом закрытие. Не Command на кнопке: закрытие подсказки отвязывает её содержимое,
    /// привязка Command становится null, и кнопка, получив Click раньше команды, закрывала вопрос, ничего не удалив.
    /// </summary>
    /// <summary>Как с удалением: сначала команда, потом закрытие подсказки, иначе привязка к команде отвалилась бы раньше.</summary>
    private void OnInstallConfirmed(object? sender, RoutedEventArgs e)
    {
        InstallButton.Flyout?.Hide();
        var install = _vm?.Updates.InstallCommand;
        if (install?.CanExecute(null) == true) install.Execute(null);
    }

    private void OnDeleteConfirmed(object? sender, RoutedEventArgs e)
    {
        var delete = _vm?.Editor.DeleteCommand;
        if (delete?.CanExecute(null) == true) delete.Execute(null);
        DeleteButton.Flyout?.Hide();
    }

    private static Animation Fade(TimeSpan duration) => new()
    {
        Duration = duration,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } },
        },
    };

    private static Animation RiseAndFade(TimeSpan duration, double offset) => new()
    {
        Duration = duration,
        Easing = EaseOut,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, offset) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
        },
    };
}
