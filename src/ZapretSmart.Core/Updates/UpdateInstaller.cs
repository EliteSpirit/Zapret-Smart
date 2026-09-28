using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ZapretSmart.Core.Updates;

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Скачивает выбранную версию, сверяет SHA-256 с SHA256SUMS.txt того же релиза и готовит замену файлов.
/// Файлы меняет не само приложение (свой exe запущенным не перезапишешь), а скрипт PowerShell после его выхода.
/// </summary>
public sealed class UpdateInstaller(HttpClient http, string workRoot)
{
    /// <summary>Скачивает и распаковывает сборку. Возвращает папку с файлами новой версии.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<string>? progress, CancellationToken ct)
    {
        var work = Path.Combine(workRoot, "update-" + release.Version);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        progress?.Report("Скачиваю контрольные суммы");
        var expected = ExpectedHash(await http.GetStringAsync(release.SumsUrl, ct), release.ZipName)
            ?? throw new UpdateException($"в SHA256SUMS.txt нет строки для {release.ZipName}");

        progress?.Report($"Скачиваю {release.ZipName}");
        var zip = Path.Combine(work, release.ZipName);
        string actual;
        using (var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(zip);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        if (actual != expected)
        {
            File.Delete(zip);
            throw new UpdateException($"контрольная сумма архива не совпала: ожидалась {expected}, получена {actual}. Архив удалён, ничего не установлено");
        }

        progress?.Report("Распаковываю");
        var files = Path.Combine(work, "files");
        // ExtractToDirectory отказывается от записей, ведущих за пределы папки (../), так что архив не выпишет файлы куда попало.
        ZipFile.ExtractToDirectory(zip, files);
        if (!File.Exists(Path.Combine(files, "ZapretSmart.exe")))
            throw new UpdateException("в архиве нет ZapretSmart.exe");
        return files;
    }

    /// <summary>Строка «hash  имя» из SHA256SUMS.txt.</summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Пишет скрипт замены и запускает его. Скрипт ждёт выхода приложения, заменяет только изменившиеся файлы,
    /// при любой ошибке возвращает прежние и запускает приложение снова.
    /// </summary>
    public Process StartApply(string newFiles, string installDir, string log)
    {
        var script = Path.Combine(workRoot, "apply-update.ps1");
        File.WriteAllText(script, ApplyScript, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                "-ProcessId", Environment.ProcessId.ToString(),
                "-Source", newFiles, "-Target", installDir, "-Log", log,
            },
        };
        return Process.Start(psi) ?? throw new UpdateException("не удалось запустить установку");
    }

    /// <summary>
    /// Замена файлов. Резервная копия каждого заменяемого файла; новые файлы запоминаются, чтобы при откате их убрать.
    /// Одинаковые по хэшу файлы не трогаются: так не приходится перезаписывать WinDivert64.sys, пока драйвер загружен.
    /// </summary>
    public const string ApplyScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Log, [int]$Retries = 20, [switch]$NoStart)
        $ErrorActionPreference = 'Stop'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Log) | Out-Null
        function Write-Log([string]$Message) {
            Add-Content -LiteralPath $Log -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' ' + $Message) -Encoding UTF8
        }
        try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch { }
        $Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\', '/')
        $backup = Join-Path (Split-Path -Parent $Source) 'backup'
        $replaced = New-Object System.Collections.ArrayList
        $created = New-Object System.Collections.ArrayList
        try {
            $plan = New-Object System.Collections.ArrayList
            foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
                $rel = $file.FullName.Substring($Source.Length).TrimStart('\', '/')
                $dst = Join-Path $Target $rel
                if ((Test-Path -LiteralPath $dst) -and ((Get-FileHash -LiteralPath $dst).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash)) { continue }
                [void]$plan.Add(@($file.FullName, $dst, $rel))
            }
            foreach ($item in $plan) {
                $src = $item[0]; $dst = $item[1]; $rel = $item[2]
                if (Test-Path -LiteralPath $dst) {
                    $copy = Join-Path $backup $rel
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $copy) | Out-Null
                    Copy-Item -LiteralPath $dst -Destination $copy -Force
                    [void]$replaced.Add(@($copy, $dst))
                } else {
                    [void]$created.Add($dst)
                }
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
                $done = $false
                for ($i = 0; $i -lt $Retries -and -not $done; $i++) {
                    try { Copy-Item -LiteralPath $src -Destination $dst -Force; $done = $true } catch { Start-Sleep -Milliseconds 500 }
                }
                if (-not $done) { throw "не удалось заменить $rel (файл занят?)" }
            }
            Write-Log "ok: заменено файлов $($plan.Count)"
        } catch {
            Write-Log "ошибка: $($_.Exception.Message). Возвращаю прежние файлы."
            foreach ($item in $replaced) { try { Copy-Item -LiteralPath $item[0] -Destination $item[1] -Force } catch { Write-Log "не удалось вернуть $($item[1])" } }
            foreach ($path in $created) { try { Remove-Item -LiteralPath $path -Force } catch { } }
        }
        if (-not $NoStart) { Start-Process -FilePath (Join-Path $Target 'ZapretSmart.exe') }
        """;
}
