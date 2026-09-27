using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ZapretSmart.App.Controls.Backdrops;

/// <summary>Звёзды, полосатое закатное солнце на горизонте и перспективная сетка, бегущая к зрителю.</summary>
internal sealed class SynthwaveGrid : IBackdropEffect
{
    private const double Horizon = 0.62;
    private const int DepthLines = 16;
    private const double Speed = 0.35;

    private static readonly IImmutableBrush SunBrush = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.Parse("#73FFD84D")), new ImmutableGradientStop(1, Color.Parse("#73FF4FB1"))],
        startPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative),
        endPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative));

    private readonly Random _random;
    private readonly ImmutablePen[] _pens = new ImmutablePen[AlphaBrushes.Levels + 1];
    private readonly AlphaBrushes _stars = new(Colors.White);
    private readonly AlphaBrushes _glow;
    private (Point P, double Alpha)[] _starField = [];
    private Geometry? _sun;
    private double _phase;

    public SynthwaveGrid(Color color, int seed)
    {
        _random = new Random(seed);
        var brushes = new AlphaBrushes(color);
        for (var i = 0; i <= AlphaBrushes.Levels; i++)
            _pens[i] = new ImmutablePen(brushes[(double)i / AlphaBrushes.Levels], 1.2);
        _glow = new AlphaBrushes(color);
    }

    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(33);

    public void Resize(Size size)
    {
        var horizon = size.Height * Horizon;
        var count = Math.Clamp((int)(size.Width * horizon / 9000), 5, 90);
        _starField = new (Point, double)[count];
        for (var i = 0; i < count; i++)
            _starField[i] = (new Point(_random.NextDouble() * size.Width, _random.NextDouble() * horizon * 0.9), 0.2 + _random.NextDouble() * 0.5);
        _sun = SunShape(size);
    }

    /// <summary>
    /// Солнце садится за горизонт правее середины: в центре его всё равно закрыли бы карточки.
    /// Прорези вырезаны из геометрии, а не закрашены, поэтому сквозь них виден настоящий фон окна.
    /// </summary>
    private static Geometry SunShape(Size size)
    {
        var horizon = size.Height * Horizon;
        var r = Math.Clamp(Math.Min(size.Width, size.Height) * 0.2, 12, 150);
        var disc = new Rect(size.Width * 0.72 - r, horizon - r * 1.35, r * 2, r * 2);
        var cuts = new GeometryGroup();
        for (var i = 0; i < 5; i++)
        {
            var y = disc.Center.Y + r * (0.15 + i * 0.2);
            cuts.Children.Add(new RectangleGeometry(new Rect(disc.X - 1, y, disc.Width + 2, r * (0.03 + i * 0.025))));
        }
        cuts.Children.Add(new RectangleGeometry(new Rect(disc.X - 1, horizon, disc.Width + 2, disc.Height)));
        return new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(disc), cuts);
    }

    public void Advance(double seconds) => _phase = (_phase + seconds * Speed) % 1;

    public void Render(DrawingContext context, Size size)
    {
        var w = size.Width;
        var h = size.Height;
        if (w < 1 || h < 1) return;
        var horizon = h * Horizon;
        var floor = h - horizon;

        foreach (var (p, alpha) in _starField)
            context.DrawEllipse(_stars[alpha], null, p, 0.9, 0.9);

        if (_sun is not null) context.DrawGeometry(SunBrush, null, _sun);

        context.FillRectangle(_glow[0.08], new Rect(0, horizon - 14, w, 14));
        context.DrawLine(_pens[AlphaBrushes.Level(0.7)], new Point(0, horizon), new Point(w, horizon));

        // Продольные линии сходятся в точку на горизонте.
        var vx = w / 2;
        var spacing = Math.Max(40, w / 14);
        var reach = (int)Math.Ceiling(w * 1.5 / spacing);
        for (var i = -reach; i <= reach; i++)
        {
            var bottomX = vx + i * spacing * 2.2;
            context.DrawLine(_pens[AlphaBrushes.Level(0.35)], new Point(vx + i * spacing * 0.08, horizon), new Point(bottomX, h));
        }

        // Поперечные линии: равный шаг по глубине даёт сгущение к горизонту.
        for (var i = 0; i < DepthLines; i++)
        {
            var z = 1 + i - _phase;
            var y = horizon + floor / z;
            if (y > h) continue;
            var alpha = 0.55 * Math.Clamp((y - horizon) / floor * 2.2, 0, 1);
            context.DrawLine(_pens[AlphaBrushes.Level(alpha)], new Point(0, y), new Point(w, y));
        }
    }
}
