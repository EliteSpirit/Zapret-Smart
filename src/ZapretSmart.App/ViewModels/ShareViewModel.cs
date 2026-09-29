using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Share;

namespace ZapretSmart.App.ViewModels;

/// <summary>
/// Раздача обхода на телефон. Прокси на ПК открывает соединения телефона от своего имени, поэтому движок обхода
/// обрабатывает их как свои. Точка доступа Windows нужна, только если ПК и телефон не в одной сети Wi-Fi.
/// </summary>
public sealed partial class ShareViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly bool _manageFirewall;
    private readonly HashSet<IPAddress> _reportedStrangers = [];
    private ShareProxy? _proxy;
    private bool _loading;

    public ShareViewModel(MainWindowViewModel main, int port, bool manageFirewall, bool enabled)
    {
        _main = main;
        _manageFirewall = manageFirewall;
        Port = port;
        _loading = true;
        IsEnabled = enabled;
        _loading = false;
        if (enabled) _ = StartAsync();
    }

    public bool IsHotspotAvailable => OperatingSystem.IsWindows();

    /// <summary>Порт, на котором прокси слушает. До запуска — порт из настроек.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PacUrl))]
    private int _port;

    /// <summary>Адрес ПК, который вводится на телефоне.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PacUrl))]
    [NotifyPropertyChangedFor(nameof(HasAddress))]
    private string? _address;

    public bool HasAddress => IsEnabled && Address is not null;

    public string PacUrl => $"http://{Address}:{Port}/proxy.pac";

    /// <summary>Остальные адреса ПК, если сетей несколько.</summary>
    public ObservableCollection<string> OtherAddresses { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddress))]
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
    [NotifyCanExecuteChangedFor(nameof(StartHotspotCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopHotspotCommand))]
    private HotspotState? _hotspot;

    public bool IsHotspotOn => Hotspot?.IsOn == true;

    public string HotspotText => Hotspot switch
    {
        null => "Если телефон не в той же сети Wi-Fi, что ПК, включите точку доступа: ПК сам станет сетью Wi-Fi.",
        { Error: { } error } => "Точка доступа: " + error + ".",
        { IsOn: true } h => $"Точка доступа включена. Сеть «{h.Ssid}», пароль {h.Passphrase}. Подключено устройств: {h.Clients}.",
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
        var proxy = new ShareProxy(new ShareProxyOptions { Port = Port });
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
        RefreshAddresses();
        Status = _main.IsRunning ? "Раздача работает." : "Прокси работает, но обход выключен: телефон получит интернет без обхода.";
        _main.AppendLog($"Раздача: прокси слушает порт {Port}");

        if (_manageFirewall && Environment.ProcessPath is { } exe)
        {
            var error = await ShareFirewall.AllowAsync(exe, Port, CancellationToken.None);
            if (error is not null)
            {
                Status = error + ". Телефон может не достучаться до ПК.";
                _main.AppendLog("! Раздача: " + error);
            }
        }
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
        Address = null;
        OtherAddresses.Clear();
        LastClient = null;
        Status = "";
        if (removeFirewallRule && _manageFirewall) await ShareFirewall.RemoveAsync(CancellationToken.None);
    }

    /// <summary>Статус обхода сменился: подсказка в карточке должна это отражать.</summary>
    public void OnBypassChanged()
    {
        if (_proxy is not null)
            Status = _main.IsRunning ? "Раздача работает." : "Прокси работает, но обход выключен: телефон получит интернет без обхода.";
    }

    [RelayCommand]
    private void RefreshAddresses()
    {
        var all = NetworkPolicy.FindLocalAddresses();
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
    private Task StartHotspot() => RunHotspotAsync(HotspotAction.Start);

    [RelayCommand(CanExecute = nameof(CanStopHotspot))]
    private Task StopHotspot() => RunHotspotAsync(HotspotAction.Stop);

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
    }
}
