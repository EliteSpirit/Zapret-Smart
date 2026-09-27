using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ZapretSmart.App.Views;

public partial class MainWindow : Window
{
    /// <summary>ease-out с сильным стартом: движение видно в первый же кадр.</summary>
    private static readonly Easing EaseOut = new SplineEasing(0.23, 1, 0.32, 1);

    private readonly Animation _pageEnter;

    public MainWindow()
    {
        InitializeComponent();
        if (Motion.IsReduced) Classes.Add("reduced-motion");
        _pageEnter = Motion.IsReduced ? Fade(TimeSpan.FromMilliseconds(120)) : RiseAndFade(TimeSpan.FromMilliseconds(180), 6);

        // Вкладки переключают часто: переход короткий и почти без движения, только чтобы смена не была рывком.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == Tabs && Tabs.SelectedContent is Visual page)
                _ = _pageEnter.RunAsync(page);
        };
    }

    /// <summary>Выход из меню трея: закрыть по-настоящему, а не спрятать.</summary>
    public bool ExitRequested { get; set; }

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
