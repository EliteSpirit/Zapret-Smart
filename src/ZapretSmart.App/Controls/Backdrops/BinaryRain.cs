using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ZapretSmart.App.Theming;

namespace ZapretSmart.App.Controls.Backdrops;

/// <summary>
/// Колонки нулей и единиц падают с разной скоростью: яркая «голова», затухающий хвост, изредка мигающие цифры.
/// Каждая цифра на каждом уровне яркости один раз рисуется в маленькую картинку, в кадре только DrawImage.
/// DrawText на каждую из ~1000 цифр стоил около 27 мс на кадр при программной отрисовке, картинки в разы дешевле.
/// </summary>
internal sealed class BinaryRain : IBackdropEffect, IDisposable
{
    /// <summary>Спрайты рисуются с запасом по разрешению, чтобы цифры оставались чёткими на экранах со 150–200 %.</summary>
    private const double SpriteScale = 2;

    /// <summary>
    /// Область спрайта в пикселях. DrawImage(image, dest) взял бы логический размер (18×20) и нарисовал бы из спрайта
    /// 36×40 только левую верхнюю четверть: Skia трактует область источника в пикселях.
    /// </summary>
    private static readonly Rect SpriteSource = new(0, 0, CellWidth * SpriteScale, CellHeight * SpriteScale);

    private const double CellWidth = 18;
    private const double CellHeight = 20;
    private const double FontSize = 14;
    private const double MaxAlpha = 0.42;

    private readonly Random _random;
    private readonly FormattedText[,] _glyphs = new FormattedText[2, AlphaBrushes.Levels + 1];
    private readonly FormattedText[] _heads = new FormattedText[2];
    private RenderTargetBitmap?[,]? _glyphSprites;
    private RenderTargetBitmap?[]? _headSprites;
    private Column[] _columns = [];
    private int _rows;

    public BinaryRain(Color color, int seed)
    {
        _random = new Random(seed);
        var typeface = new Typeface(ThemeResources.Mono);
        var tail = new AlphaBrushes(color);
        var head = new AlphaBrushes(ThemeResources.Mix(color, Colors.White, 0.35));
        for (var digit = 0; digit < 2; digit++)
        {
            for (var level = 0; level <= AlphaBrushes.Levels; level++)
                _glyphs[digit, level] = Text(digit, typeface, tail[(double)level / AlphaBrushes.Levels]);
            _heads[digit] = Text(digit, typeface, head[0.7]);
        }
    }

    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(55);

    private static FormattedText Text(int digit, Typeface typeface, IBrush brush) =>
        new(digit == 0 ? "0" : "1", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);

    public void Resize(Size size)
    {
        var fresh = _columns.Length == 0;
        _rows = (int)Math.Ceiling(size.Height / CellHeight) + 1;
        var count = (int)Math.Ceiling(size.Width / CellWidth);
        var old = _columns;
        _columns = new Column[count];
        for (var i = 0; i < count; i++)
        {
            if (i < old.Length && old[i].Digits.Length >= _rows)
            {
                _columns[i] = old[i];
                continue;
            }
            _columns[i] = new Column { Digits = new byte[_rows + 64] };
            for (var r = 0; r < _columns[i].Digits.Length; r++) _columns[i].Digits[r] = (byte)_random.Next(2);
            Respawn(ref _columns[i], initial: true);
        }
        if (fresh) BackdropEffects.Prewarm(this, 6);
    }

    private void Respawn(ref Column c, bool initial)
    {
        c.Speed = 5 + _random.NextDouble() * 11;
        c.Length = 8 + _random.Next(20);
        // Часть колонок стартует с большой задержкой: сплошная стена цифр читается хуже и отвлекает.
        c.Head = initial ? _random.NextDouble() * _rows * 1.5 - _rows * 0.5 : -_random.NextDouble() * _rows * 0.8;
    }

    public void Advance(double seconds)
    {
        for (var i = 0; i < _columns.Length; i++)
        {
            ref var c = ref _columns[i];
            c.Head += c.Speed * seconds;
            if (c.Head - c.Length > _rows) Respawn(ref c, initial: false);
            if (_random.NextDouble() < seconds * 4)
            {
                var r = _random.Next(c.Digits.Length);
                c.Digits[r] ^= 1;
            }
        }
    }

    public void Render(DrawingContext context, Size size)
    {
        EnsureSprites();
        for (var i = 0; i < _columns.Length; i++)
        {
            var c = _columns[i];
            var head = (int)Math.Floor(c.Head);
            var x = i * CellWidth;
            var from = Math.Max(0, head - c.Length);
            var to = Math.Min(_rows - 1, head);
            for (var r = from; r <= to; r++)
            {
                var digit = c.Digits[r % c.Digits.Length];
                var cell = new Rect(x, r * CellHeight, CellWidth, CellHeight);
                if (r == head)
                {
                    context.DrawImage(_headSprites![digit]!, SpriteSource, cell);
                    continue;
                }
                var fade = 1 - (double)(head - r) / c.Length;
                var level = AlphaBrushes.Level(MaxAlpha * fade * fade);
                if (level == 0) continue;
                context.DrawImage(_glyphSprites![digit, level]!, SpriteSource, cell);
            }
        }
    }

    private void EnsureSprites()
    {
        if (_glyphSprites is not null) return;
        _glyphSprites = new RenderTargetBitmap?[2, AlphaBrushes.Levels + 1];
        _headSprites = new RenderTargetBitmap?[2];
        for (var digit = 0; digit < 2; digit++)
        {
            for (var level = 1; level <= AlphaBrushes.Levels; level++)
                _glyphSprites[digit, level] = Sprite(_glyphs[digit, level]);
            _headSprites[digit] = Sprite(_heads[digit]);
        }
    }

    private static RenderTargetBitmap Sprite(FormattedText text)
    {
        var bitmap = new RenderTargetBitmap(
            new PixelSize((int)(CellWidth * SpriteScale), (int)(CellHeight * SpriteScale)),
            new Vector(96 * SpriteScale, 96 * SpriteScale));
        using (var ctx = bitmap.CreateDrawingContext())
            ctx.DrawText(text, new Point(3, (CellHeight - text.Height) / 2));
        return bitmap;
    }

    public void Dispose()
    {
        if (_glyphSprites is not null)
            foreach (var sprite in _glyphSprites) sprite?.Dispose();
        if (_headSprites is not null)
            foreach (var sprite in _headSprites) sprite?.Dispose();
        _glyphSprites = null;
        _headSprites = null;
    }

    private struct Column
    {
        public double Head;
        public double Speed;
        public int Length;
        public byte[] Digits;
    }
}
