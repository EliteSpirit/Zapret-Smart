using System.Diagnostics;
using System.Text;
using ZapretSmart.Core.Updates;

namespace ZapretSmart.Core.Share;

public enum HotspotAction { Status, Start, Stop }

/// <summary>Состояние «Мобильного хот-спота» Windows. Error заполнен, если управлять им не вышло.</summary>
public sealed record HotspotState(bool IsOn, string? Ssid, string? Passphrase, int Clients, string? Error)
{
    public static HotspotState Failed(string error) => new(false, null, null, 0, error);
}

/// <summary>
/// «Мобильный хот-спот» Windows: та же точка доступа, что включается из панели быстрых настроек.
/// Управляется через WinRT (NetworkOperatorTetheringManager). Из .NET 8 без Windows-сборки WinRT недоступен,
/// поэтому вызываем его из Windows PowerShell 5.1, где проекция WinRT встроена.
/// </summary>
public static class WindowsHotspot
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    public static async Task<HotspotState> RunAsync(HotspotAction action, string workDir, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return HotspotState.Failed("точка доступа Windows есть только в Windows");
        Directory.CreateDirectory(workDir);
        var script = Path.Combine(workDir, "hotspot.ps1");
        await File.WriteAllTextAsync(script, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action.ToString().ToLowerInvariant() },
        };
        UpdateInstaller.IsolateFromPowerShell7(psi);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить powershell.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var state = Parse(await output);
            var err = (await errors).Trim();
            return state.Error is null && state.Ssid is null && err.Length > 0 ? HotspotState.Failed(FirstLine(err)) : state;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return HotspotState.Failed("Windows не ответила за " + (int)Timeout.TotalSeconds + " с");
        }
    }

    /// <summary>Вывод скрипта: строки «ключ: значение». Ошибка перекрывает остальное.</summary>
    public static HotspotState Parse(string output)
    {
        string? state = null, ssid = null, pass = null, error = null;
        var clients = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) continue;
            var value = line[(colon + 2)..];
            switch (line[..colon])
            {
                case "state": state = value.Trim(); break;
                case "ssid": ssid = value; break;
                case "passphrase": pass = value; break;
                case "clients": int.TryParse(value, out clients); break;
                case "error": error = value.Trim(); break;
            }
        }
        if (error is not null) return HotspotState.Failed(Explain(error));
        if (state is null) return HotspotState.Failed("Windows не сообщила состояние точки доступа");
        return new HotspotState(state is "On" or "InTransition", ssid, pass, clients, null);
    }

    private static string Explain(string error) => error switch
    {
        "NoInternet" => "нет подключения к интернету, раздавать нечего",
        "WiFiDeviceOff" => "Wi-Fi выключен",
        "EntitlementCheckFailure" or "EntitlementCheckTimeout" => "оператор связи не разрешает раздачу",
        "MobileBroadbandDeviceOff" => "модем выключен",
        "BluetoothDeviceOff" => "Bluetooth выключен",
        "OperationInProgress" => "Windows уже включает или выключает точку доступа, попробуйте через пару секунд",
        "TechnologyNotAvailable" => "в этом компьютере нет Wi-Fi, который умеет раздавать",
        _ => error,
    };

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    /// <summary>
    /// Скрипт выводит только ключи и значения; понятные тексты ошибок делает приложение.
    /// Wait(-1) на задаче WinRT: у Windows PowerShell нет await, а без ожидания результат потерялся бы.
    /// </summary>
    public const string Script = """
        param([ValidateSet('status','start','stop')][string]$Action = 'status')
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        try {
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
            } | Select-Object -First 1
            function Await($op, [Type]$type) {
                $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
                [void]$task.Wait(-1)
                $task.Result
            }
            [void][Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]
            [void][Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]
            $connection = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
            if ($null -eq $connection) { 'error: NoInternet'; exit 0 }
            $manager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($connection)
            $resultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]
            if ($Action -eq 'start' -and $manager.TetheringOperationalState -ne 'On') {
                $r = Await ($manager.StartTetheringAsync()) $resultType
                if ($r.Status -ne 'Success') { "error: $($r.Status)"; exit 0 }
            }
            if ($Action -eq 'stop' -and $manager.TetheringOperationalState -ne 'Off') {
                $r = Await ($manager.StopTetheringAsync()) $resultType
                if ($r.Status -ne 'Success') { "error: $($r.Status)"; exit 0 }
            }
            $config = $manager.GetCurrentAccessPointConfiguration()
            "state: $($manager.TetheringOperationalState)"
            "ssid: $($config.Ssid)"
            "passphrase: $($config.Passphrase)"
            "clients: $($manager.ClientCount)"
        } catch {
            $e = $_.Exception
            while ($null -ne $e.InnerException) { $e = $e.InnerException }
            "error: $($e.Message -replace '[\r\n]+', ' ')"
        }
        """;
}

/// <summary>
/// Правило брандмауэра Windows для прокси: входящие на порт прокси только из локальной подсети и только
/// для ZapretSmart.exe. Без него телефон не достучится до ПК, если сеть в Windows помечена как общедоступная.
/// </summary>
public static class ShareFirewall
{
    public const string RuleName = "Zapret Smart share proxy";

    public static IReadOnlyList<string> AddArguments(string program, int port) =>
    [
        "advfirewall", "firewall", "add", "rule", $"name={RuleName}", "dir=in", "action=allow", "protocol=TCP",
        $"localport={port}", "remoteip=localsubnet", $"program={program}", "enable=yes", "profile=any",
    ];

    public static IReadOnlyList<string> DeleteArguments => ["advfirewall", "firewall", "delete", "rule", $"name={RuleName}"];

    /// <summary>Ставит правило заново: старое (со старым портом или путём) удаляется. Возвращает текст ошибки или null.</summary>
    public static async Task<string?> AllowAsync(string program, int port, CancellationToken ct)
    {
        await RunAsync(DeleteArguments, ct);
        var (code, output) = await RunAsync(AddArguments(program, port), ct);
        return code == 0 ? null : "не удалось открыть порт в брандмауэре: " + output.Trim();
    }

    public static async Task RemoveAsync(CancellationToken ct) => await RunAsync(DeleteArguments, ct);

    private static async Task<(int Code, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return (0, "");
        var psi = new ProcessStartInfo("netsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output);
    }
}
