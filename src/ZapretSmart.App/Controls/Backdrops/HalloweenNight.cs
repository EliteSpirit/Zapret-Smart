using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ZapretSmart.App.Theming;

namespace ZapretSmart.App.Controls.Backdrops;

/// <summary>Луна с ореолом в правом верхнем углу, поднимающиеся искры и изредка пролетающие летучие мыши.</summary>
internal sealed class HalloweenNight : IBackdropEffect
{
    private const double EmbersPerMegapixel = 55;

    private static readonly IImmutableSolidColorBrush BatBrush = new ImmutableSolidColorBrush(Color.Parse("#E62E1C40"));
    private static readonly IImmutableSolidColorBrush MoonBrush = new ImmutableSolidColorBrush(Color.Parse("#4DFFE9C7"));
    private static readonly IImmutableSolidColorBrush CraterBrush = new ImmutableSolidColorBrush(Color.Parse("#14000000"));
    /// <summary>Ореол: плавный радиальный градиент. Стопка полупрозрачных кругов давала видимые кольца, как у мишени.</summary>
    private static readonly IImmutableBrush GlowBrush = new ImmutableRadialGradientBrush(
    [
        new ImmutableGradientStop(0, Color.Parse("#38FFE2B8")),
        new ImmutableGradientStop(0.3, Color.Parse("#26FFD9A0")),
        new ImmutableGradientStop(0.62, Color.Parse("#0CFFD9A0")),
        new ImmutableGradientStop(1, Color.Parse("#00FFD9A0")),
    ], radiusX: RelativeScalar.Middle, radiusY: RelativeScalar.Middle);

    private readonly Random _random;
    private readonly AlphaBrushes _ember;
    private readonly AlphaBrushes _emberCore;
    private readonly Bat[] _bats = new Bat[3];
    private Ember[] _embers = [];
    private Size _size;
    private double _time;

    public HalloweenNight(Color color, int seed)
    {
        _random = new Random(seed);
        _ember = new AlphaBrushes(color);
        _emberCore = new AlphaBrushes(ThemeResources.Mix(color, Colors.White, 0.45));
    }

    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(33);

    private double Scale => Math.Clamp(_size.Width / 1100, 0.3, 1.3);

    public void Resize(Size size)
    {
        var fresh = _embers.Length == 0;
        _size = size;
        var count = Math.Clamp((int)(size.Width * size.Height / 1e6 * EmbersPerMegapixel), 6, 120);
        var old = _embers;
        _embers = new Ember[count];
        for (var i = 0; i < count; i++)
            _embers[i] = i < old.Length ? old[i] : NewEmber(_random.NextDouble() * size.Height);
        if (!fresh) return;

        for (var i = 0; i < _bats.Length; i++) _bats[i].Wait = 2 + _random.NextDouble() * 10;
        // Одна мышь видна сразу, иначе тема до первого пролёта выглядит просто оранжевой.
        _bats[0] = new Bat { Active = true, X = size.Width * 0.55, Y = size.Height * 0.2, Vx = 55 * Scale, Size = 0.9 * Scale, Phase = 0.6 };
        BackdropEffects.Prewarm(this, 1.5);
    }

    private Ember NewEmber(double y) => new()
    {
        X = _random.NextDouble() * _size.Width,
        Y = y,
        Radius = 0.9 + _random.NextDouble() * 1.6,
        Speed = 14 + _random.NextDouble() * 34,
        Sway = 6 + _random.NextDouble() * 20,
        Frequency = 0.15 + _random.NextDouble() * 0.4,
        Phase = _random.NextDouble() * Math.PI * 2,
        Flicker = 2 + _random.NextDouble() * 5,
    };

    public void Advance(double seconds)
    {
        _time += seconds;
        for (var i = 0; i < _embers.Length; i++)
        {
            ref var e = ref _embers[i];
            e.Y -= e.Speed * seconds;
            if (e.Y < -10) e = NewEmber(_size.Height + _random.NextDouble() * 30);
        }

        for (var i = 0; i < _bats.Length; i++)
        {
            ref var b = ref _bats[i];
            if (!b.Active)
            {
                b.Wait -= seconds;
                if (b.Wait <= 0) Launch(ref b);
                continue;
            }
            b.X += b.Vx * seconds;
            b.Phase += seconds * 8.5;
            var margin = 40 * b.Size;
            if (b.X < -margin * 2 || b.X > _size.Width + margin * 2)
            {
                b.Active = false;
                b.Wait = 6 + _random.NextDouble() * 14;
            }
        }
    }

    private void Launch(ref Bat b)
    {
        var right = _random.Next(2) == 0;
        b.Size = (0.55 + _random.NextDouble() * 0.45) * Scale;
        b.X = right ? -40 * b.Size : _size.Width + 40 * b.Size;
        b.Y = _size.Height * (0.08 + _random.NextDouble() * 0.4);
        b.Vx = (right ? 1 : -1) * (45 + _random.NextDouble() * 40) * Scale;
        b.Phase = _random.NextDouble() * Math.PI * 2;
        b.Active = true;
    }

    public void Render(DrawingContext context, Size size)
    {
        DrawMoon(context, size);

        foreach (var e in _embers)
        {
            var x = e.X + Math.Sin(_time * e.Frequency * Math.PI * 2 + e.Phase) * e.Sway;
            // Гаснут, поднимаясь к верху окна, и слегка мерцают.
            var height = Math.Clamp(e.Y / size.Height, 0, 1);
            var alpha = (0.25 + 0.5 * height) * (0.75 + 0.25 * Math.Sin(_time * e.Flicker + e.Phase));
            var center = new Point(x, e.Y);
            context.DrawEllipse(_ember[alpha * 0.22], null, center, e.Radius * 3.2, e.Radius * 3.2);
            context.DrawEllipse(_emberCore[alpha], null, center, e.Radius, e.Radius);
        }

        foreach (var b in _bats)
        {
            if (!b.Active) continue;
            // Вверх-вниз вместе со взмахом: мышь не скользит по линейке.
            var y = b.Y + Math.Sin(b.Phase * 0.5) * 6 * b.Size;
            using (context.PushTransform(Matrix.CreateScale(b.Size, b.Size) * Matrix.CreateTranslation(b.X, y)))
                context.DrawGeometry(BatBrush, null, BatShape(Math.Sin(b.Phase)));
        }
    }

    private static void DrawMoon(DrawingContext context, Size size)
    {
        var r = Math.Clamp(Math.Min(size.Width, size.Height) * 0.07, 5, 56);
        var c = new Point(size.Width - r * 1.15, size.Height * 0.16 + r * 0.2);
        context.DrawEllipse(GlowBrush, null, c, r * 3.4, r * 3.4);
        context.DrawEllipse(MoonBrush, null, c, r, r);
        context.DrawEllipse(CraterBrush, null, new Point(c.X - r * 0.35, c.Y - r * 0.2), r * 0.22, r * 0.22);
        context.DrawEllipse(CraterBrush, null, new Point(c.X + r * 0.25, c.Y + r * 0.3), r * 0.16, r * 0.16);
        context.DrawEllipse(CraterBrush, null, new Point(c.X + r * 0.1, c.Y - r * 0.45), r * 0.1, r * 0.1);
    }

    /// <summary>Силуэт летучей мыши размахом около 42 единиц. flap от -1 до 1 поднимает и опускает концы крыльев.</summary>
    internal static StreamGeometry BatShape(double flap)
    {
        var o = flap * 7;
        Point[] top = [new(2, -2), new(8, -5 + o * 0.4), new(14, -7 + o * 0.8), new(21, -6 + o)];
        Point[] bottom = [new(19, -1 + o * 0.8), new(16, 1 + o * 0.5), new(13, 0 + o * 0.4), new(10, 3 + o * 0.3), new(7, 1 + o * 0.2), new(4, 3)];
        Point[] ears = [new(-2, -4), new(-2, -8), new(-0.6, -5.2), new(0.6, -5.2), new(2, -8), new(2, -4)];

        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(ears[0], true);
        foreach (var p in ears.Skip(1)) g.LineTo(p);
        foreach (var p in top) g.LineTo(p);
        foreach (var p in bottom) g.LineTo(p);
        g.LineTo(new Point(0, 6));
        foreach (var p in bottom.Reverse()) g.LineTo(new Point(-p.X, p.Y));
        foreach (var p in top.Reverse()) g.LineTo(new Point(-p.X, p.Y));
        g.EndFigure(true);
        return geometry;
    }

    private struct Ember
    {
        public double X;
        public double Y;
        public double Radius;
        public double Speed;
        public double Sway;
        public double Frequency;
        public double Phase;
        public double Flicker;
    }

    private struct Bat
    {
        public bool Active;
        public double Wait;
        public double X;
        public double Y;
        public double Vx;
        public double Size;
        public double Phase;
    }
}
