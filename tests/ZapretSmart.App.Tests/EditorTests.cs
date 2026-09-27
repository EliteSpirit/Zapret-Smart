using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZapretSmart.App.ViewModels;
using ZapretSmart.App.Views;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Search;

namespace ZapretSmart.App.Tests;

public sealed class EditorTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "zs-app-tests-" + Guid.NewGuid().ToString("N"));

    private MainWindowViewModel CreateVm(string? engineExe = null)
    {
        var bin = AppContext.BaseDirectory;
        return new MainWindowViewModel(new AppPaths(
            engineExe ?? Path.Combine(bin, "engine", "winws.exe"),
            new EngineLayout(Path.Combine(bin, "engine", "fake"), Path.Combine(_data, "lists"), Path.Combine(_data, "ipsets")),
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
        Assert.Equal(["blocked", "general"], vm.ListStore.HostlistIds());
        Assert.Equal(["blocked-ip"], vm.ListStore.IpsetIds());
        Assert.StartsWith(_data, vm.ListStore.Directory);
        Assert.True(File.Exists(Path.Combine(_data, "lists", "general.txt")));
        Assert.True(File.Exists(Path.Combine(_data, "lists", "exclude.txt")));
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
        ed.Profiles[0].HostToggles.Single(t => t.Id == "blocked").IsChecked = false;
        ed.Profiles[0].IpToggles.Single(t => t.Id == "blocked-ip").IsChecked = true;
        ed.SaveCommand.Execute(null);

        var saved = vm.Strategies.Single(s => s.Name == "Тестовая");
        Assert.False(saved.IsPreset);
        Assert.Equal(["general"], saved.Strategy.Profiles[0].Hostlists);
        Assert.Equal(["blocked-ip"], saved.Strategy.Profiles[0].Ipsets);
        Assert.True(saved.Strategy.Profiles[0].AutoHostlist);
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
        var preset = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "strategies", "10-general-multisplit.json"));
        File.WriteAllText(Path.Combine(_data, "strategies", "general-multisplit.json"), preset);

        var vm = CreateVm();
        Assert.Equal(3, vm.Strategies.Count);
        Assert.Contains(vm.LoadErrors, e => e.Contains("занят встроенной"));
    }

    [AvaloniaFact]
    public void NewProfileDefaultsToBlockedListsAndAutoList()
    {
        var ed = CreateVm().Editor;
        ed.NewCommand.Execute(null);
        var p = ed.Profiles[0];
        Assert.Equal(["blocked", "general"], p.HostToggles.Where(t => t.IsChecked).Select(t => t.Id));
        Assert.True(p.AutoHostlist);
        Assert.Equal("", p.ScopeHint);
    }

    [AvaloniaFact]
    public void ProfileWithoutListsWarnsAboutAllTraffic()
    {
        var ed = CreateVm().Editor;
        ed.NewCommand.Execute(null);
        var p = ed.Profiles[0];
        foreach (var t in p.HostToggles) t.IsChecked = false;
        p.AutoHostlist = false;
        Assert.Contains("ко всему трафику", p.ScopeHint);
        p.IpToggles[0].IsChecked = true;
        Assert.Equal("", p.ScopeHint);
    }

    [AvaloniaFact]
    public void PresetListsAreShownAsChecked()
    {
        var ed = CreateVm().Editor;
        ed.SelectedItem = ed.Items.First(i => i.Strategy.Id == "general-multisplit");
        Assert.All(ed.Profiles, p => Assert.Equal(["blocked", "general"], p.HostToggles.Where(t => t.IsChecked).Select(t => t.Id)));
        Assert.True(ed.Profiles[2].AutoHostlist);
    }

    [AvaloniaFact]
    public void BlocklistsShowStatusAndAutoListCanBeCleared()
    {
        var vm = CreateVm();
        Assert.All(vm.Blocklists.Items, i => Assert.Equal("ещё не загружен", i.Info));
        Assert.Equal("Автосписок пуст", vm.Blocklists.AutoInfo);

        File.WriteAllText(Path.Combine(_data, "lists", "auto.txt"), "a.com\nb.com\n");
        vm.Blocklists.RefreshAutoCommand.Execute(null);
        Assert.Equal("В автосписке 2 домена", vm.Blocklists.AutoInfo);
        vm.Blocklists.ClearAutoCommand.Execute(null);
        Assert.Equal("Автосписок пуст", vm.Blocklists.AutoInfo);
        Assert.Equal("", File.ReadAllText(Path.Combine(_data, "lists", "auto.txt")));
    }

    [AvaloniaFact]
    public void StartWarnsAboutListsNotYetDownloaded()
    {
        var vm = CreateVm();
        vm.SelectedStrategy = vm.Strategies.First(s => s.Strategy.Id == "general-multisplit");
        vm.StartCommand.Execute(null);
        Assert.Contains(vm.Log, l => l.Contains("список доменов 'blocked' ещё не загружен"));
        Assert.Contains(vm.Log, l => l.StartsWith("> winws", StringComparison.Ordinal) && l.Contains("--hostlist-domains=zapret-smart.invalid"));
        Assert.False(vm.IsRunning);
    }

    [Theory]
    [InlineData(1, "1 домен")]
    [InlineData(2, "2 домена")]
    [InlineData(5, "5 доменов")]
    [InlineData(11, "11 доменов")]
    [InlineData(14, "14 доменов")]
    [InlineData(21, "21 домен")]
    [InlineData(112, "112 доменов")]
    [InlineData(81187, "81\u00a0187 доменов")]
    public void RussianPlural(int n, string expected)
    {
        Assert.Equal(expected, BlocklistsViewModel.Plural(n, "домен", "домена", "доменов"));
    }

    private static SearchResult FoundResult() => new(
        ["mysite.org", "www.youtube.com"], ["ok.com"], [],
        new CandidateScore(new Candidate("multisplit", ["dpi-desync=multisplit", "dpi-desync-split-pos=1"]), [], 2, TimeSpan.FromMilliseconds(120), null),
        false);

    [AvaloniaFact]
    public void SavedSearchResultCoversTheSitesItWasFoundFor()
    {
        var vm = CreateVm();
        vm.Search.ApplyResult(FoundResult(), ["mysite.org", "www.youtube.com", "ok.com"]);
        vm.Search.ResultName = "Найдено";
        vm.Search.SaveResultCommand.Execute(null);

        var saved = vm.Strategies.Single(s => s.Name == "Найдено").Strategy;
        var ownList = saved.Profiles[0].Hostlists[0];
        Assert.Equal(saved.Id, ownList);
        Assert.Equal(["mysite.org", "www.youtube.com"], File.ReadAllLines(Path.Combine(_data, "lists", ownList + ".txt")));
        Assert.Contains("blocked", saved.Profiles[0].Hostlists);
        Assert.True(saved.Profiles[0].AutoHostlist);
        Assert.Same(vm.SelectedStrategy!.Strategy.Id, saved.Id);
    }

    [AvaloniaFact]
    public void TooLongResultNameIsReportedNotCrash()
    {
        var vm = CreateVm();
        vm.Search.ApplyResult(FoundResult(), ["mysite.org"]);
        vm.Search.ResultName = new string('я', 150);
        vm.Search.SaveResultCommand.Execute(null);
        Assert.Contains("Не сохранено", vm.Search.Summary);
        Assert.All(vm.Strategies, s => Assert.True(s.IsPreset));
    }

    [AvaloniaFact]
    public void EngineThatCannotStartIsLoggedNotCrash()
    {
        // Так выглядит winws.exe, заблокированный антивирусом или повреждённый: Process.Start бросает Win32Exception.
        Directory.CreateDirectory(_data);
        var bogus = Path.Combine(_data, "winws.exe");
        File.WriteAllText(bogus, "not an executable");
        var vm = CreateVm(bogus);
        vm.SelectedStrategy = vm.Strategies.First(s => s.Strategy.Id == "discord-voice");
        vm.StartCommand.Execute(null);
        Assert.Contains(vm.Log, l => l.Contains("Не удалось запустить движок"));
        Assert.False(vm.IsRunning);
    }

    [AvaloniaFact]
    public void UnsavedDraftSurvivesExternalReload()
    {
        var vm = CreateVm();
        vm.Editor.NewCommand.Execute(null);
        vm.Editor.Name = "Черновик";
        vm.ReloadStrategies(null);
        Assert.Equal("Черновик", vm.Editor.Name);
        Assert.Null(vm.Editor.EditingId);
    }

    [AvaloniaFact]
    public void ReloadWhileRunningKeepsRunningStrategySelected()
    {
        var vm = CreateVm();
        var running = vm.Strategies.First(s => s.Strategy.Id == "discord-voice");
        vm.SelectedStrategy = running;
        vm.IsRunning = true;

        vm.Editor.NewCommand.Execute(null);
        vm.Editor.Name = "Другая";
        vm.Editor.SaveCommand.Execute(null);

        Assert.Equal("discord-voice", vm.SelectedStrategy!.Strategy.Id);
        vm.IsRunning = false;
    }

    [AvaloniaFact]
    public void BadUserStrategyFileIsShownInEditor()
    {
        Directory.CreateDirectory(Path.Combine(_data, "strategies"));
        File.WriteAllText(Path.Combine(_data, "strategies", "a.json"),
            """{ "id": "foo", "name": "x", "intercept": { "tcp": "443" }, "profiles": [null] }""");
        var vm = CreateVm();
        Assert.Contains(vm.Editor.LoadErrors, e => e.StartsWith("a.json", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void NothingStartsAfterDispose()
    {
        var vm = CreateVm();
        vm.Dispose();
        Assert.False(vm.StartCommand.CanExecute(null));
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
