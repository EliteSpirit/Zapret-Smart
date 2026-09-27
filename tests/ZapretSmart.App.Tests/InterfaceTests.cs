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
        ((IPseudoClasses)primary.Classes).Add(":pointerover");
        ((IPseudoClasses)danger.Classes).Add(":pointerover");
        Pump(0.4);

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
