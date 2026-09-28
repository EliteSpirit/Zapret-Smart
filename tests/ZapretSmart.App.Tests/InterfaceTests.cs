using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZapretSmart.App.Theming;
using ZapretSmart.App.ViewModels;
using ZapretSmart.App.Views;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Search;

namespace ZapretSmart.App.Tests;

/// <summary>Поведение интерфейса, которое не видно из модели: кнопки, вкладки, журнал, подтверждения.</summary>
public sealed class InterfaceTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "zs-ui-" + Guid.NewGuid().ToString("N"));

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

    private (MainWindowViewModel Vm, MainWindow Window) Open()
    {
        var vm = CreateVm();
        var w = new MainWindow { DataContext = vm };
        w.Show();
        Pump();
        return (vm, w);
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

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Color PresenterBackground(Button b) =>
        ((ISolidColorBrush)b.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").Background!).Color;

    private static Color Resource(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var value));
        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>Сколько пикселей в прямоугольнике ярче серого 90 из 255: фон пункта меню тёмный, светится только текст.</summary>
    private static int BrightPixels(Avalonia.Media.Imaging.WriteableBitmap bitmap, PixelRect area)
    {
        using var fb = bitmap.Lock();
        var row = new byte[fb.RowBytes];
        var bright = 0;
        for (var y = area.Y; y < area.Bottom; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
            for (var x = area.X; x < area.Right; x++)
                if ((row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2]) / 3 > 90) bright++;
        }
        return bright;
    }

    /// <summary>
    /// Пункт меню, с которого уходит выделение, на кадр вспыхивал целиком. Его фон переходил в Brushes.Transparent,
    /// а это белый с нулевой альфой: переход шёл через светлый полупрозрачный цвет. Сообщил тестировщик из РФ.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeftMenuItemDoesNotFlashWhenAnotherIsClicked(bool menuOnTop)
    {
        var (vm, w) = Open();
        vm.MenuOnTop = menuOnTop;
        Pump(0.3);
        var items = w.GetVisualDescendants().OfType<TabItem>().ToList();
        var origin = items[0].TranslatePoint(default, w)!.Value;
        var first = new PixelRect((int)origin.X + 2, (int)origin.Y + 2, (int)items[0].Bounds.Width - 4, (int)items[0].Bounds.Height - 4);
        var target = items[1].TranslatePoint(new Point(items[1].Bounds.Width / 2, items[1].Bounds.Height / 2), w)!.Value;
        var textOnly = BrightPixels(w.CaptureRenderedFrame()!, first);

        w.MouseMove(target);
        w.MouseDown(target, MouseButton.Left);
        var worst = 0;
        var deadline = DateTime.UtcNow.AddSeconds(0.25);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            worst = Math.Max(worst, BrightPixels(w.CaptureRenderedFrame()!, first));
        }
        w.MouseUp(target, MouseButton.Left);

        Assert.Equal(1, w.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex);
        // При вспышке светился весь пункт, без неё только буквы и значок.
        Assert.True(worst < textOnly * 3 + 200, $"покинутый пункт вспыхнул: {worst} ярких пикселей, в покое {textOnly}");
    }

    [AvaloniaFact]
    public void MenuOnTopPutsTabsInARowAndIsRemembered()
    {
        var (vm, w) = Open();
        var tabs = w.GetVisualDescendants().OfType<TabControl>().Single();
        Assert.Equal(Dock.Left, tabs.TabStripPlacement);

        vm.MenuOnTop = true;
        Pump(0.2);
        Assert.Contains("top-menu", w.Classes);
        Assert.Equal(Dock.Top, tabs.TabStripPlacement);
        var items = w.GetVisualDescendants().OfType<TabItem>().ToList();
        var a = items[0].TranslatePoint(default, w)!.Value;
        var b = items[1].TranslatePoint(default, w)!.Value;
        Assert.True(b.X > a.X && Math.Abs(b.Y - a.Y) < 1, $"вкладки не в строку: {a} {b}");
        // Содержимое начинается под полосой меню, а не под ней прячется.
        var page = (Visual)tabs.SelectedContent!;
        var bar = w.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("chrome"));
        Assert.True(bar.Bounds.Height > 0 && page.TranslatePoint(default, w)!.Value.Y >= bar.Bounds.Bottom);

        Assert.True(CreateVm().MenuOnTop);
        vm.MenuOnTop = false;
        Pump(0.2);
        Assert.Equal(Dock.Left, tabs.TabStripPlacement);
    }

    [AvaloniaFact]
    public async Task HostsSwitchWritesAndRemovesOnlyItsBlock()
    {
        var vm = CreateVm();
        Directory.CreateDirectory(_data);
        var hosts = Path.Combine(_data, "hosts");
        const string mine = "# мой hosts\r\n10.0.0.1 my.router\r\n";
        File.WriteAllText(hosts, mine);
        var snapshot = Path.Combine(AppContext.BaseDirectory, "hosts", "flowseal.hosts");
        // Сеть «недоступна»: берётся встроенный снимок, и тест не зависит от GitHub.
        var manager = new Core.Hosts.HostsManager(hosts, Path.Combine(_data, "data"), snapshot, new HttpClient(new FailingHandler()));
        var model = new HostsViewModel(vm, manager, enabled: false);

        model.IsEnabled = true;
        await WaitIdle(model);
        Assert.StartsWith(mine, File.ReadAllText(hosts), StringComparison.Ordinal);
        Assert.Contains(Core.Hosts.HostsFile.BeginMarker, File.ReadAllText(hosts));
        Assert.Contains(model.Entries, e => e.EndsWith(" github.com", StringComparison.Ordinal));
        Assert.Contains("встроенный снимок", model.Status);
        Assert.Contains("\"hostsEnabled\": true", File.ReadAllText(Path.Combine(_data, "settings.json")));

        model.IsEnabled = false;
        await WaitIdle(model);
        Assert.Equal(mine, File.ReadAllText(hosts));
        Assert.Empty(model.Entries);
    }

    private static async Task WaitIdle(HostsViewModel model)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        await Task.Yield();
        while ((model.IsBusy || model.Status == "Загружаю список") && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.False(model.IsBusy);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("нет сети");
    }

    [AvaloniaFact]
    public void VersionListSelectsNewestAndDescribesIt()
    {
        var updates = CreateVm().Updates;
        var list = Core.Updates.Releases.Parse("""
            [ { "tag_name": "v0.2.0", "prerelease": true, "published_at": "2026-09-20T10:00:00Z",
                "assets": [ { "name": "ZapretSmart-0.2.0-win-x64.zip", "browser_download_url": "https://github.com/EliteSpirit/Zapret-Smart/releases/download/v0.2.0/ZapretSmart-0.2.0-win-x64.zip" },
                            { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/EliteSpirit/Zapret-Smart/releases/download/v0.2.0/SHA256SUMS.txt" } ] },
              { "tag_name": "v0.3.0", "prerelease": false, "published_at": "2026-09-27T10:00:00Z",
                "assets": [ { "name": "ZapretSmart-0.3.0-win-x64.zip", "browser_download_url": "https://github.com/EliteSpirit/Zapret-Smart/releases/download/v0.3.0/ZapretSmart-0.3.0-win-x64.zip" },
                            { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/EliteSpirit/Zapret-Smart/releases/download/v0.3.0/SHA256SUMS.txt" } ] } ]
            """);
        updates.ShowReleases(list);
        Assert.Equal(["0.3.0", "0.2.0"], updates.Releases.Select(r => r.Release.Version.ToString()));
        Assert.Same(updates.Releases[0], updates.Selected);
        Assert.StartsWith("0.3.0 · ", updates.Releases[0].Title, StringComparison.Ordinal);
        Assert.EndsWith(" · пререлиз", updates.Releases[1].Title, StringComparison.Ordinal);
        Assert.Contains("0.3.0", updates.Status);
    }

    /// <summary>Раньше Fluent перекрашивал главную кнопку при наведении в серый, а «Выключить» терял красную рамку.</summary>
    [AvaloniaFact]
    public void AccentButtonsKeepTheirColorOnHover()
    {
        var primary = new Button { Content = "Включить", Classes = { "primary" } };
        var danger = new Button { Content = "Выключить", Classes = { "danger" } };
        var w = new Window { Content = new StackPanel { Children = { primary, danger } } };
        w.Show();
        ThemeResources.Apply(Application.Current!, AppTheme.Graphite);
        Pump();
        // Проверяем цвет, который задаёт стиль наведения, а не кадр 120-миллисекундного перехода: иначе результат
        // зависел бы от того, успели ли часы анимации дотикать под нагрузкой.
        foreach (var b in new[] { primary, danger })
            b.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").Transitions = null;
        ((IPseudoClasses)primary.Classes).Add(":pointerover");
        ((IPseudoClasses)danger.Classes).Add(":pointerover");
        Pump();

        Assert.Equal(Resource("ZsAccentHover"), PresenterBackground(primary));
        Assert.Equal(Resource("ZsDangerSoft"), PresenterBackground(danger));
        var presenter = danger.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        Assert.Equal(Resource("ZsDanger"), ((ISolidColorBrush)presenter.BorderBrush!).Color);
    }

    [AvaloniaFact]
    public void OnlyTheActionThatCanRunIsShown()
    {
        var (vm, w) = Open();
        var start = Named<Button>(w, "StartButton");
        var stop = Named<Button>(w, "StopButton");
        Assert.DoesNotContain("hidden", start.Classes);
        Assert.Contains("hidden", stop.Classes);
        Assert.False(stop.IsEffectivelyEnabled);

        vm.IsRunning = true;
        Pump();
        Assert.Contains("hidden", start.Classes);
        Assert.False(start.IsEffectivelyEnabled);
        Assert.DoesNotContain("hidden", stop.Classes);

        vm.IsRunning = false;
        vm.IsSwitching = true;
        Pump();
        Assert.DoesNotContain("hidden", stop.Classes);
        vm.IsSwitching = false;
    }

    [AvaloniaFact]
    public void KeyboardTabSwitchIsInstantAndMouseSwitchAnimates()
    {
        var (_, w) = Open();
        var tabs = w.GetVisualDescendants().OfType<TabControl>().Single();

        w.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        tabs.SelectedIndex = 2;
        Assert.Equal(1, LowestOpacityWhileSettling((Visual)tabs.SelectedContent!), 3);

        w.MouseDown(new Point(100, 300), MouseButton.Left);
        w.MouseUp(new Point(100, 300), MouseButton.Left);
        tabs.SelectedIndex = 3;
        Assert.True(LowestOpacityWhileSettling((Visual)tabs.SelectedContent!) < 0.1);
    }

    private static double LowestOpacityWhileSettling(Visual page)
    {
        var lowest = page.Opacity;
        page.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.OpacityProperty) lowest = Math.Min(lowest, (double)e.NewValue!);
        };
        var deadline = DateTime.UtcNow.AddSeconds(0.6);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Assert.Equal(1, page.Opacity, 3);
        return lowest;
    }

    [AvaloniaFact]
    public void LogFollowsNewLinesUnlessUserScrolledUp()
    {
        var (vm, w) = Open();
        var scroll = Named<ScrollViewer>(w, "LogScroll");
        for (var i = 0; i < 80; i++) vm.AppendLog("строка " + i);
        Pump(0.2);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);

        scroll.Offset = new Vector(0, 0);
        Pump(0.1);
        vm.AppendLog("новая строка");
        Pump(0.2);
        Assert.Equal(0, scroll.Offset.Y, 1);
    }

    /// <summary>
    /// Полный журнал (500 строк, часть переносится) не раскладывается целиком: иначе каждый возврат на главную вкладку
    /// стоил втрое дороже остальных. При этом прокрутка по-прежнему доходит до последней строки.
    /// </summary>
    [AvaloniaFact]
    public void FullLogIsVirtualizedAndStillEndsAtTheLastLine()
    {
        var (vm, w) = Open();
        for (var i = 0; i < 500; i++)
            vm.AppendLog(i % 3 == 0 ? $"строка {i} " + string.Concat(Enumerable.Repeat("--dpi-desync=fake,multisplit ", 8)) : $"строка {i}");
        Pump(0.3);

        var lines = Named<ItemsControl>(w, "LogItems").GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).ToList();
        Assert.InRange(lines.Count, 1, 60);
        var scroll = Named<ScrollViewer>(w, "LogScroll");
        Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
        var last = lines.Single(t => t.Text == vm.Log[^1]);
        var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), scroll)!.Value.Y;
        Assert.InRange(bottom, 0, scroll.Viewport.Height + 1);
    }

    [AvaloniaFact]
    public void DeletingAskedForConfirmationFirst()
    {
        var (vm, w) = Open();
        var ed = vm.Editor;
        w.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        Pump();
        var del = Named<Button>(w, "DeleteButton");
        Assert.False(del.IsEffectivelyEnabled);

        ed.NewCommand.Execute(null);
        ed.Name = "Удаляемая";
        ed.SaveCommand.Execute(null);
        Pump();
        Assert.True(del.IsEffectivelyEnabled);

        // Сама кнопка только открывает вопрос: стратегия на месте.
        del.Flyout!.ShowAt(del);
        Pump();
        Assert.Contains(vm.Strategies, s => s.Name == "Удаляемая");
        var confirm = ((Control)((Flyout)del.Flyout).Content!).GetSelfAndLogicalDescendants().OfType<Button>().Single();
        Assert.Equal("Удалить «Удаляемая»?", ((Control)((Flyout)del.Flyout).Content!).GetSelfAndLogicalDescendants().OfType<TextBlock>().First().Text);

        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(confirm)).Invoke();
        Pump();
        Assert.DoesNotContain(vm.Strategies, s => s.Name == "Удаляемая");
        Assert.False(((Flyout)del.Flyout).IsOpen);
    }

    [AvaloniaFact]
    public void SearchStepsFollowProgressAndFinishTogether()
    {
        var search = CreateVm().Search;
        Assert.All(search.Steps, s => Assert.False(s.IsActive || s.IsDone));

        search.SetStep(3);
        Assert.Equal([true, true, false, false], search.Steps.Select(s => s.IsDone));
        Assert.Equal([false, false, true, false], search.Steps.Select(s => s.IsActive));

        search.ApplyResult(new SearchResult([], ["example.com"], [], null, false), ["example.com"]);
        Assert.All(search.Steps, s => Assert.True(s.IsDone));
        Assert.All(search.Steps, s => Assert.False(s.IsActive));
    }

    [AvaloniaFact]
    public void ProfilesAreNumberedLikeValidationMessages()
    {
        var ed = CreateVm().Editor;
        ed.NewCommand.Execute(null);
        ed.AddProfileCommand.Execute(null);
        ed.AddProfileCommand.Execute(null);
        Assert.Equal([1, 2, 3], ed.Profiles.Select(p => p.Number));
        Assert.Equal("TCP 443", ed.Profiles[0].FilterSummary);

        ed.Profiles[1].ArgsText = "filter-udp=443\ndpi-desync=fake\nbogus";
        Assert.Contains(ed.Errors, e => e.StartsWith("Профиль 2,", StringComparison.Ordinal));
        Assert.Equal("UDP 443", ed.Profiles[1].FilterSummary);

        ed.RemoveProfileCommand.Execute(ed.Profiles[0]);
        Assert.Equal([1, 2], ed.Profiles.Select(p => p.Number));
    }

    [AvaloniaFact]
    public void SaveStatusTracksEdits()
    {
        var ed = CreateVm().Editor;
        ed.NewCommand.Execute(null);
        Assert.Equal("Новая стратегия ещё не сохранена", ed.SaveStatus);
        Assert.False(ed.IsSaved);

        ed.SaveCommand.Execute(null);
        Assert.Equal("Сохранено", ed.SaveStatus);
        Assert.True(ed.IsSaved);

        ed.Name += " 2";
        Assert.Equal("Есть несохранённые изменения", ed.SaveStatus);
        Assert.False(ed.IsSaved);
    }

    [AvaloniaFact]
    public void PresetIsShownAsDescriptionWithListChips()
    {
        var ed = CreateVm().Editor;
        Assert.True(ed.IsReadOnly);
        Assert.False(ed.CanDeleteSelected);
        var chips = ed.Profiles.SelectMany(p => p.Chips).ToList();
        Assert.Contains(chips, c => c.Text == "blocked" && c.IsAccent);
        Assert.Contains(chips, c => c.Text == "general" && !c.IsAccent);
    }

    [Theory]
    [InlineData("0.3.0+4f2a9c1", "версия 0.3.0")]
    [InlineData("0.3.0-rc.1", "версия 0.3.0-rc.1")]
    [InlineData("0.0.0-dev+4f2a9c1", "версия dev")]
    [InlineData(null, "версия dev")]
    public void VersionLabel(string? informational, string expected) =>
        Assert.Equal(expected, MainWindowViewModel.FormatVersion(informational));

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }
}
