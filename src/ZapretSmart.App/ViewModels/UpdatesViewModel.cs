using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Updates;

namespace ZapretSmart.App.ViewModels;

public sealed record ReleaseOption(ReleaseInfo Release, bool IsCurrent)
{
    public string Title =>
        Release.Version + (Release.PublishedAt == DateTimeOffset.MinValue ? "" : $" · {Release.PublishedAt.ToLocalTime():dd.MM.yyyy}")
        + (Release.IsPrerelease ? " · пререлиз" : "") + (IsCurrent ? " · установлена" : "");
}

/// <summary>
/// Выбор версии приложения из релизов на GitHub и её установка: обновление или откат.
/// Список загружается только по кнопке: сам по себе приложение на GitHub не ходит.
/// </summary>
public sealed partial class UpdatesViewModel(MainWindowViewModel main, HttpClient http) : ObservableObject
{
    public ObservableCollection<ReleaseOption> Releases { get; } = [];

    public string Current => MainWindowViewModel.CurrentVersion;

    /// <summary>Установка возможна только из собранной папки релиза на Windows: рядом с нами должен лежать ZapretSmart.exe.</summary>
    public bool CanInstallHere { get; } = OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "ZapretSmart.exe"));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(CanInstallNow))]
    private ReleaseOption? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(CanInstallNow))]
    private bool _isBusy;

    /// <summary>Кнопка «Установить» открывает подтверждение, поэтому её доступность нужна отдельным свойством.</summary>
    public bool CanInstallNow => CanInstall();

    [ObservableProperty] private string _status = "Нажмите «Проверить», чтобы загрузить список версий.";

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task Check()
    {
        IsBusy = true;
        Status = "Загружаю список версий";
        try
        {
            ShowReleases(await Core.Updates.Releases.FetchAsync(http, CancellationToken.None));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status = "Не удалось загрузить список версий: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ShowReleases(IReadOnlyList<ReleaseInfo> releases)
    {
        AppVersion.TryParse(Current, out var current);
        Releases.Clear();
        foreach (var r in releases) Releases.Add(new ReleaseOption(r, r.Version.CompareTo(current) == 0));
        var newest = Releases.FirstOrDefault();
        Selected = newest;
        Status = newest is null ? "Релизов с архивом для Windows не нашлось."
            : newest.IsCurrent ? $"Установлена последняя версия, {Current}."
            : newest.Release.Version.CompareTo(current) > 0 ? $"Есть версия новее: {newest.Release.Version}."
            : "Выберите версию.";
    }

    private bool CanInstall() => !IsBusy && CanInstallHere && Selected is { IsCurrent: false };

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        var release = Selected!.Release;
        IsBusy = true;
        try
        {
            var installer = new UpdateInstaller(http, Path.GetTempPath());
            var files = await installer.DownloadAsync(release, new Progress<string>(p => Status = p), CancellationToken.None);
            Status = "Выключаю обход и перезапускаюсь";
            main.AppendLog($"Обновление: устанавливаю {release.Version}, приложение перезапустится");
            await main.StopEngineAsync();
            installer.StartApply(files, AppContext.BaseDirectory, Path.Combine(main.Paths.DataDir, "update.log"));
            main.RequestExitForUpdate();
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = "Не установлено: " + e.Message;
            main.AppendLog("! Обновление: " + Status);
            IsBusy = false;
        }
    }
}
