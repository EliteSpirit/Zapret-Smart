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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action.ToString().ToLowerInvariant() },
        };
        UpdateInstaller.IsolateFromPowerShell7(psi);
        var result = await ProcessRunner.RunAsync(psi, Timeout, ct);
        if (result.StartError is { } startError) return HotspotState.Failed(startError);
        if (result.TimedOut) return HotspotState.Failed("Windows не ответила за " + (int)Timeout.TotalSeconds + " с");
        var state = Parse(result.Output);
        var err = result.Errors.Trim();
        return state.Error is null && state.Ssid is null && err.Length > 0 ? HotspotState.Failed(FirstLine(err)) : state;
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

/// <summary>Итог установки правила. RemovedProgramRules — сколько прежних входящих правил для программы снято.</summary>
public sealed record FirewallResult(string? Error, int RemovedProgramRules);

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

    /// <summary>
    /// Все входящие правила для программы, в том числе запрещающие. Windows создаёт их сама, если на её вопрос
    /// «разрешить доступ?» ответили «Отмена» или окно закрылось, а запрет в брандмауэре сильнее любого разрешения:
    /// с ним телефон получает таймаут, хотя наше правило стоит.
    /// </summary>
    public static IReadOnlyList<string> DeleteProgramArguments(string program) =>
        ["advfirewall", "firewall", "delete", "rule", "name=all", "dir=in", $"program={program}"];

    /// <summary>
    /// Ставит правило заново: старое (со старым портом или путём) и чужие входящие правила для этой программы
    /// удаляются. Вызывать до открытия порта, иначе Windows успеет показать свой вопрос.
    /// </summary>
    public static async Task<FirewallResult> AllowAsync(string program, int port, CancellationToken ct)
    {
        await RunAsync(DeleteArguments, ct);
        var (deleted, deleteOutput) = await RunAsync(DeleteProgramArguments(program), ct);
        var removed = deleted == 0 ? CountDeleted(deleteOutput) : 0;
        var (code, output) = await RunAsync(AddArguments(program, port), ct);
        return new FirewallResult(code == 0 ? null : "не удалось открыть порт в брандмауэре: " + output.Trim(), removed);
    }

    /// <summary>
    /// Число удалённых правил из ответа netsh («Deleted 2 rule(s).», «Удалено правил: 2.»). Код 0 без числа
    /// значит, что удалено хотя бы одно.
    /// </summary>
    public static int CountDeleted(string output)
    {
        var digits = new string(output.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 1;
    }

    public static async Task RemoveAsync(CancellationToken ct) => await RunAsync(DeleteArguments, ct);

    /// <summary>Сколько ждать netsh. Дольше он не работает; зависший netsh не должен держать раздачу выключенной.</summary>
    public static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(20);

    /// <summary>netsh через ProcessRunner: не бросает и не виснет. Если не запустился или не ответил, код -1 и причина.</summary>
    private static async Task<(int Code, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return (0, "");
        var psi = new ProcessStartInfo("netsh")
        {
            StandardOutputEncoding = ProcessRunner.ConsoleEncoding,
            StandardErrorEncoding = ProcessRunner.ConsoleEncoding,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var result = await ProcessRunner.RunAsync(psi, NetshTimeout, ct);
        if (result.StartError is { } startError) return (-1, startError);
        if (result.TimedOut) return (-1, $"netsh не ответил за {NetshTimeout.TotalSeconds:0} с");
        return (result.ExitCode, result.Output + result.Errors);
    }
}
