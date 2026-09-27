using ZapretSmart.Core.Settings;

namespace ZapretSmart.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zs-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CommunityIsOffByDefault()
    {
        Assert.False(new SettingsStore(Path.Combine(_dir, "settings.json")).Load().CommunityEnabled);
    }

    [Fact]
    public void RoundTrips()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { CommunityEnabled = true, SelectedStrategyId = "x" });
        Assert.Equal(new AppSettings { CommunityEnabled = true, SelectedStrategyId = "x" }, store.Load());
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(new AppSettings(), new SettingsStore(path).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
