using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Settings;
using ZapretSmart.Core.Storage;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.App.ViewModels;

public sealed record StrategyItem(Strategy Strategy, bool IsPreset)
{
    public string Name => Strategy.Name;
    public string? Description => Strategy.Description;
    public string Origin => IsPreset ? "встроенная" : "моя";
}

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 500;

    private readonly SettingsStore _settingsStore;
    private readonly EngineRunner _runner;
    private AppSettings _settings;
    private bool _stopRequested;

    public MainWindowViewModel(AppPaths paths)
    {
        Paths = paths;
        _settingsStore = new SettingsStore(paths.SettingsFile);
        _settings = _settingsStore.Load();
        _communityEnabled = _settings.CommunityEnabled;

        Lists = new ListStore(paths.Layout.ListsDir, paths.BundledListsDir);
        UserStrategies = new UserStrategyStore(paths.UserStrategiesDir);
        try
        {
            Lists.SeedMissing();
        }
        catch (IOException e)
        {
            AppendLog("! Не удалось подготовить списки доменов: " + e.Message);
        }

        _runner = new EngineRunner(paths.EngineExe);
        _runner.Output += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _runner.Exited += code => Dispatcher.UIThread.Post(() => OnEngineExited(code));

        Editor = new StrategyEditorViewModel(this);
        Search = new SearchViewModel(this);

        ReloadStrategies();
        UpdateStatus();
    }

    public AppPaths Paths { get; }
    public ListStore Lists { get; }
    public UserStrategyStore UserStrategies { get; }
    public StrategyEditorViewModel Editor { get; }
    public SearchViewModel Search { get; }

    public ObservableCollection<StrategyItem> Strategies { get; } = [];
    public ObservableCollection<string> LoadErrors { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    public bool IsWindows => OperatingSystem.IsWindows();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private StrategyItem? _selectedStrategy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _communityEnabled;

    partial void OnCommunityEnabledChanged(bool value) => SaveSettings(_settings with { CommunityEnabled = value });

    partial void OnSelectedStrategyChanged(StrategyItem? value) =>
        SaveSettings(_settings with { SelectedStrategyId = value?.Strategy.Id });

    partial void OnIsSearchingChanged(bool value) => UpdateStatus();

    private bool CanStart() => IsWindows && !IsRunning && !IsSearching && SelectedStrategy is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        try
        {
            var argv = EngineCommandBuilder.Build(SelectedStrategy!.Strategy, Paths.Layout);
            AppendLog("> winws " + string.Join(' ', argv));
            _stopRequested = false;
            _runner.Start(argv);
            IsRunning = true;
        }
        catch (StrategyRejectedException e)
        {
            foreach (var err in e.Errors) AppendLog("! " + err);
        }
        catch (FileNotFoundException)
        {
            AppendLog("! Не найден движок: " + Paths.EngineExe);
        }
        UpdateStatus();
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task Stop() => StopEngineAsync();

    public async Task StopEngineAsync()
    {
        if (!IsRunning) return;
        _stopRequested = true;
        await _runner.StopAsync();
        IsRunning = false;
        UpdateStatus();
    }

    [RelayCommand]
    private void ReloadStrategies() => ReloadStrategies(null);

    public void ReloadStrategies(string? selectId)
    {
        selectId ??= SelectedStrategy?.Strategy.Id ?? _settings.SelectedStrategyId;
        Strategies.Clear();
        LoadErrors.Clear();

        var presets = StrategyLoader.LoadDirectory(Paths.PresetsDir);
        var presetIds = presets.Where(l => l.IsValid).Select(l => l.Strategy!.Id).ToHashSet();
        var loaded = presets.Select(l => (l, preset: true))
            .Concat(StrategyLoader.LoadDirectory(Paths.UserStrategiesDir).Select(l => (l, preset: false)));

        foreach (var (l, preset) in loaded)
        {
            if (!l.IsValid)
                LoadErrors.Add($"{Path.GetFileName(l.Source)}: {string.Join("; ", l.Errors)}");
            else if (!preset && presetIds.Contains(l.Strategy!.Id))
                LoadErrors.Add($"{Path.GetFileName(l.Source)}: id '{l.Strategy.Id}' занят встроенной стратегией");
            else
                Strategies.Add(new StrategyItem(l.Strategy!, preset));
        }

        SelectedStrategy = Strategies.FirstOrDefault(s => s.Strategy.Id == selectId) ?? Strategies.FirstOrDefault();
        Editor.SyncWith(Strategies);
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private void OpenListsFolder()
    {
        if (!IsWindows) return;
        Directory.CreateDirectory(Lists.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Lists.Directory } });
    }

    public void AppendLog(string line)
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    private void OnEngineExited(int code)
    {
        if (_stopRequested || !IsRunning) return;
        IsRunning = false;
        AppendLog($"! Движок завершился с кодом {code}");
        UpdateStatus();
    }

    private void UpdateStatus() =>
        StatusText = !IsWindows ? "Движок работает только под Windows"
            : IsSearching ? "Идёт поиск стратегии"
            : IsRunning ? "Обход включён"
            : "Обход выключен";

    private void SaveSettings(AppSettings settings)
    {
        if (settings == _settings) return;
        _settings = settings;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (IOException e)
        {
            AppendLog("! Не удалось сохранить настройки: " + e.Message);
        }
    }

    public void Dispose()
    {
        Search.Dispose();
        _runner.Dispose();
    }
}
