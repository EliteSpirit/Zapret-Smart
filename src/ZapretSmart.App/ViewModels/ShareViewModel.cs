using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Share;

namespace ZapretSmart.App.ViewModels;

/// <summary>
/// Раздача обхода на телефон, отдельная служба рядом с обходом ПК. Прокси открывает соединения телефона от имени ПК
/// с портов EngineCommandBuilder.ShareLocalPorts, и их обрабатывает собственный движок раздачи со своей стратегией.
/// Обход ПК эти порты пропускает, так что каждую службу можно включать и выключать, не трогая другую.
/// Прокси работает в той сети, где уже есть ПК и телефон (домашний Wi-Fi). Точка доступа ПК — запасной вариант,
/// когда общего Wi-Fi нет, и сама она не включается.
/// </summary>
public sealed partial class ShareViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly bool _manageSystem;

    /// <summary>Точку доступа включило приложение: значит, и выключает её приложение (при выключении раздачи и на выходе).</summary>
    private bool _startedHotspot;
    private readonly HashSet<IPAddress> _reportedStrangers = [];
    private ShareProxy? _proxy;
    private EngineRunner? _engine;
    private bool _engineStopRequested;
    private bool _reportedExhausted;
    private bool _loading;

    public ShareViewModel(MainWindowViewModel main, int port, bool manageSystem, bool enabled)
    {
        _main = main;
        _manageSystem = manageSystem;
        Port = port;
        _loading = true;
        IsEnabled = enabled;
        _loading = false;
        if (enabled) _ = StartAsync();
    }

    public bool IsHotspotAvailable => OperatingSystem.IsWindows();

    /// <summary>Движок раздачи запускается, если он есть: в поставке это только Windows-сборка.</summary>
    public bool CanRunEngine => File.Exists(_main.Paths.EngineExe);

    /// <summary>Те же стратегии, что у обхода ПК; выбор у раздачи свой.</summary>
    public IReadOnlyList<StrategyItem> Strategies => _main.Strategies;

    [ObservableProperty] private StrategyItem? _strategy;

    [ObservableProperty] private bool _isEngineRunning;

    private bool _syncingStrategy;

    /// <summary>Список стратегий пересобран: находим выбранную раздачей по id (по умолчанию — стратегию обхода ПК).</summary>
    public void OnStrategiesReloaded(string? savedId)
    {
        var id = Strategy?.Strategy.Id ?? savedId;
        _syncingStrategy = true;
        Strategy = _main.Strategies.FirstOrDefault(s => s.Strategy.Id == id) ?? _main.SelectedStrategy;
        _syncingStrategy = false;
        if (_proxy is not null && _engine is null) StartEngine();
    }

    partial void OnStrategyChanged(StrategyItem? value)
    {
        if (_syncingStrategy || _loading) return;
        _main.SaveShareStrategy(value?.Strategy.Id);
        // Другая стратегия во время работы: перезапускаем только движок раздачи, обход ПК не трогается.
        if (_engine is not null) _ = RestartEngineAsync();
    }

    private async Task RestartEngineAsync()
    {
        await StopEngineAsync();
        if (_proxy is not null) StartEngine();
    }

    private void StartEngine()
    {
        if (_engine is not null || Strategy is null) return;
        if (!CanRunEngine)
        {
            Status = "Движок обхода работает только в Windows: телефон получит интернет, но без обхода.";
            return;
        }
        EngineCommand cmd;
        try
        {
            cmd = EngineCommandBuilder.Build(Strategy.Strategy, _main.Paths.Layout, EngineScope.Share);
        }
        catch (StrategyRejectedException e)
        {
            foreach (var err in e.Errors) _main.AppendLog("! Раздача: " + err);
            Status = "Стратегия раздачи не прошла проверку, обхода для телефона нет.";
            return;
        }
        foreach (var w in cmd.Warnings) _main.AppendLog("! Раздача: " + w);
        _main.AppendLog("Раздача: > winws " + string.Join(' ', cmd.Argv));
        var runner = new EngineRunner(_main.Paths.EngineExe);
        // stderr EngineRunner тоже отдаёт через Output, отдельная подписка на ErrorOutput удвоила бы строки.
        runner.Output += line => Dispatcher.UIThread.Post(() => _main.AppendLog("[раздача] " + line));
        runner.Exited += code => Dispatcher.UIThread.Post(() => OnEngineExited(runner, code));
        _engineStopRequested = false;
        try
        {
            runner.Start(cmd.Argv);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            runner.Dispose();
            _main.AppendLog("! Раздача: не удалось запустить движок: " + e.Message);
            Status = "Не удалось запустить движок раздачи, обхода для телефона нет.";
            return;
        }
        _engine = runner;
        IsEngineRunning = true;
        Status = $"Раздача работает, стратегия «{Strategy.Name}».";
    }

    private void OnEngineExited(EngineRunner runner, int code)
    {
        if (_engine != runner) return;
        _engine = null;
        IsEngineRunning = false;
        runner.Dispose();
        if (_engineStopRequested) return;
        _main.AppendLog($"! Раздача: движок завершился с кодом {code}");
        Status = "Движок раздачи остановился, телефон получает интернет без обхода. Выключите и включите раздачу или выберите другую стратегию.";
    }

    private async Task StopEngineAsync()
    {
        var runner = _engine;
        if (runner is null) return;
        _engineStopRequested = true;
        _engine = null;
        IsEngineRunning = false;
        await runner.StopAsync();
        runner.Dispose();
        _main.AppendLog("Раздача: движок остановлен");
    }

    /// <summary>Порт, на котором прокси слушает. До запуска — порт из настроек.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PacUrl))]
    [NotifyPropertyChangedFor(nameof(ProfileUrl))]
    [NotifyPropertyChangedFor(nameof(PhonePacUrl))]
    private int _port;

    /// <summary>Адрес ПК, который вводится на телефоне.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PacUrl))]
    [NotifyPropertyChangedFor(nameof(HasAddress))]
    [NotifyPropertyChangedFor(nameof(ShowPhoneSetup))]
    [NotifyPropertyChangedFor(nameof(ProfileUrl))]
    [NotifyPropertyChangedFor(nameof(PhonePacUrl))]
    private string? _address;

    public bool HasAddress => IsEnabled && Address is not null;

    public string PacUrl => $"http://{Address}:{Port}/proxy.pac";

    /// <summary>
    /// Домашняя сеть Wi-Fi, для которой iPhone получает профиль. Если ПК подключён к ней по Wi-Fi, имя и пароль
    /// берутся из Windows; если кабелем, их вводят руками. Пустой пароль — профиль без пароля.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileUrl))]
    [NotifyPropertyChangedFor(nameof(PhoneNetworkName))]
    [NotifyPropertyChangedFor(nameof(HasHomeNetwork))]
    [NotifyPropertyChangedFor(nameof(CanMakeProfile))]
    [NotifyPropertyChangedFor(nameof(PhonePacUrl))]
    [NotifyPropertyChangedFor(nameof(ShowPhoneSetup))]
    private string _homeSsid = "";

    [ObservableProperty] private string _homePassword = "";

    /// <summary>Имя и пароль сети взяты из Windows: поля ввода не нужны.</summary>
    [ObservableProperty] private bool _homeDetected;

    [ObservableProperty] private string? _homeNote;

    private bool _homeIsOpen;

    public bool HasHomeNetwork => !string.IsNullOrWhiteSpace(HomeSsid);

    /// <summary>Профиль iPhone можно выдать: известна домашняя сеть или работает точка доступа ПК.</summary>
    public bool CanMakeProfile => HasHomeNetwork || UseHotspot;

    /// <summary>Телефон ходит через домашнюю сеть (обычный случай) или через точку доступа ПК, если общего Wi-Fi нет.</summary>
    private bool UseHotspot => !HasHomeNetwork && IsHotspotOn && Hotspot?.Ssid is not null;

    private string PhoneHost => UseHotspot ? HotspotAddress : Address ?? "";

    public string PhoneNetworkName => UseHotspot ? Hotspot!.Ssid! : HomeSsid;

    /// <summary>Файл автонастройки для Android: тот же, что в профиле iPhone.</summary>
    public string PhonePacUrl => $"http://{PhoneHost}:{Port}/proxy.pac";

    /// <summary>Остальные адреса ПК, если сетей несколько.</summary>
    public ObservableCollection<string> OtherAddresses { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddress))]
    [NotifyPropertyChangedFor(nameof(ShowPhoneSetup))]
    private bool _isEnabled;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _lastClient;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartHotspotCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopHotspotCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshHotspotCommand))]
    private bool _isHotspotBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotText))]
    [NotifyPropertyChangedFor(nameof(IsHotspotOn))]
    [NotifyPropertyChangedFor(nameof(ShowPhoneSetup))]
    [NotifyPropertyChangedFor(nameof(ProfileUrl))]
    [NotifyPropertyChangedFor(nameof(PhonePacUrl))]
    [NotifyPropertyChangedFor(nameof(PhoneNetworkName))]
    [NotifyPropertyChangedFor(nameof(CanMakeProfile))]
    [NotifyCanExecuteChangedFor(nameof(StartHotspotCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopHotspotCommand))]
    private HotspotState? _hotspot;

    public bool IsHotspotOn => Hotspot?.IsOn == true;

    /// <summary>Адрес ПК в сети точки доступа Windows.</summary>
    public string HotspotAddress => (_hotspotAddress ?? NetworkPolicy.WindowsHotspotAddress).ToString();

    private IPAddress? _hotspotAddress;

    /// <summary>Короткий адрес профиля для Safari: его легко набрать руками.</summary>
    public string ProfileUrl => $"{PhoneHost}:{Port}/i";

    public bool ShowPhoneSetup => IsEnabled && (Address is not null || UseHotspot);

    public static readonly TimeSpan ProbeDuration = TimeSpan.FromSeconds(20);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeCommand))]
    private bool _isProbing;

    [ObservableProperty] private string? _probeText;

    private bool CanProbe() => IsHotspotAvailable && !IsProbing;

    /// <summary>
    /// Видит ли WinDivert трафик телефона на точке доступа без прокси. От ответа зависит, можно ли сделать
    /// обычную раздачу: если трафик не виден, доработка движка бессмысленна.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task Probe()
    {
        IsProbing = true;
        ProbeText = $"Слушаю {(int)ProbeDuration.TotalSeconds} секунд. Откройте на телефоне, подключённом к точке доступа ПК, пару сайтов. Прокси на телефоне для этой проверки должен быть выключен.";
        try
        {
            var dll = Path.Combine(Path.GetDirectoryName(_main.Paths.EngineExe)!, "WinDivert.dll");
            var r = await ForwardProbe.RunAsync(dll, ProbeDuration, CancellationToken.None);
            ProbeText = ForwardProbe.Explain(r);
            _main.AppendLog($"Проверка раздачи: пересылка {r.ForwardPackets}, входящие {r.InboundPackets}" + (r.Error is null ? "" : ", ошибка: " + r.Error));
            foreach (var sample in r.Samples) _main.AppendLog("  " + sample);
        }
        finally
        {
            IsProbing = false;
        }
    }

    public string HotspotText => Hotspot switch
    {
        null => "Если общего Wi-Fi нет (например, ПК подключён кабелем к модему), ПК может сам стать сетью Wi-Fi.",
        { Error: { } error } => "Точка доступа: " + error + ".",
        // Пароль показывается отдельно моноширинным шрифтом: Inter рисует «x» между цифрами как «×».
        { IsOn: true } h => $"Точка доступа включена, подключено устройств: {h.Clients}.",
        _ => "Точка доступа выключена.",
    };

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loading) return;
        _main.SaveShareEnabled(value);
        _ = value ? StartAsync() : StopAsync(removeFirewallRule: true);
    }

    private async Task StartAsync()
    {
        if (_proxy is not null) return;
        var proxy = new ShareProxy(new ShareProxyOptions
        {
            Port = Port,
            Wifi = CurrentWifi,
            // Без движка раздачи (не Windows) порты не важны; в Windows по ним движок раздачи находит свой трафик.
            OutboundPorts = OperatingSystem.IsWindows() ? EngineCommandBuilder.ShareLocalPorts : null,
        });
        try
        {
            proxy.Start();
        }
        catch (SocketException e)
        {
            await proxy.DisposeAsync();
            Status = e.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Порт {Port} занят другой программой. Раздача не включилась."
                : "Не удалось включить раздачу: " + e.Message;
            return;
        }
        _proxy = proxy;
        Port = proxy.Port;
        proxy.ClientConnected += ip => Dispatcher.UIThread.Post(() => LastClient = $"Последнее подключение: {ip} в {DateTime.Now:HH:mm}");
        proxy.ClientRejected += ip => Dispatcher.UIThread.Post(() =>
        {
            if (_reportedStrangers.Add(ip)) _main.AppendLog($"! Раздача: отклонено подключение не из локальной сети ({ip})");
        });
        proxy.OutboundPortsExhausted += () => Dispatcher.UIThread.Post(() =>
        {
            if (_reportedExhausted) return;
            _reportedExhausted = true;
            var (low, high) = EngineCommandBuilder.ShareLocalPorts;
            _main.AppendLog($"! Раздача: заняты все порты {low}–{high}, часть соединений телефона идёт без обхода");
        });
        RefreshAddresses();
        _main.AppendLog($"Раздача: прокси слушает порт {Port}");
        StartEngine();

        if (_manageSystem && Environment.ProcessPath is { } exe)
        {
            var error = await ShareFirewall.AllowAsync(exe, Port, CancellationToken.None);
            if (error is not null)
            {
                Status = error + ". Телефон может не достучаться до ПК.";
                _main.AppendLog("! Раздача: " + error);
            }
        }
    }

    /// <summary>
    /// Сеть для профиля iPhone. Вызывается из потока прокси; строки неизменяемые, поэтому чтение без блокировки безопасно.
    /// </summary>
    private WifiNetwork? CurrentWifi()
    {
        var ssid = HomeSsid.Trim();
        if (ssid.Length > 0 && IPAddress.TryParse(Address, out var lan))
            return new WifiNetwork(ssid, HomePassword.Length > 0 ? HomePassword : null, lan, _homeIsOpen);
        var h = Hotspot;
        return h is { IsOn: true, Ssid: { Length: > 0 } hs, Passphrase: { } pass }
            ? new WifiNetwork(hs, pass, _hotspotAddress ?? NetworkPolicy.WindowsHotspotAddress)
            : null;
    }

    /// <summary>Домашняя сеть из Windows: имя и пароль сохранённого профиля Wi-Fi, к которому подключён ПК.</summary>
    private void DetectHomeNetwork()
    {
        if (!_manageSystem || HomeDetected) return;
        var wifi = WindowsWifi.Current();
        if (wifi is null)
        {
            HomeNote = "ПК подключён не по Wi-Fi, поэтому имя домашней сети неизвестно. Введите его, чтобы получить профиль для iPhone.";
            return;
        }
        HomeSsid = wifi.Ssid;
        HomePassword = wifi.Passphrase ?? "";
        _homeIsOpen = wifi.IsOpen;
        HomeDetected = true;
        HomeNote = !wifi.IsPersonal
            ? "Сеть с логином (корпоративная): профиль iPhone для неё не подойдёт, настройте прокси на телефоне вручную."
            : wifi.Passphrase is null && !wifi.IsOpen
                ? "Пароль сети прочитать не удалось: iPhone спросит его при установке профиля."
                : null;
    }

    private async Task StopAsync(bool removeFirewallRule)
    {
        var proxy = _proxy;
        _proxy = null;
        if (proxy is not null)
        {
            await proxy.DisposeAsync();
            _main.AppendLog("Раздача: прокси остановлен");
        }
        await StopEngineAsync();
        Address = null;
        OtherAddresses.Clear();
        LastClient = null;
        Status = "";
        if (removeFirewallRule && _manageSystem) await ShareFirewall.RemoveAsync(CancellationToken.None);
        if (_startedHotspot)
        {
            _startedHotspot = false;
            await RunHotspotAsync(HotspotAction.Stop);
            _main.AppendLog("Раздача: точка доступа выключена");
        }
    }

    [RelayCommand]
    private void RefreshAddresses()
    {
        var all = NetworkPolicy.FindLocalAddresses();
        _hotspotAddress = all.FirstOrDefault(a => a.IsWindowsHotspot)?.Address;
        OnPropertyChanged(nameof(HotspotAddress));
        OnPropertyChanged(nameof(ProfileUrl));
        OnPropertyChanged(nameof(PhonePacUrl));
        DetectHomeNetwork();
        Address = all.Count > 0 ? all[0].Address.ToString() : null;
        OtherAddresses.Clear();
        foreach (var a in all.Skip(1)) OtherAddresses.Add($"{a.Address} ({a.InterfaceName})");
        if (IsEnabled && _proxy is not null && all.Count == 0)
            Status = "ПК не подключён к локальной сети. Подключите его к Wi-Fi или включите точку доступа.";
    }

    private bool CanStartHotspot() => IsHotspotAvailable && !IsHotspotBusy && !IsHotspotOn;
    private bool CanStopHotspot() => IsHotspotAvailable && !IsHotspotBusy && IsHotspotOn;
    private bool CanRefreshHotspot() => IsHotspotAvailable && !IsHotspotBusy;

    [RelayCommand(CanExecute = nameof(CanStartHotspot))]
    private async Task StartHotspot()
    {
        await RunHotspotAsync(HotspotAction.Start);
        if (Hotspot is { IsOn: true }) _startedHotspot = true;
    }

    [RelayCommand(CanExecute = nameof(CanStopHotspot))]
    private async Task StopHotspot()
    {
        await RunHotspotAsync(HotspotAction.Stop);
        _startedHotspot = false;
    }

    [RelayCommand(CanExecute = nameof(CanRefreshHotspot))]
    private Task RefreshHotspot() => RunHotspotAsync(HotspotAction.Status);

    private async Task RunHotspotAsync(HotspotAction action)
    {
        IsHotspotBusy = true;
        try
        {
            Hotspot = await WindowsHotspot.RunAsync(action, Path.Combine(Path.GetTempPath(), "ZapretSmart"), CancellationToken.None);
            if (Hotspot.Error is not null && action != HotspotAction.Status) _main.AppendLog("! Точка доступа: " + Hotspot.Error);
            // Адрес 192.168.137.1 появляется у ПК через пару секунд после включения.
            if (action == HotspotAction.Start && Hotspot.IsOn) await Task.Delay(TimeSpan.FromSeconds(3));
            if (_proxy is not null) RefreshAddresses();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Hotspot = HotspotState.Failed(e.Message);
        }
        finally
        {
            IsHotspotBusy = false;
        }
    }

    public void Dispose()
    {
        var proxy = _proxy;
        _proxy = null;
        // Правило брандмауэра остаётся: раздача включена и при следующем запуске поднимется снова.
        proxy?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        // Движок раздачи: Dispose убивает процесс синхронно (как у обхода ПК), Job Object подстрахует при падении.
        var engine = _engine;
        _engine = null;
        _engineStopRequested = true;
        engine?.Dispose();
        // Точку доступа, которую включили кнопкой в приложении, гасим: без приложения в ней нет прокси.
        if (_startedHotspot)
        {
            try
            {
                WindowsHotspot.RunAsync(HotspotAction.Stop, Path.Combine(Path.GetTempPath(), "ZapretSmart"), CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(15));
            }
            catch (AggregateException)
            {
            }
        }
    }
}
