using Avalonia;
using Avalonia.Media;

namespace ZapretSmart.App.Controls.Backdrops;

/// <summary>Снег в три слоя глубины: дальние хлопья мельче, бледнее и медленнее. Каждый покачивается по синусоиде.</summary>
internal sealed class Snowfall : IBackdropEffect
{
    private const double FlakesPerMegapixel = 150;

    private readonly Random _random;
    private readonly AlphaBrushes _brushes;
    private Flake[] _flakes = [];
    private Size _size;
    private double _time;

    public Snowfall(Color color, int seed)
    {
        _random = new Random(seed);
        _brushes = new AlphaBrushes(color);
    }

    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(33);

    public void Resize(Size size)
    {
        var fresh = _flakes.Length == 0;
        _size = size;
        var count = Math.Clamp((int)(size.Width * size.Height / 1e6 * FlakesPerMegapixel), 12, 260);
        var old = _flakes;
        _flakes = new Flake[count];
        for (var i = 0; i < count; i++)
        {
            if (i < old.Length)
            {
                _flakes[i] = old[i];
                if (_flakes[i].X > size.Width) _flakes[i].X = _random.NextDouble() * size.Width;
                continue;
            }
            _flakes[i] = NewFlake(_random.NextDouble() * size.Height);
        }
        if (fresh) BackdropEffects.Prewarm(this, 2);
    }

    private Flake NewFlake(double y)
    {
        var depth = _random.NextDouble();
        return new Flake
        {
            X = _random.NextDouble() * _size.Width,
            Y = y,
            Radius = 0.8 + depth * 2.2,
            Speed = 14 + depth * 46 + _random.NextDouble() * 8,
            Alpha = 0.22 + depth * 0.5,
            Sway = 4 + depth * 18,
            Frequency = 0.25 + _random.NextDouble() * 0.6,
            Phase = _random.NextDouble() * Math.PI * 2,
        };
    }

    public void Advance(double seconds)
    {
        _time += seconds;
        for (var i = 0; i < _flakes.Length; i++)
        {
            ref var f = ref _flakes[i];
            f.Y += f.Speed * seconds;
            if (f.Y - f.Radius > _size.Height) f = NewFlake(-f.Radius - _random.NextDouble() * 20);
        }
    }

    public void Render(DrawingContext context, Size size)
    {
        foreach (var f in _flakes)
        {
            var x = f.X + Math.Sin(_time * f.Frequency * Math.PI * 2 + f.Phase) * f.Sway;
            context.DrawEllipse(_brushes[f.Alpha], null, new Point(x, f.Y), f.Radius, f.Radius);
        }
    }

    private struct Flake
    {
        public double X;
        public double Y;
        public double Radius;
        public double Speed;
        public double Alpha;
        public double Sway;
        public double Frequency;
        public double Phase;
    }
}
