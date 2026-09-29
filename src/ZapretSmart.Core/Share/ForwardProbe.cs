using System.Net;
using System.Runtime.InteropServices;

namespace ZapretSmart.Core.Share;

/// <summary>Что увидела проверка: пакеты на слое пересылки и входящие пакеты для чужих адресов.</summary>
public sealed record ForwardProbeResult(int ForwardPackets, int InboundPackets, IReadOnlyList<string> Samples, string? Error)
{
    public static ForwardProbeResult Failed(string error) => new(0, 0, [], error);
}

/// <summary>
/// Проверка для обычной раздачи (без прокси): видит ли WinDivert трафик устройств, подключённых к точке доступа ПК.
/// Движок сейчас ловит только трафик самого ПК (слой NETWORK). Трафик телефона Windows пересылает, и вопрос в том,
/// проходит ли он через слой NETWORK_FORWARD и в каком виде. Ответ зависит от того, как Windows раздаёт интернет,
/// и выясняется только на живом ПК с Wi-Fi и телефоном.
///
/// Только чтение: флаг SNIFF. WinDivert отдаёт копии пакетов, сами пакеты идут дальше без задержки и без изменений,
/// поэтому проверка не мешает ни обходу, ни интернету.
/// </summary>
public static class ForwardProbe
{
    private const int LayerNetwork = 0;
    private const int LayerForward = 1;
    private const ulong FlagSniff = 0x0001;
    private const ulong FlagRecvOnly = 0x0004;
    private const int ShutdownBoth = 3;
    private const int AddressSize = 80;
    public const int MaxSamples = 8;

    /// <summary>Пересылаемые пакеты к веб-портам: на обычном ПК их нет, пока не включена раздача или виртуальная машина.</summary>
    public const string ForwardFilter = "ip and (tcp.DstPort == 443 or tcp.DstPort == 80 or udp.DstPort == 443)";

    /// <summary>Входящие на веб-порты чужих адресов: так выглядел бы трафик телефона, если Windows отдаёт его на слой NETWORK.</summary>
    public const string InboundFilter = "inbound and !loopback and ip and (tcp.DstPort == 443 or tcp.DstPort == 80)";

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate IntPtr OpenFn([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool RecvFn(IntPtr handle, byte[] packet, uint packetLen, out uint recvLen, byte[] address);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ShutdownFn(IntPtr handle, int how);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool CloseFn(IntPtr handle);

    public static async Task<ForwardProbeResult> RunAsync(string winDivertDll, TimeSpan duration, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return ForwardProbeResult.Failed("проверка работает только в Windows");
        if (!File.Exists(winDivertDll)) return ForwardProbeResult.Failed("не найден " + winDivertDll);

        IntPtr lib;
        try
        {
            lib = NativeLibrary.Load(winDivertDll);
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            return ForwardProbeResult.Failed("не удалось загрузить WinDivert: " + e.Message);
        }
        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<OpenFn>(NativeLibrary.GetExport(lib, "WinDivertOpen"));
            var recv = Marshal.GetDelegateForFunctionPointer<RecvFn>(NativeLibrary.GetExport(lib, "WinDivertRecv"));
            var shutdown = Marshal.GetDelegateForFunctionPointer<ShutdownFn>(NativeLibrary.GetExport(lib, "WinDivertShutdown"));
            var close = Marshal.GetDelegateForFunctionPointer<CloseFn>(NativeLibrary.GetExport(lib, "WinDivertClose"));

            var forward = open(ForwardFilter, LayerForward, 0, FlagSniff | FlagRecvOnly);
            if (forward == new IntPtr(-1)) return ForwardProbeResult.Failed("WinDivert не открыл слой пересылки: код " + Marshal.GetLastWin32Error());
            var inbound = open(InboundFilter, LayerNetwork, 0, FlagSniff | FlagRecvOnly);
            if (inbound == new IntPtr(-1))
            {
                close(forward);
                return ForwardProbeResult.Failed("WinDivert не открыл входящий трафик: код " + Marshal.GetLastWin32Error());
            }

            var local = LocalIPv4();
            var samples = new List<string>();
            var forwardCount = Listen(forward, recv, "пересылка", samples, _ => true);
            var inboundCount = Listen(inbound, recv, "входящий", samples, dst => !local.Contains(dst));
            try
            {
                await Task.Delay(duration, ct);
            }
            finally
            {
                // После shutdown WinDivertRecv возвращает ошибку, и потоки чтения заканчиваются.
                shutdown(forward, ShutdownBoth);
                shutdown(inbound, ShutdownBoth);
                await Task.WhenAll(forwardCount, inboundCount);
                close(forward);
                close(inbound);
            }
            List<string> copy;
            lock (samples) copy = [.. samples];
            return new ForwardProbeResult(forwardCount.Result, inboundCount.Result, copy, null);
        }
        catch (EntryPointNotFoundException e)
        {
            return ForwardProbeResult.Failed("в WinDivert.dll нет нужной функции: " + e.Message);
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }

    private static Task<int> Listen(IntPtr handle, RecvFn recv, string kind, List<string> samples, Func<IPAddress, bool> counts) =>
        Task.Factory.StartNew(() =>
        {
            var packet = new byte[65535];
            var address = new byte[AddressSize];
            var n = 0;
            while (recv(handle, packet, (uint)packet.Length, out var len, address))
            {
                if (Describe(packet.AsSpan(0, (int)len)) is not { } d || !counts(d.Destination)) continue;
                n++;
                lock (samples)
                {
                    if (samples.Count < MaxSamples) samples.Add($"{kind}: {d.Text}");
                }
            }
            return n;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>«192.168.137.23 → 142.250.74.14:443 tcp ttl 64» для IPv4-пакета; null для остального.</summary>
    public static (IPAddress Destination, string Text)? Describe(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 20 || packet[0] >> 4 != 4) return null;
        var ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl + 4) return null;
        var proto = packet[9] switch { 6 => "tcp", 17 => "udp", _ => null };
        if (proto is null) return null;
        var src = new IPAddress(packet.Slice(12, 4));
        var dst = new IPAddress(packet.Slice(16, 4));
        var port = (packet[ihl + 2] << 8) | packet[ihl + 3];
        return (dst, $"{src} → {dst}:{port} {proto} ttl {packet[8]}");
    }

    private static HashSet<IPAddress> LocalIPv4()
    {
        var set = new HashSet<IPAddress>();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) set.Add(ua.Address);
            }
            catch (System.Net.NetworkInformation.NetworkInformationException)
            {
            }
        }
        return set;
    }

    /// <summary>Вывод для человека: что это значит для обычной раздачи.</summary>
    public static string Explain(ForwardProbeResult r) => r switch
    {
        { Error: { } e } => "Проверка не удалась: " + e + ".",
        { ForwardPackets: > 0 } => $"Трафик телефона виден на слое пересылки ({r.ForwardPackets} пакетов). Обычную раздачу без прокси можно доделать: скопируйте журнал и пришлите разработчику.",
        { InboundPackets: > 0 } => $"Трафик телефона виден как входящий ({r.InboundPackets} пакетов), но не на слое пересылки. Скопируйте журнал и пришлите разработчику: это другой вариант доработки.",
        _ => "Трафика телефона не видно. Если телефон был подключён к точке доступа ПК и открывал сайты, значит обычная раздача с этим движком не заработает, остаётся прокси.",
    };
}
