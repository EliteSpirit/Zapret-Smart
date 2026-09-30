using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace ZapretSmart.Core.Share;

/// <summary>
/// Сеть Wi-Fi, к которой подключён ПК. Passphrase — пароль из сохранённого профиля Windows (null, если сеть
/// открытая или пароль прочитать нельзя). IsPersonal: пароль общий на всех (WPA/WPA2/WPA3-Personal) или сеть открыта;
/// для корпоративных сетей с логином профиль iPhone без настроек EAP не установится.
/// </summary>
public sealed record WifiCredentials(string Ssid, string? Passphrase, bool IsOpen, bool IsPersonal);

/// <summary>
/// Текущая сеть Wi-Fi через WLAN API Windows. Не через netsh: его вывод переведён на язык системы
/// («Содержимое ключа»), а профиль из WlanGetProfile — XML с постоянными именами. Пароль Windows отдаёт открытым
/// текстом только администратору (флаг WLAN_PROFILE_GET_PLAINTEXT_KEY); приложение и так работает с этими правами.
/// </summary>
public static class WindowsWifi
{
    private const uint ClientVersion = 2;
    private const int OpcodeCurrentConnection = 7;
    private const int StateConnected = 1;
    private const uint GetPlaintextKey = 4;
    private const int InterfaceInfoSize = 16 + 512 + 4;
    private const int ProfileNameOffset = 8;
    private const int SsidOffset = 8 + 512;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved,
        out uint dataSize, out IntPtr data, IntPtr valueType);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr handle, ref Guid iface, string profileName, IntPtr reserved,
        out IntPtr profileXml, ref uint flags, out uint grantedAccess);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    /// <summary>Сеть, к которой ПК подключён по Wi-Fi; null — не Windows, нет Wi-Fi или ПК подключён кабелем.</summary>
    public static WifiCredentials? Current()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Query();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.Xml.XmlException)
        {
            // На Windows Server без службы WLAN библиотеки может не быть.
            return null;
        }
    }

    private static WifiCredentials? Query()
    {
        if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle) != 0) return null;
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return null;
            try
            {
                var count = Marshal.ReadInt32(list);
                for (var i = 0; i < count; i++)
                {
                    var info = list + 8 + i * InterfaceInfoSize;
                    var guid = Marshal.PtrToStructure<Guid>(info);
                    if (WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out _, out var attrs, IntPtr.Zero) != 0)
                        continue;
                    try
                    {
                        if (Marshal.ReadInt32(attrs) != StateConnected) continue;
                        var profileName = Marshal.PtrToStringUni(attrs + ProfileNameOffset)!;
                        var ssidLength = Math.Clamp(Marshal.ReadInt32(attrs + SsidOffset), 0, 32);
                        var ssidBytes = new byte[ssidLength];
                        Marshal.Copy(attrs + SsidOffset + 4, ssidBytes, 0, ssidLength);
                        var ssid = Encoding.UTF8.GetString(ssidBytes);

                        var flags = GetPlaintextKey;
                        if (WlanGetProfile(handle, ref guid, profileName, IntPtr.Zero, out var xml, ref flags, out _) != 0)
                            return new WifiCredentials(ssid, null, false, true);
                        try
                        {
                            var parsed = ParseProfile(Marshal.PtrToStringUni(xml)!);
                            return parsed with { Ssid = ssid };
                        }
                        finally
                        {
                            WlanFreeMemory(xml);
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(attrs);
                    }
                }
                return null;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Разбор XML профиля WLAN. Пароль берётся, только если он не зашифрован (protected=false): иначе Windows
    /// отдала бы зашифрованный блок, который телефону бесполезен.
    /// </summary>
    public static WifiCredentials ParseProfile(string xml)
    {
        var doc = XDocument.Parse(xml);
        XElement? Find(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
        var ssid = Find("SSID")?.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? Find("name")?.Value ?? "";
        var auth = Find("authentication")?.Value ?? "";
        var isOpen = auth.Equals("open", StringComparison.OrdinalIgnoreCase);
        var isPersonal = isOpen || auth.EndsWith("PSK", StringComparison.OrdinalIgnoreCase)
            || auth.Equals("WPA3SAE", StringComparison.OrdinalIgnoreCase);
        var isProtected = Find("protected")?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true;
        var key = Find("keyMaterial")?.Value;
        var passphrase = !isOpen && !isProtected && !string.IsNullOrEmpty(key) ? key : null;
        return new WifiCredentials(ssid, passphrase, isOpen, isPersonal);
    }
}
