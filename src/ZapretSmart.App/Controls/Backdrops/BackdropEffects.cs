using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ZapretSmart.App.Theming;

namespace ZapretSmart.App.Controls.Backdrops;

internal interface IBackdropEffect
{
    /// <summary>Шаг анимации. Дождю хватает 18 кадров в секунду (он и должен идти рывками), частицам нужно около 30.</summary>
    TimeSpan FrameInterval { get; }

    void Resize(Size size);

    void Advance(double seconds);

    void Render(DrawingContext context, Size size);
}

internal static class BackdropEffects
{
    public static IBackdropEffect? Create(BackdropKind kind, Color color, int seed) => kind switch
    {
        BackdropKind.BinaryRain => new BinaryRain(color, seed),
        BackdropKind.Halloween => new HalloweenNight(color, seed),
        BackdropKind.Snow => new Snowfall(color, seed),
        BackdropKind.Synthwave => new SynthwaveGrid(color, seed),
        _ => null,
    };

    /// <summary>Прогон вперёд, чтобы сцена была заполнена сразу, а не собиралась у пользователя на глазах.</summary>
    public static void Prewarm(IBackdropEffect effect, double seconds)
    {
        const double step = 1.0 / 20;
        for (var t = 0.0; t < seconds; t += step) effect.Advance(step);
    }
}

/// <summary>Кисти одного цвета с разной прозрачностью, созданные один раз: в кадре ничего не выделяется.</summary>
internal sealed class AlphaBrushes
{
    public const int Levels = 24;

    private readonly IImmutableSolidColorBrush[] _brushes = new IImmutableSolidColorBrush[Levels + 1];

    public AlphaBrushes(Color color)
    {
        for (var i = 0; i <= Levels; i++)
            _brushes[i] = new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(255.0 * i / Levels), color.R, color.G, color.B));
    }

    public IImmutableSolidColorBrush this[double alpha] => _brushes[Level(alpha)];

    /// <summary>NaN (0/0 на окне нулевой высоты) даёт 0, а не отрицательный индекс.</summary>
    public static int Level(double alpha) => double.IsNaN(alpha) ? 0 : (int)Math.Round(Math.Clamp(alpha, 0, 1) * Levels);
}
