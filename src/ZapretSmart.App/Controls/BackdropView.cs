using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ZapretSmart.App.Controls.Backdrops;
using ZapretSmart.App.Theming;

namespace ZapretSmart.App.Controls;

/// <summary>
/// Фон темы: бинарный дождь, снег и т. п. Анимируется, только пока окно видно и не свёрнуто.
/// Если в Windows выключены эффекты анимации или движение фона выключено в настройках, рисуется один неподвижный кадр.
/// </summary>
public sealed class BackdropView : Control
{
    public static readonly StyledProperty<BackdropKind> KindProperty =
        AvaloniaProperty.Register<BackdropView, BackdropKind>(nameof(Kind));

    public static readonly StyledProperty<Color> EffectColorProperty =
        AvaloniaProperty.Register<BackdropView, Color>(nameof(EffectColor), Colors.White);

    public static readonly StyledProperty<bool> AnimatedProperty =
        AvaloniaProperty.Register<BackdropView, bool>(nameof(Animated), true);

    /// <summary>Зерно случайности: одинаковое у превью и у тестов, чтобы кадр был воспроизводим.</summary>
    public static readonly StyledProperty<int> SeedProperty =
        AvaloniaProperty.Register<BackdropView, int>(nameof(Seed), 7);

    private static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

    private readonly Stopwatch _clock = new();
    private IBackdropEffect? _effect;
    private DispatcherTimer? _timer;
    private TimeSpan _last;
    private Window? _window;
    private Size _size;

    public BackdropView()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public BackdropKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Color EffectColor
    {
        get => GetValue(EffectColorProperty);
        set => SetValue(EffectColorProperty, value);
    }

    public bool Animated
    {
        get => GetValue(AnimatedProperty);
        set => SetValue(AnimatedProperty, value);
    }

    public int Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    /// <summary>Идёт ли сейчас анимация. Для тестов и отладки.</summary>
    public bool IsRunning => _timer?.IsEnabled == true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == EffectColorProperty || change.Property == SeedProperty)
        {
            Rebuild();
        }
        else if (change.Property == AnimatedProperty || change.Property == IsVisibleProperty)
        {
            UpdateTimer();
        }
        else if (change.Property == BoundsProperty)
        {
            var size = Bounds.Size;
            if (size == _size) return;
            _size = size;
            _effect?.Resize(size);
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowPropertyChanged;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        UpdateTimer();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) UpdateTimer();
    }

    private void Rebuild()
    {
        (_effect as IDisposable)?.Dispose();
        _effect = BackdropEffects.Create(Kind, EffectColor, Seed);
        if (_effect is not null && _size.Width > 0 && _size.Height > 0) _effect.Resize(_size);
        UpdateTimer();
        InvalidateVisual();
    }

    private bool ShouldRun() =>
        _effect is not null && Animated && !Motion.IsReduced && IsEffectivelyVisible
        && _window is { IsVisible: true } && _window.WindowState != WindowState.Minimized;

    private void UpdateTimer()
    {
        if (!ShouldRun())
        {
            _timer?.Stop();
            _clock.Stop();
            return;
        }
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick += (_, _) => Step();
        }
        _timer.Interval = _effect!.FrameInterval;
        if (_timer.IsEnabled) return;
        _clock.Restart();
        _last = TimeSpan.Zero;
        _timer.Start();
    }

    private void Step()
    {
        if (_effect is null) return;
        var now = _clock.Elapsed;
        var dt = now - _last;
        _last = now;
        // После паузы (окно было свёрнуто, система притормозила) не прыгаем вперёд.
        if (dt > MaxStep) dt = MaxStep;
        _effect.Advance(dt.TotalSeconds);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_effect is null || _size.Width < 1 || _size.Height < 1) return;
        _effect.Render(context, _size);
    }
}
