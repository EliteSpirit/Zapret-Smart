using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly HashSet<IPAddress> _seenClients = [];
    private readonly ShareLog _log;
    private ShareProxy? _proxy;
    private EngineRunner? _engine;
    private bool _engineStopRequested;
    private bool _reportedExhausted;
    private bool _loading;

    public ShareViewModel(MainWindowViewModel main, int port, bool manageSystem, bool enabled)
    {
        _main = main;
        _manageSystem = manageSystem;
        _log = new ShareLog(Path.Combine(main.Paths.DataDir, "share.log"));
        Port = port;
        _loading = true;
        IsEnabled = enabled;
        _loading = false;
        if (enabled) _ = StartSafelyAsync();
    }

    /// <summary>Модель окна: карточка раздачи показывает галки автозапуска, которые хранит она.</summary>
    public MainWindowViewModel Main => _main;

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
            foreach (var err in e.Errors) Log(err, warning: true);
            Status = "Стратегия раздачи не прошла проверку, обхода для телефона нет.";
            return;
        }
        foreach (var w in cmd.Warnings) Log(w, warning: true);
        Log("> winws " + string.Join(' ', cmd.Argv));
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
            Log("не удалось запустить движок: " + e.Message, warning: true);
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
        Log($"движок завершился с кодом {code}", warning: true);
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
        Log("движок остановлен");
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckReachCommand))]
    private bool _isCheckingReach;

    [ObservableProperty] private string? _reachText;

    private HashSet<IPAddress>? _reachConnected;
    private CancellationTokenSource? _reachStop;

    private bool CanCheckReach() => IsHotspotAvailable && !IsCheckingReach;

    private bool IsOwnAddress(IPAddress ip) =>
        NetworkPolicy.FindLocalAddresses().Any(a => a.Address.Equals(ip));

    /// <summary>
    /// «Почему телефон не достучался»: слушает ли прокси, доходят ли до ПК попытки подключения, пускает ли их
    /// брандмауэр. Итог в карточке и в журнале раздачи. Прерывается раньше, как только телефон дошёл до прокси.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckReach))]
    private async Task CheckReach()
    {
        if (_proxy is null || Address is null || !IPAddress.TryParse(Address, out var address))
        {
            ReachText = "Сначала включите раздачу: проверять нечего, пока прокси не запущен и у ПК нет адреса в сети.";
            return;
        }
        IsCheckingReach = true;
        var port = Port;
        var seconds = (int)ShareReachProbe.Duration.TotalSeconds;
        ReachText = $"Проверяю до {seconds} секунд. Сейчас откройте на телефоне http://{Address}:{port}/proxy.pac (телефон в той же сети Wi-Fi).";
        Log($"проверка связи: жду подключения к {Address}:{port} до {seconds} с");
        var syns = new System.Collections.Concurrent.ConcurrentDictionary<IPAddress, int>();
        _reachConnected = [];
        using var stop = new CancellationTokenSource();
        _reachStop = stop;
        try
        {
            var self = await ShareReachProbe.SelfConnectsAsync(address, port, CancellationToken.None);
            Log(self ? $"проверка связи: с самого ПК {Address}:{port} отвечает" : $"проверка связи: {Address}:{port} не отвечает даже с самого ПК", warning: !self);
            var loopback = await ShareReachProbe.SelfConnectsAsync(IPAddress.Loopback, port, CancellationToken.None);
            Log(loopback ? $"проверка связи: 127.0.0.1:{port} отвечает" : $"проверка связи: 127.0.0.1:{port} не отвечает", warning: !loopback);
            var interceptors = OperatingSystem.IsWindows()
                ? ShareReachProbe.ForeignInterceptors(Path.GetDirectoryName(_main.Paths.EngineExe)!)
                : [];
            foreach (var other in interceptors) Log("проверка связи: другой перехватчик пакетов: " + other, warning: true);
            var firewall = await ShareReachProbe.ReadFirewallAsync(CancellationToken.None);
            if (firewall.BlocksAllInbound) Log("проверка связи: в брандмауэре Windows включено «Блокировать все входящие подключения»", warning: true);
            if (firewall.ThirdParty.Count > 0) Log("проверка связи: сторонний брандмауэр: " + string.Join(", ", firewall.ThirdParty));

            string? sniffError = null;
            if (self)
            {
                var dll = Path.Combine(Path.GetDirectoryName(_main.Paths.EngineExe)!, "WinDivert.dll");
                sniffError = await ForwardProbe.SniffAsync(dll, ShareReachProbe.SynFilter(port), ShareReachProbe.Duration, packet =>
                {
                    if (ShareReachProbe.SynSource(packet.Span) is { } source) syns.AddOrUpdate(source, 1, (_, n) => n + 1);
                }, stop.Token);
            }
            var report = new ReachReport(port, Address, ShareReachProbe.Duration, self, loopback, interceptors, firewall,
                new Dictionary<IPAddress, int>(syns), [.. _reachConnected], sniffError);
            var lines = ShareReachProbe.Explain(report);
            ReachText = string.Join("\n", lines);
            foreach (var line in lines) Log("проверка связи: " + line);
        }
        finally
        {
            _reachConnected = null;
            _reachStop = null;
            IsCheckingReach = false;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindConflictsCommand))]
    private bool _isFindingConflicts;

    [ObservableProperty] private string? _conflictText;

    private bool CanFindConflicts() => IsHotspotAvailable && !IsFindingConflicts;

    /// <summary>
    /// «Найти мешающие программы»: тестовое подключение к своему порту, журнал аудита WFP, фильтры WFP и процессы,
    /// сверенные со списком известных файрволов, антивирусов и VPN. Работает и без включённой раздачи: порт свой.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindConflicts))]
    private async Task FindConflicts()
    {
        IPAddress? address = Address is { } a && IPAddress.TryParse(a, out var parsed) ? parsed : NetworkPolicy.FindLocalAddresses().FirstOrDefault()?.Address;
        if (address is null)
        {
            ConflictText = "У ПК нет адреса в локальной сети: подключите его к роутеру и повторите.";
            return;
        }
        IsFindingConflicts = true;
        ConflictText = "Ищу. Это до минуты: выгрузка фильтров Windows идёт долго.";
        Log($"поиск мешающих программ: проверяю {address}");
        try
        {
            var report = await Task.Run(() => ConflictScan.RunAsync(address, Path.GetDirectoryName(_main.Paths.EngineExe)!,
                Path.Combine(_main.Paths.DataDir, "diag"), CancellationToken.None));
            var lines = ConflictScan.Explain(report);
            ConflictText = string.Join("\n\n", lines);
            foreach (var line in lines) Log("поиск мешающих программ: " + line, warning: !report.LanConnects);
            foreach (var detail in report.Details) Log("поиск мешающих программ: " + detail);
        }
        catch (Exception e)
        {
            _log.Write("! " + e);
            ConflictText = "Поиск не удался: " + e.Message;
            Log("поиск мешающих программ не удался: " + e.Message, warning: true);
        }
        finally
        {
            IsFindingConflicts = false;
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
        _ = value ? StartSafelyAsync() : StopSafelyAsync(removeFirewallRule: true);
    }

    /// <summary>
    /// Запуск раздачи без броска в пустоту: исключение попадает в журнал и в статус карточки. Иначе прокси молча
    /// не запускался бы, а в журнале не было бы ни строки.
    /// </summary>
    private async Task StartSafelyAsync()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception e)
        {
            // Полный текст с местом ошибки идёт только в файл: его присылают для разбора.
            _log.Write("! " + e);
            Log($"раздача не запустилась: {e.GetType().Name}: {e.Message}", warning: true);
            // Всё, что успело запуститься (прокси, правило брандмауэра), снимается: карточка не должна говорить
            // «не запустилась», пока порт на самом деле открыт.
            await StopSafelyAsync(removeFirewallRule: true);
            Status = "Раздача не запустилась: " + e.Message;
        }
    }

    private async Task StopSafelyAsync(bool removeFirewallRule)
    {
        try
        {
            await StopAsync(removeFirewallRule);
        }
        catch (Exception e)
        {
            _log.Write("! " + e);
            Log($"раздача остановилась с ошибкой: {e.GetType().Name}: {e.Message}", warning: true);
        }
    }

    private async Task StartAsync()
    {
        if (_proxy is not null) return;
        Log($"включаю раздачу на порту {Port}");
        // Правило ставится до открытия порта: иначе Windows успевает спросить «разрешить доступ?», и после «Отмены»
        // её запрет перекрывает наше разрешение. Если netsh не справился, порт всё равно открывается.
        if (_manageSystem && FirewallProgram is { } exe)
        {
            // Полный путь не пишется: в нём обычно имя пользователя Windows, а журнал присылают.
            Log("ставлю правило брандмауэра для " + Path.GetFileName(exe));
            var firewall = await ShareFirewall.AllowAsync(exe, Port, CancellationToken.None);
            if (firewall.Error is { } error)
            {
                Status = error + ". Телефон может не достучаться до ПК.";
                Log(error, warning: true);
            }
            else
            {
                Log($"брандмауэр открыт для порта {Port} из локальной сети");
            }
            if (firewall.RemovedProgramRules > 0)
                Log($"сняты прежние правила брандмауэра для {Path.GetFileName(exe)} ({firewall.RemovedProgramRules}), в том числе запреты Windows");
        }
        // Пока ставилось правило, раздачу могли выключить (выключение успело снять правило раньше, чем мы его
        // поставили) или запустить повторно.
        if (_proxy is not null) return;
        if (!IsEnabled)
        {
            if (_manageSystem) await ShareFirewall.RemoveAsync(CancellationToken.None);
            return;
        }
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
        catch (Exception e)
        {
            await proxy.DisposeAsync();
            if (e is not SocketException socket) throw;
            Status = socket.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Порт {Port} занят другой программой. Раздача не включилась."
                : "Не удалось включить раздачу: " + socket.Message;
            Log(Status, warning: true);
            if (_manageSystem) await ShareFirewall.RemoveAsync(CancellationToken.None);
            return;
        }
        _proxy = proxy;
        Port = proxy.Port;
        proxy.ClientConnected += ip => Dispatcher.UIThread.Post(() =>
        {
            LastClient = $"Последнее подключение: {ip} в {DateTime.Now:HH:mm}";
            if (_seenClients.Add(ip)) Log($"подключилось устройство {ip}");
            if (_reachConnected is { } reached && !IPAddress.IsLoopback(ip) && !IsOwnAddress(ip))
            {
                reached.Add(ip);
                _reachStop?.Cancel();
            }
        });
        proxy.ClientRejected += ip => Dispatcher.UIThread.Post(() =>
        {
            if (_reportedStrangers.Add(ip)) Log($"отклонено подключение не из локальной сети ({ip})", warning: true);
        });
        proxy.Logged += (ip, text) => Dispatcher.UIThread.Post(() => Log($"{ip}: {text}", collapseRepeats: true));
        proxy.OutboundPortsExhausted += () => Dispatcher.UIThread.Post(() =>
        {
            if (_reportedExhausted) return;
            _reportedExhausted = true;
            var (low, high) = EngineCommandBuilder.ShareLocalPorts;
            Log($"заняты все порты {low}–{high}, часть соединений телефона идёт без обхода", warning: true);
        });
        RefreshAddresses();
        Log(Address is null
            ? $"прокси слушает порт {Port}, но ПК не в локальной сети"
            : $"прокси слушает порт {Port}, адрес для телефона {Address}:{Port}"
              + (OtherAddresses.Count > 0 ? $", другие адреса ПК: {string.Join(", ", OtherAddresses)}" : ""));
        StartEngine();
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
            Log("прокси остановлен");
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
            Log("точка доступа выключена");
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

    /// <summary>
    /// Строка в журнал на вкладке «Обход» и в share.log. С collapseRepeats (запросы устройств) повтор той же строки
    /// чаще раза в минуту пропускается.
    /// </summary>
    private void Log(string text, bool warning = false, bool collapseRepeats = false)
    {
        if (_log.Write(warning ? "! " + text : text, collapseRepeats)) _main.AppendLog((warning ? "! " : "") + "Раздача: " + text);
    }

    public string LogFile => _log.FilePath;

    /// <summary>Программа, для которой ставится правило брандмауэра: сам ZapretSmart.exe, в тестах подставной путь.</summary>
    private string? FirewallProgram => _main.Paths.FirewallProgram ?? Environment.ProcessPath;

    [RelayCommand]
    private void OpenLog()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(_log.FilePath)) _log.Write("журнал раздачи создан");
        Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { _log.FilePath } });
    }
}
