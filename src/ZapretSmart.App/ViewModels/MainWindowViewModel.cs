using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Settings;
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

    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly EngineRunner _runner;
    private AppSettings _settings;
    private bool _stopRequested;

    public MainWindowViewModel(AppPaths paths)
    {
        _paths = paths;
        _settingsStore = new SettingsStore(paths.SettingsFile);
        _settings = _settingsStore.Load();
        _communityEnabled = _settings.CommunityEnabled;

        _runner = new EngineRunner(paths.EngineExe);
        _runner.Output += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _runner.Exited += code => Dispatcher.UIThread.Post(() => OnEngineExited(code));

        ReloadStrategies();
        UpdateStatus();
    }

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
    private string _statusText = "";

    [ObservableProperty]
    private bool _communityEnabled;

    partial void OnCommunityEnabledChanged(bool value) => SaveSettings(_settings with { CommunityEnabled = value });

    partial void OnSelectedStrategyChanged(StrategyItem? value) =>
        SaveSettings(_settings with { SelectedStrategyId = value?.Strategy.Id });

    private bool CanStart() => IsWindows && !IsRunning && SelectedStrategy is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        try
        {
            var argv = EngineCommandBuilder.Build(SelectedStrategy!.Strategy, _paths.Layout);
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
            AppendLog("! Не найден движок: " + _paths.EngineExe);
        }
        UpdateStatus();
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        _stopRequested = true;
        await _runner.StopAsync();
        IsRunning = false;
        UpdateStatus();
    }

    [RelayCommand]
    private void ReloadStrategies()
    {
        var selectedId = SelectedStrategy?.Strategy.Id ?? _settings.SelectedStrategyId;
        Strategies.Clear();
        LoadErrors.Clear();

        var loaded = StrategyLoader.LoadDirectory(_paths.PresetsDir).Select(l => (l, preset: true))
            .Concat(StrategyLoader.LoadDirectory(_paths.UserStrategiesDir).Select(l => (l, preset: false)));

        foreach (var (l, preset) in loaded)
        {
            if (l.IsValid)
                Strategies.Add(new StrategyItem(l.Strategy!, preset));
            else
                LoadErrors.Add($"{Path.GetFileName(l.Source)}: {string.Join("; ", l.Errors)}");
        }

        SelectedStrategy = Strategies.FirstOrDefault(s => s.Strategy.Id == selectedId) ?? Strategies.FirstOrDefault();
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private void OnEngineExited(int code)
    {
        if (_stopRequested || !IsRunning) return;
        IsRunning = false;
        AppendLog($"! Движок завершился с кодом {code}");
        UpdateStatus();
    }

    private void UpdateStatus() =>
        StatusText = !IsWindows ? "Движок работает только под Windows"
            : IsRunning ? "Обход включён"
            : "Обход выключен";

    private void AppendLog(string line)
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

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

    public void Dispose() => _runner.Dispose();
}
