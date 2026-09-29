using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Settings;

namespace ZapretSmart.App;

public sealed record AppPaths(
    string EngineExe,
    EngineLayout Layout,
    string BundledListsDir,
    string PresetsDir,
    string UserStrategiesDir,
    string SettingsFile,
    string? HostsFile = null,
    bool ManageFirewall = false)
{
    /// <summary>Папка данных пользователя: настройки, резервная копия hosts, журнал обновления.</summary>
    public string DataDir => Path.GetDirectoryName(SettingsFile)!;

    public string BundledHostsSnapshot => Path.Combine(AppContext.BaseDirectory, "hosts", "flowseal.hosts");

    public static AppPaths Default
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var dataDir = Path.GetDirectoryName(SettingsStore.DefaultPath)!;
            return new AppPaths(
                EngineExe: Path.Combine(baseDir, "engine", "winws.exe"),
                Layout: new EngineLayout(
                    FakeDir: Path.Combine(baseDir, "engine", "fake"),
                    ListsDir: Path.Combine(dataDir, "lists"),
                    IpsetsDir: Path.Combine(dataDir, "ipsets")),
                BundledListsDir: Path.Combine(baseDir, "lists"),
                PresetsDir: Path.Combine(baseDir, "strategies"),
                UserStrategiesDir: Path.Combine(dataDir, "strategies"),
                SettingsFile: SettingsStore.DefaultPath,
                // Системный hosts правим только на Windows; в тестах путь не задан, и до системного файла они не дотянутся.
                HostsFile: OperatingSystem.IsWindows() ? Core.Hosts.HostsFile.DefaultPath : null,
                // Правило брандмауэра для раздачи ставим только в настоящем приложении, тесты брандмауэр не трогают.
                ManageFirewall: OperatingSystem.IsWindows());
        }
    }
}
