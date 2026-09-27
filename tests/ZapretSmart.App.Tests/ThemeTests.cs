using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZapretSmart.App.Controls;
using ZapretSmart.App.Controls.Backdrops;
using ZapretSmart.App.Theming;
using ZapretSmart.App.ViewModels;
using ZapretSmart.App.Views;
using ZapretSmart.Core.Engine;

namespace ZapretSmart.App.Tests;

public sealed class ThemeTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "zs-theme-" + Guid.NewGuid().ToString("N"));

    private MainWindowViewModel CreateVm()
    {
        var bin = AppContext.BaseDirectory;
        return new MainWindowViewModel(new AppPaths(
            Path.Combine(bin, "engine", "winws.exe"),
            new EngineLayout(Path.Combine(bin, "engine", "fake"), Path.Combine(_data, "lists"), Path.Combine(_data, "ipsets")),
            Path.Combine(bin, "lists"),
            Path.Combine(bin, "strategies"),
            Path.Combine(_data, "strategies"),
            Path.Combine(_data, "settings.json")));
    }

    private static void Pump(double seconds = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        } while (DateTime.UtcNow < deadline);
    }

    public static TheoryData<string> ThemeIds => new(AppTheme.All.Select(t => t.Id));

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void ThemeTextIsReadable(string id)
    {
        var p = AppTheme.Find(id).Palette;
        // Основной текст: AAA (7:1). Остальной текст, включая мелкие подписи и акцентное «SMART»: AA (4.5:1).
        AssertContrast(p.Text, p.Surface, 7, "текст на карточке");
        AssertContrast(p.Text, p.Background, 7, "текст на фоне");
        AssertContrast(p.Text, p.SurfaceRaised, 4.5, "текст на кнопке и выбранном пункте");
        AssertContrast(p.TextMuted, p.Surface, 4.5, "подписи на карточке");
        AssertContrast(p.TextMuted, p.Background, 4.5, "подписи на фоне");
        AssertContrast(p.TextMuted, p.SurfaceRaised, 4.5, "подписи на приподнятой поверхности");
        AssertContrast(p.Accent, p.Surface, 4.5, "акцентный текст");
        AssertContrast(p.AccentText, p.Accent, 4.5, "текст на главной кнопке");
        AssertContrast(p.Danger, p.Surface, 4.5, "ошибки");
        AssertContrast(p.Danger, p.SurfaceRaised, 4.5, "ошибки на приподнятой поверхности");
        AssertContrast(p.Warning, p.Surface, 4.5, "предупреждения в журнале");
    }

    private static void AssertContrast(Color fg, Color bg, double min, string what)
    {
        var ratio = ThemeResources.Contrast(fg, bg);
        Assert.True(ratio >= min, $"{what}: контраст {ratio:0.00} меньше {min}");
    }

    [Fact]
    public void ThemeIdsAreUniqueAndUnknownIdFallsBackToDefault()
    {
        Assert.Equal(AppTheme.All.Count, AppTheme.All.Select(t => t.Id).Distinct().Count());
        Assert.Same(AppTheme.Default, AppTheme.Find(null));
        Assert.Same(AppTheme.Default, AppTheme.Find("theme-from-the-future"));
        Assert.Same(AppTheme.Hacker, AppTheme.Find("hacker"));
    }

    /// <summary>Любой {DynamicResource X} в разметке должен быть в словаре каждой темы: иначе элемент молча останется без цвета.</summary>
    [AvaloniaFact]
    public void EveryResourceUsedInMarkupIsProvidedByEveryTheme()
    {
        var root = RepoRoot();
        var used = new[] { "src/ZapretSmart.App/App.axaml", "src/ZapretSmart.App/Views/MainWindow.axaml" }
            .SelectMany(f => Regex.Matches(File.ReadAllText(Path.Combine(root, f)), @"DynamicResource (\w+)").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        Assert.Contains("ZsAccent", used);
        foreach (var theme in AppTheme.All)
        {
            var dict = ThemeResources.Build(theme);
            var missing = used.Where(k => !dict.ContainsKey(k)).ToList();
            Assert.True(missing.Count == 0, $"{theme.Id}: нет ресурсов {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void MarkupHasNoStaticColors()
    {
        // StaticResource не обновляется при смене темы, а цвет прямо в разметке не меняется вовсе.
        var root = RepoRoot();
        foreach (var f in new[] { "src/ZapretSmart.App/App.axaml", "src/ZapretSmart.App/Views/MainWindow.axaml" })
        {
            var text = File.ReadAllText(Path.Combine(root, f));
            Assert.DoesNotContain("{StaticResource", text);
            Assert.DoesNotMatch(@"(Background|Foreground|Fill|Stroke|BorderBrush)=""#", text);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "ZapretSmart.App", "App.axaml"))) return dir.FullName;
        throw new InvalidOperationException("не найден корень репозитория");
    }

    [AvaloniaFact]
    public void ThemeChoiceIsSavedAndMarkedInPicker()
    {
        var vm = CreateVm();
        Assert.Same(AppTheme.Default, vm.Theme);
        Assert.True(vm.Themes.Single(t => t.Theme == AppTheme.Default).IsSelected);

        vm.Themes.Single(t => t.Theme == AppTheme.Halloween).SelectCommand.Execute(null);
        Assert.Same(AppTheme.Halloween, vm.Theme);
        Assert.Equal([AppTheme.Halloween], vm.Themes.Where(t => t.IsSelected).Select(t => t.Theme));

        vm.AnimatedBackdrop = false;
        var again = CreateVm();
        Assert.Same(AppTheme.Halloween, again.Theme);
        Assert.False(again.AnimatedBackdrop);
    }

    [AvaloniaFact]
    public void UnknownThemeInSettingsFileFallsBackToDefault()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, "settings.json"), """{ "themeId": "vaporwave-9000" }""");
        Assert.Same(AppTheme.Default, CreateVm().Theme);
    }

    [AvaloniaFact]
    public void SwitchingThemeRepaintsWindowAndFluentControls()
    {
        var vm = CreateVm();
        var w = new MainWindow { DataContext = vm };
        w.Show();
        Pump();

        vm.Theme = AppTheme.Light;
        Pump(0.5);
        Assert.Equal(ThemeVariant.Light, Application.Current!.ActualThemeVariant);
        Assert.Equal(AppTheme.Light.Palette.Background, ((ISolidColorBrush)w.Background!).Color);
        Assert.True(Application.Current.TryGetResource("SystemAccentColor", null, out var accent));
        Assert.Equal(AppTheme.Light.Palette.Accent, accent);

        vm.Theme = AppTheme.Hacker;
        Pump(0.5);
        Assert.Equal(ThemeVariant.Dark, Application.Current.ActualThemeVariant);
        Assert.Equal(AppTheme.Hacker.Palette.Background, ((ISolidColorBrush)w.Background!).Color);
        Assert.Same(ThemeResources.Mono, w.FontFamily);
    }

    [AvaloniaFact]
    public void ThemeCrossfadeOverlayIsRemovedAfterFade()
    {
        var vm = CreateVm();
        var w = new MainWindow { DataContext = vm };
        w.Show();
        Pump();
        var overlay = w.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "OverlayHost");

        vm.Theme = AppTheme.Winter;
        Assert.Single(overlay.Children);
        Assert.IsType<RenderTargetBitmap>(((Image)overlay.Children[0]).Source);
        Pump(0.8);
        Assert.Empty(overlay.Children);
    }

    [AvaloniaFact]
    public void BackdropAnimatesOnlyWhenItIsSeenAndAllowed()
    {
        var vm = CreateVm();
        vm.Theme = AppTheme.Hacker;
        var w = new MainWindow { DataContext = vm };
        w.Show();
        Pump();
        var backdrop = w.GetVisualDescendants().OfType<BackdropView>().Single(b => b.Name == "Backdrop");
        Assert.Equal(BackdropKind.BinaryRain, backdrop.Kind);
        Assert.True(backdrop.IsRunning);

        vm.AnimatedBackdrop = false;
        Assert.False(backdrop.IsRunning);
        vm.AnimatedBackdrop = true;
        Assert.True(backdrop.IsRunning);

        w.WindowState = WindowState.Minimized;
        Assert.False(backdrop.IsRunning);
        w.WindowState = WindowState.Normal;
        Assert.True(backdrop.IsRunning);

        // Окно спрятано в трей.
        w.Hide();
        Assert.False(backdrop.IsRunning);
        w.Show();
        Assert.True(backdrop.IsRunning);

        vm.Theme = AppTheme.Graphite;
        Assert.Equal(BackdropKind.None, backdrop.Kind);
        Assert.False(backdrop.IsRunning);
    }

    [AvaloniaFact]
    public void ThemePreviewsAreStill()
    {
        var vm = CreateVm();
        var w = new MainWindow { DataContext = vm };
        w.Show();
        w.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 3;
        Pump(0.3);
        var previews = w.GetVisualDescendants().OfType<BackdropView>().Where(b => b.Name != "Backdrop").ToList();
        Assert.Equal(AppTheme.All.Count, previews.Count);
        Assert.All(previews, p => Assert.False(p.IsRunning));
    }

    public static TheoryData<BackdropKind> Kinds => new(Enum.GetValues<BackdropKind>().Where(k => k != BackdropKind.None));

    [AvaloniaTheory]
    [MemberData(nameof(Kinds))]
    public void EffectSurvivesOddSizesAndLongPauses(BackdropKind kind)
    {
        var effect = BackdropEffects.Create(kind, Colors.Lime, seed: 1)!;
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64));
        foreach (var size in new[] { new Size(0, 0), new Size(1920, 1080), new Size(3, 700), new Size(700, 3), new Size(1100, 720) })
        {
            effect.Resize(size);
            effect.Advance(0);
            effect.Advance(0.1);
            for (var i = 0; i < 200; i++) effect.Advance(0.05);
            using var ctx = bitmap.CreateDrawingContext();
            effect.Render(ctx, size);
        }
    }

    /// <summary>
    /// Грубая защита от регрессий вроде «выделяем FormattedText на каждый глиф в каждом кадре».
    /// Кадр рисуется программно на процессоре, в приложении на видеокарте быстрее.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Kinds))]
    public void EffectFrameIsCheap(BackdropKind kind)
    {
        var effect = BackdropEffects.Create(kind, Colors.Lime, seed: 1)!;
        var size = new Size(1100, 720);
        effect.Resize(size);
        using var bitmap = new RenderTargetBitmap(new PixelSize(1100, 720));
        var sw = Stopwatch.StartNew();
        const int frames = 30;
        for (var i = 0; i < frames; i++)
        {
            effect.Advance(0.033);
            using var ctx = bitmap.CreateDrawingContext();
            effect.Render(ctx, size);
        }
        var perFrame = sw.Elapsed.TotalMilliseconds / frames;
        Assert.True(perFrame < 50, $"{kind}: {perFrame:0.0} мс на кадр");
    }

    [AvaloniaFact]
    public void BatShapeIsClosedAndSymmetric()
    {
        foreach (var flap in new[] { -1.0, 0, 1 })
        {
            var bounds = HalloweenNight.BatShape(flap).Bounds;
            Assert.Equal(-bounds.Left, bounds.Right, 3);
            Assert.InRange(bounds.Width, 40, 44);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }
}
