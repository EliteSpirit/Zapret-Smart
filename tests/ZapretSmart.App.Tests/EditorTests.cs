using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZapretSmart.App.ViewModels;
using ZapretSmart.App.Views;
using ZapretSmart.Core.Engine;

namespace ZapretSmart.App.Tests;

public sealed class EditorTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "zs-app-tests-" + Guid.NewGuid().ToString("N"));

    private MainWindowViewModel CreateVm()
    {
        var bin = AppContext.BaseDirectory;
        return new MainWindowViewModel(new AppPaths(
            Path.Combine(bin, "engine", "winws.exe"),
            new EngineLayout(Path.Combine(bin, "engine", "fake"), Path.Combine(_data, "lists")),
            Path.Combine(bin, "lists"),
            Path.Combine(bin, "strategies"),
            Path.Combine(_data, "strategies"),
            Path.Combine(_data, "settings.json")));
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void AllTabsRender()
    {
        var vm = CreateVm();
        var w = new MainWindow { DataContext = vm };
        w.Show();
        var tabs = w.GetVisualDescendants().OfType<TabControl>().Single();
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Pump();
            Assert.NotNull(w.CaptureRenderedFrame());
        }
        Assert.Equal(3, vm.Strategies.Count);
        Assert.Empty(vm.LoadErrors);
    }

    [AvaloniaFact]
    public void BundledListsAreCopiedToUserFolder()
    {
        var vm = CreateVm();
        Assert.Contains("general", vm.Lists.Ids());
        Assert.StartsWith(_data, vm.Lists.Directory);
    }

    [AvaloniaFact]
    public void PresetIsReadOnly()
    {
        var ed = CreateVm().Editor;
        Assert.True(ed.SelectedItem!.IsPreset);
        Assert.True(ed.IsReadOnly);
        Assert.False(ed.SaveCommand.CanExecute(null));
        Assert.False(ed.DeleteCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ForbiddenValueBlocksSaveUntilFixed()
    {
        var ed = CreateVm().Editor;
        ed.NewCommand.Execute(null);
        Assert.True(ed.SaveCommand.CanExecute(null));

        ed.Profiles[0].ArgsText += "\ndpi-desync-fake-tls=C:\\Users\\me\\secret.txt";
        Assert.False(ed.IsValid);
        Assert.False(ed.SaveCommand.CanExecute(null));
        Assert.Contains(ed.Errors, e => e.StartsWith("Профиль 1, строка 4", StringComparison.Ordinal));

        ed.Profiles[0].ArgsText = "filter-tcp=443\ndpi-desync=multisplit";
        Assert.True(ed.IsValid);
    }

    [AvaloniaFact]
    public void AddOptionInsertsFlagOrKey()
    {
        var ed = CreateVm().Editor;
        ed.NewCommand.Execute(null);
        var p = ed.Profiles[0];
        p.SelectedOption = "dpi-desync-fooling";
        p.AddOptionCommand.Execute(null);
        Assert.EndsWith("\ndpi-desync-fooling=", p.ArgsText);
        Assert.Null(p.SelectedOption);
        p.SelectedOption = "  hostcase ";
        p.AddOptionCommand.Execute(null);
        Assert.EndsWith("\nhostcase", p.ArgsText);
        p.SelectedOption = "debug";
        Assert.False(p.AddOptionCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SaveCopyDeleteRoundTrip()
    {
        var vm = CreateVm();
        var ed = vm.Editor;

        ed.NewCommand.Execute(null);
        ed.Name = "Тестовая";
        ed.Profiles[0].Hostlist = ed.ListChoices.Single(c => c.Id == "general");
        ed.SaveCommand.Execute(null);

        var saved = vm.Strategies.Single(s => s.Name == "Тестовая");
        Assert.False(saved.IsPreset);
        Assert.Equal("general", saved.Strategy.Profiles[0].Hostlist);
        Assert.True(File.Exists(vm.UserStrategies.PathFor(saved.Strategy.Id)));
        Assert.Same(saved, ed.SelectedItem);
        Assert.False(ed.SaveCommand.CanExecute(null));

        ed.SelectedItem = vm.Strategies.First(s => s.IsPreset);
        ed.CopyCommand.Execute(null);
        Assert.False(ed.IsReadOnly);
        Assert.EndsWith("(копия)", ed.Name);
        ed.SaveCommand.Execute(null);
        Assert.Equal(2, vm.Strategies.Count(s => !s.IsPreset));

        ed.SelectedItem = vm.Strategies.Single(s => s.Name == "Тестовая");
        ed.DeleteCommand.Execute(null);
        Assert.DoesNotContain(vm.Strategies, s => s.Name == "Тестовая");
        Assert.False(File.Exists(vm.UserStrategies.PathFor(saved.Strategy.Id)));
        Assert.NotNull(ed.SelectedItem);
        Assert.Equal(ed.SelectedItem!.Name, ed.Name);
    }

    [AvaloniaFact]
    public void UserStrategyCannotShadowPreset()
    {
        Directory.CreateDirectory(Path.Combine(_data, "strategies"));
        var preset = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "strategies")).First());
        File.WriteAllText(Path.Combine(_data, "strategies", "dup.json"), preset);

        var vm = CreateVm();
        Assert.Equal(3, vm.Strategies.Count);
        Assert.Contains(vm.LoadErrors, e => e.Contains("занят встроенной"));
    }

    [AvaloniaFact]
    public void SearchIsDisabledOffWindows()
    {
        var vm = CreateVm();
        Assert.Equal(OperatingSystem.IsWindows(), vm.Search.StartCommand.CanExecute(null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }
}
