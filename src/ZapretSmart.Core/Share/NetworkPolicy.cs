using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ZapretSmart.Core.Share;

/// <summary>Адрес ПК в локальной сети, по которому к прокси подключается телефон.</summary>
public sealed record LocalAddress(IPAddress Address, string InterfaceName, bool IsWindowsHotspot);

/// <summary>
/// Кого пускать в прокси и куда. Прокси слушает все интерфейсы, поэтому граница проходит здесь:
/// клиенты только из локальной сети, а назначение не может быть самим ПК (localhost) или служебным адресом.
/// </summary>
public static class NetworkPolicy
{
    /// <summary>Адрес ПК в сети «Мобильного хот-спота» Windows: он всегда такой.</summary>
    public static readonly IPAddress WindowsHotspotAddress = IPAddress.Parse("192.168.137.1");

    /// <summary>
    /// Порты почты закрыты: иначе через ПК можно было бы рассылать спам от его имени.
    /// Остальные порты открыты, приложения на телефоне ходят не только на 443.
    /// </summary>
    public static readonly IReadOnlySet<int> BlockedPorts = new HashSet<int> { 25, 465, 587 };

    /// <summary>
    /// Клиент из локальной сети: частные диапазоны, link-local и сам ПК. 100.64.0.0/10 (CGNAT) сюда не входит:
    /// в этой сети соседи — чужие абоненты провайдера.
    /// </summary>
    public static bool IsLocalClient(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }
        return false;
    }

    /// <summary>
    /// Куда прокси соединяет. Нельзя на localhost ПК: там слушают службы, рассчитанные только на сам компьютер,
    /// и телефон (или кто угодно в Wi-Fi) не должен до них дотягиваться. Проверяется уже разрешённый адрес,
    /// поэтому имя, которое резолвится в 127.0.0.1, тоже не пройдёт.
    /// </summary>
    public static bool IsAllowedDestination(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0 || b[0] >= 224) return false;          // 0.0.0.0/8, мультикаст, 240/4 и широковещательный
            if (b[0] == 169 && b[1] == 254) return false;        // link-local
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
                return false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Порядок адресов: сначала домашний Wi-Fi (там телефон обычно и находится), потом кабель, сеть точки доступа ПК
    /// последней: это запасной вариант, когда общего Wi-Fi нет.
    /// </summary>
    public static int Rank(bool isWindowsHotspot, bool isWireless) => isWindowsHotspot ? 2 : isWireless ? 0 : 1;

    /// <summary>
    /// IPv4-адреса ПК в локальных сетях в порядке <see cref="Rank"/>.
    /// Виртуальные адаптеры (Hyper-V, VirtualBox, WSL) пропускаются: телефону туда не попасть.
    /// </summary>
    public static IReadOnlyList<LocalAddress> FindLocalAddresses()
    {
        var found = new List<(LocalAddress Address, int Rank)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var name = nic.Name + " " + nic.Description;
            if (IsVirtual(name)) continue;
            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            foreach (var ua in props.UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || !IsLocalClient(ip)) continue;
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                // Адрес в конфликте с другим устройством (Duplicate) или ещё не проверенный (Tentative) Windows держит
                // на адаптере, но не использует: телефон по нему попал бы к чужому устройству или в пустоту.
                if (OperatingSystem.IsWindows() && !IsUsable(ua.DuplicateAddressDetectionState)) continue;
                var hotspot = ip.Equals(WindowsHotspotAddress);
                var rank = Rank(hotspot, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                found.Add((new LocalAddress(ip, nic.Name, hotspot), rank));
            }
        }
        return found.OrderBy(f => f.Rank).Select(f => f.Address).ToList();
    }

    /// <summary>Адрес, которым Windows пользуется: прошёл проверку на конфликт (Preferred) или устаревает, но ещё работает.</summary>
    public static bool IsUsable(DuplicateAddressDetectionState state) =>
        state is DuplicateAddressDetectionState.Preferred or DuplicateAddressDetectionState.Deprecated;

    private static bool IsVirtual(string name) =>
        new[] { "Hyper-V", "vEthernet", "VirtualBox", "VMware", "WSL", "Docker", "TAP-", "Wintun", "WireGuard", "ZeroTier", "Tailscale" }
            .Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase));
}
