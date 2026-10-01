using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.App.Theming;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Hosts;
using ZapretSmart.Core.Updates;
using ZapretSmart.Core.Community;
using ZapretSmart.Core.Lists;
using ZapretSmart.Core.Search;
using ZapretSmart.Core.Watchdog;
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
    private bool _disposed;
    private CancellationTokenSource? _watchdogCts;

    public static readonly TimeSpan WatchdogFirstCheck = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);
    public static IReadOnlyList<string> CheckTargets => CommunityReports.StandardTargets;

    public MainWindowViewModel(AppPaths paths)
    {
        Paths = paths;
        _settingsStore = new SettingsStore(paths.SettingsFile);
        _settings = _settingsStore.Load();
        _communityEnabled = _settings.CommunityEnabled;
        _watchdogEnabled = _settings.WatchdogEnabled;
        _closeToTray = _settings.CloseToTray;
        _shareAutoStart = _settings.ShareAutoStart;
        _shareLogSites = _settings.ShareLogSites;
        _theme = AppTheme.Find(_settings.ThemeId);
        _animatedBackdrop = _settings.AnimatedBackdrop;
        _menuOnTop = _settings.MenuOnTop;
        Themes = AppTheme.All.Select(t => new ThemeOption(t, selected => Theme = selected) { IsSelected = t == _theme }).ToList();

        ListStore = new ListStore(paths.Layout, paths.BundledListsDir);
        UserStrategies = new UserStrategyStore(paths.UserStrategiesDir);
        try
        {
            foreach (var u in ListStore.SeedMissing())
                AppendLog($"Списки: в {u.ListId}.txt из новой версии добавлено {u.Added}, убрано {u.Removed}. Ваши правки сохранены.");
        }
        catch (IOException e)
        {
            AppendLog("! Не удалось подготовить списки доменов: " + e.Message);
        }

        _runner = new EngineRunner(paths.EngineExe);
        _runner.Output += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _runner.Exited += code => Dispatcher.UIThread.Post(() => OnEngineExited(code));

        Blocklists = new BlocklistsViewModel(this, new ListUpdater(ListUpdater.CreateHttpClient(), paths.Layout.ListsDir, paths.Layout.IpsetsDir));
        Editor = new StrategyEditorViewModel(this);
        var http = Releases.CreateHttpClient(CurrentVersion);
        Hosts = new HostsViewModel(this,
            paths.HostsFile is null ? null : new HostsManager(paths.HostsFile, paths.DataDir, paths.BundledHostsSnapshot, http),
            _settings.HostsEnabled);
        Updates = new UpdatesViewModel(this, http);
        Search = new SearchViewModel(this);
        Share = new ShareViewModel(this, _settings.SharePort, paths.ManageSystem, _settings.ShareEnabled || _settings.ShareAutoStart);

        ReloadStrategies();
        UpdateStatus();
        ReportLastUpdate();
    }

    /// <summary>Итог установки другой версии пишет скрипт после выхода приложения; показываем его при следующем запуске.</summary>
    private void ReportLastUpdate()
    {
        var log = Path.Combine(Paths.DataDir, "update.log");
        try
        {
            if (!File.Exists(log) || DateTime.UtcNow - File.GetLastWriteTimeUtc(log) > TimeSpan.FromMinutes(15)) return;
            var last = File.ReadLines(log).LastOrDefault(l => l.Trim().Length > 0);
            if (last is not null) AppendLog((last.Contains("ошибка", StringComparison.Ordinal) ? "! " : "") + "Обновление: " + last);
        }
        catch (IOException)
        {
        }
    }

    public AppPaths Paths { get; }
    public ListStore ListStore { get; }
    public BlocklistsViewModel Blocklists { get; }
    public UserStrategyStore UserStrategies { get; }
    public StrategyEditorViewModel Editor { get; }
    public HostsViewModel Hosts { get; }
    public UpdatesViewModel Updates { get; }
    public SearchViewModel Search { get; }
    public ShareViewModel Share { get; }

    public ObservableCollection<StrategyItem> Strategies { get; } = [];
    public ObservableCollection<string> LoadErrors { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    public bool IsWindows => OperatingSystem.IsWindows();

    public IReadOnlyList<ThemeOption> Themes { get; }

    /// <summary>«версия 0.3.0»; у локальной сборки без номера — «версия dev».</summary>
    public static string VersionText { get; } = FormatVersion(
        typeof(MainWindowViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion);

    /// <summary>Номер этой сборки без хвоста «+коммит»: 0.3.0, 0.0.0-dev.</summary>
    public static string CurrentVersion { get; } =
        (typeof(MainWindowViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0-dev")
        .Split('+')[0];

    public static string FormatVersion(string? informational)
    {
        var v = informational?.Split('+')[0];
        return "версия " + (string.IsNullOrEmpty(v) || v.StartsWith("0.0.0", StringComparison.Ordinal) ? "dev" : v);
    }

    [ObservableProperty] private AppTheme _theme;

    partial void OnThemeChanged(AppTheme value)
    {
        foreach (var t in Themes) t.IsSelected = t.Theme == value;
        SaveSettings(_settings with { ThemeId = value.Id });
    }

    [ObservableProperty] private bool _animatedBackdrop;

    partial void OnAnimatedBackdropChanged(bool value) => SaveSettings(_settings with { AnimatedBackdrop = value });

    [ObservableProperty] private bool _menuOnTop;

    partial void OnMenuOnTopChanged(bool value) => SaveSettings(_settings with { MenuOnTop = value });

    /// <summary>На месте «Включить» показывается «Выключить». Скрытая из пары кнопок всегда недоступна.</summary>
    public bool ShowStop => IsRunning || IsSwitching;

    /// <summary>Идёт работа, которая сама закончится: поиск или подбор стратегии сторожем.</summary>
    public bool IsBusy => IsSearching || IsSwitching;

    [ObservableProperty] private string _statusShort = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private StrategyItem? _selectedStrategy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _communityEnabled;

    partial void OnCommunityEnabledChanged(bool value) => SaveSettings(_settings with { CommunityEnabled = value });

    [ObservableProperty] private bool _watchdogEnabled;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private string _watchdogStatus = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isSwitching;

    partial void OnCloseToTrayChanged(bool value) => SaveSettings(_settings with { CloseToTray = value });

    /// <summary>Включать раздачу при каждом запуске программы.</summary>
    [ObservableProperty] private bool _shareAutoStart;

    partial void OnShareAutoStartChanged(bool value) => SaveSettings(_settings with { ShareAutoStart = value });

    /// <summary>Писать в журнал раздачи адреса сайтов. Прокси читает значение на каждом запросе.</summary>
    [ObservableProperty] private bool _shareLogSites;

    partial void OnShareLogSitesChanged(bool value) => SaveSettings(_settings with { ShareLogSites = value });

    /// <summary>
    /// Запуск вместе с Windows (задача Планировщика). Значение берётся из самого Планировщика, а не из настроек:
    /// задачу могли удалить руками. Меняется только в настоящем приложении на Windows.
    /// </summary>
    [ObservableProperty] private bool _startWithWindows;

    [ObservableProperty] private string? _autostartStatus;

    public bool CanStartWithWindows => Paths.ManageSystem;

    private bool _syncingAutostart;

    /// <summary>
    /// Читает задачу автозапуска. Если программу перенесли в другую папку, задача перенастраивается на новый путь:
    /// иначе при входе запускалась бы старая копия или ничего.
    /// </summary>
    public async Task SyncAutostartAsync()
    {
        if (!Paths.ManageSystem || Environment.ProcessPath is not { } exe) return;
        var command = await WindowsAutostart.QueryAsync(CancellationToken.None);
        _syncingAutostart = true;
        StartWithWindows = command is not null;
        _syncingAutostart = false;
        if (command is not null && !string.Equals(Path.GetFullPath(command), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
        {
            var error = await WindowsAutostart.EnableAsync(exe, CancellationToken.None);
            AppendLog(error is null ? "Автозапуск: задача перенастроена на " + Path.GetFileName(exe) + " в новой папке" : "! Автозапуск: " + error);
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncingAutostart || !Paths.ManageSystem) return;
        _ = ApplyAutostartAsync(value);
    }

    private async Task ApplyAutostartAsync(bool value)
    {
        string? error;
        try
        {
            error = value
                ? Environment.ProcessPath is { } exe ? await WindowsAutostart.EnableAsync(exe, CancellationToken.None) : "не удалось узнать путь к программе"
                : await WindowsAutostart.DisableAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            error = e.Message;
        }
        AutostartStatus = error;
        if (error is null)
        {
            AppendLog(value ? "Автозапуск включён: Zapret Smart запустится при входе в Windows свёрнутым в трей" : "Автозапуск выключен");
            return;
        }
        AppendLog("! Автозапуск: " + error);
        // Не вышло: галка возвращается к тому, что на самом деле в Планировщике.
        _syncingAutostart = true;
        StartWithWindows = !value;
        _syncingAutostart = false;
    }

    partial void OnWatchdogEnabledChanged(bool value)
    {
        SaveSettings(_settings with { WatchdogEnabled = value });
        if (!value) StopWatchdog();
        else if (IsRunning) StartWatchdog();
    }

    partial void OnSelectedStrategyChanged(StrategyItem? value) =>
        SaveSettings(_settings with { SelectedStrategyId = value?.Strategy.Id });

    partial void OnIsSearchingChanged(bool value) => UpdateStatus();

    // После Dispose ничего не запускаем: иначе движок остался бы работать без окна.
    private bool CanStart() => !_disposed && IsWindows && !IsRunning && !IsSearching && !IsSwitching && SelectedStrategy is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (StartEngine() && WatchdogEnabled) StartWatchdog();
    }

    private bool StartEngine()
    {
        try
        {
            var cmd = EngineCommandBuilder.Build(SelectedStrategy!.Strategy, Paths.Layout);
            foreach (var w in cmd.Warnings) AppendLog("! " + w);
            AppendLog("> winws " + string.Join(' ', cmd.Argv));
            _stopRequested = false;
            _runner.Start(cmd.Argv);
            IsRunning = true;
            UpdateStatus();
            return true;
        }
        catch (StrategyRejectedException e)
        {
            foreach (var err in e.Errors) AppendLog("! " + err);
        }
        catch (FileNotFoundException)
        {
            AppendLog("! Не найден движок: " + Paths.EngineExe);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            AppendLog("! Не удалось запустить движок: " + e.Message + ". Приложение запущено от администратора? Не заблокировал ли winws.exe антивирус?");
        }
        UpdateStatus();
        return false;
    }

    private bool CanStop() => IsRunning || IsSwitching;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task Stop() => StopEngineAsync();

    public async Task StopEngineAsync()
    {
        StopWatchdog();
        await StopEngineOnlyAsync();
    }

    private async Task StopEngineOnlyAsync()
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
        // Пока обход работает, выбранной остаётся запущенная стратегия: иначе на главной показывалась бы не та, что работает.
        if (IsRunning) selectId = SelectedStrategy?.Strategy.Id;
        selectId ??= SelectedStrategy?.Strategy.Id ?? _settings.SelectedStrategyId;
        Strategies.Clear();
        LoadErrors.Clear();

        var presets = StrategyLoader.LoadDirectory(Paths.PresetsDir);
        var presetIds = presets.Where(l => l.IsValid).Select(l => l.Strategy!.Id).ToHashSet();
        var loaded = presets.Select(l => (l, preset: true))
            .Concat(StrategyLoader.LoadUserDirectory(Paths.UserStrategiesDir).Select(l => (l, preset: false)));

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
        Share.OnStrategiesReloaded(_settings.ShareStrategyId);
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private void OpenListsFolder()
    {
        if (!IsWindows) return;
        Directory.CreateDirectory(ListStore.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { ListStore.Directory } });
    }

    private bool _autostartSynced;

    /// <summary>Фоновые задачи после показа окна. Не из конструктора: тесты создают модель без сети.</summary>
    public void StartBackgroundWork()
    {
        if (!_autostartSynced)
        {
            _autostartSynced = true;
            _ = SyncAutostartAsync();
        }
        _ = Blocklists.UpdateStaleAsync();
        _ = Hosts.RefreshIfEnabledAsync();
    }

    public void SaveHostsEnabled(bool value) => SaveSettings(_settings with { HostsEnabled = value });

    public void SaveShareEnabled(bool value) => SaveSettings(_settings with { ShareEnabled = value });

    public void SaveShareStrategy(string? id) => SaveSettings(_settings with { ShareStrategyId = id });

    /// <summary>Установка другой версии: приложение должно закрыться, чтобы скрипт заменил файлы.</summary>
    public event Action? ExitForUpdateRequested;

    public void RequestExitForUpdate() => ExitForUpdateRequested?.Invoke();

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

    private void UpdateStatus()
    {
        StatusText = !IsWindows ? "Движок работает только под Windows"
            : IsSwitching ? "Сторож подбирает стратегию"
            : IsSearching ? "Идёт поиск стратегии"
            : IsRunning ? "Обход включён"
            : "Обход выключен";
        StatusShort = !IsWindows ? "Только для Windows"
            : IsSwitching ? "Подбор стратегии"
            : IsSearching ? "Идёт поиск"
            : IsRunning ? "Обход включён"
            : "Обход выключен";
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

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdogCts = new CancellationTokenSource();
        _ = RunWatchdogAsync(_watchdogCts.Token);
    }

    private void StopWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = null;
        WatchdogStatus = "";
    }

    private async Task RunWatchdogAsync(CancellationToken ct)
    {
        var tracker = new HealthTracker(failuresBeforeSwitch: 2, switchCooldown: TimeSpan.FromMinutes(30));
        var prober = new HttpsProber(TimeSpan.FromSeconds(8));
        var targets = CheckTargets;
        var delay = WatchdogFirstCheck;
        var warnedNeverWorked = false;
        WatchdogStatus = "Сторож: первая проверка через несколько секунд";
        try
        {
            while (true)
            {
                await Task.Delay(delay, ct);
                delay = WatchdogInterval;
                if (!IsRunning || IsSearching) continue;

                var passed = await StrategyRanker.CheckAsync(prober, targets, ct);
                WatchdogStatus = $"Сторож: {DateTime.Now:HH:mm}, открываются {passed} из {targets.Count} проверочных сайтов";
                switch (tracker.Observe(passed, DateTime.UtcNow))
                {
                    case HealthVerdict.NeverWorked when !warnedNeverWorked:
                        warnedNeverWorked = true;
                        AppendLog("Сторож: с этой стратегией проверочные сайты не открываются. Переключаться вслепую не буду: запустите поиск.");
                        break;
                    case HealthVerdict.Degraded:
                        AppendLog($"Сторож: открываются {passed} из {targets.Count}, было {tracker.Baseline}. Проверю ещё раз.");
                        break;
                    case HealthVerdict.Cooldown:
                        AppendLog("Сторож: снова хуже, но стратегию уже меняли недавно. Жду, чтобы не метаться.");
                        break;
                    case HealthVerdict.Switch:
                        await SwitchStrategyAsync(tracker, prober, targets, passed, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SwitchStrategyAsync(HealthTracker tracker, IProber prober, IReadOnlyList<string> targets, int passedNow, CancellationToken ct)
    {
        var current = SelectedStrategy!;
        AppendLog($"Сторож: сайты перестали открываться ({passedNow} из {targets.Count}), проверяю стратегии");
        IsSwitching = true;
        UpdateStatus();
        try
        {
            await StopEngineOnlyAsync();
            // Текущая идёт первой: при равенстве остаёмся на ней, сбой мог быть временным.
            var candidates = Strategies.Where(s => s.Strategy.Intercept.Tcp is not null).Select(s => s.Strategy)
                .OrderBy(s => s.Id == current.Strategy.Id ? 0 : 1).ToList();
            using var host = new WinwsEngineHost(Paths.EngineExe, Paths.Layout, TimeSpan.FromSeconds(8));
            var ranked = await new StrategyRanker(host, prober).RankAsync(candidates, targets, ct);
            var best = ranked[0];
            var switched = best.Strategy.Id != current.Strategy.Id;
            SelectedStrategy = Strategies.FirstOrDefault(s => s.Strategy.Id == best.Strategy.Id) ?? current;
            AppendLog(switched
                ? $"Сторож: переключено на «{best.Strategy.Name}», открываются {best.Passed} из {best.Total}"
                : $"Сторож: другие стратегии не лучше, «{current.Name}» перезапущена ({best.Passed} из {best.Total})");
            tracker.Restarted(best.Passed, DateTime.UtcNow, switched);
        }
        finally
        {
            IsSwitching = false;
            // Отмена значит «Выключить» или закрытие приложения: тогда обход не возвращаем.
            if (ct.IsCancellationRequested) AppendLog("Сторож: подбор прерван, обход выключен");
            else if (!_disposed && !IsRunning) StartEngine();
            UpdateStatus();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopWatchdog();
        Search.Dispose();
        Share.Dispose();
        _runner.Dispose();
    }
}
