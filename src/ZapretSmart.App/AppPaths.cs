using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Settings;

namespace ZapretSmart.App;

public sealed record AppPaths(
    string EngineExe,
    EngineLayout Layout,
    string PresetsDir,
    string UserStrategiesDir,
    string SettingsFile)
{
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
                    ListsDir: Path.Combine(baseDir, "lists")),
                PresetsDir: Path.Combine(baseDir, "strategies"),
                UserStrategiesDir: Path.Combine(dataDir, "strategies"),
                SettingsFile: SettingsStore.DefaultPath);
        }
    }
}
