using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Hosts;

namespace ZapretSmart.App.ViewModels;

/// <summary>
/// Блок Zapret Smart в системном файле hosts. Включение сразу ставит записи, выключение убирает блок.
/// Записи пользователя вне блока не трогаются; копия файла перед каждым изменением лежит в папке данных.
/// </summary>
public sealed partial class HostsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly HostsManager? _manager;
    private bool _loading;

    public HostsViewModel(MainWindowViewModel main, HostsManager? manager, bool enabled)
    {
        _main = main;
        _manager = manager;
        _loading = true;
        IsEnabled = enabled && manager is not null;
        _loading = false;
        RefreshEntries();
        Status = manager is null ? "Файл hosts правится только на Windows."
            : Entries.Count > 0 ? $"В hosts стоит {BlocklistsViewModel.Plural(Entries.Count, "запись", "записи", "записей")} приложения."
            : "Записей приложения в hosts нет.";
    }

    public bool IsAvailable => _manager is not null;
    public string BackupPath => _manager?.BackupPath ?? "";
    public ObservableCollection<string> Entries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "";

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loading) return;
        _main.SaveHostsEnabled(value);
        _ = value ? UpdateAsync() : RemoveAsync();
    }

    private bool CanUpdate() => IsAvailable && IsEnabled && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateNow() => UpdateAsync();

    /// <summary>Фоновое обновление вместе со списками блокировок: только если пользователь сам включил hosts.</summary>
    public Task RefreshIfEnabledAsync() => IsEnabled && !IsBusy ? UpdateAsync() : Task.CompletedTask;

    public async Task UpdateAsync()
    {
        if (_manager is null) return;
        IsBusy = true;
        Status = "Загружаю список";
        try
        {
            var loaded = await _manager.LoadSourceAsync(CancellationToken.None);
            if (!IsEnabled) return;
            var plan = _manager.Apply(loaded.Source.Entries);
            var parts = new List<string>
            {
                $"В hosts {BlocklistsViewModel.Plural(plan.Applied.Count, "запись", "записи", "записей")} ({loaded.Origin}, {DateTime.Now:dd.MM HH:mm})."
                    + (plan.Changed ? "" : " Файл не менялся."),
            };
            if (plan.ShadowedByUser.Count > 0)
                parts.Add($"Ваши записи главнее, их не трогаем: {string.Join(", ", plan.ShadowedByUser.Distinct().Take(5))}{(plan.ShadowedByUser.Count > 5 ? "…" : "")}.");
            if (loaded.Source.Rejected.Count > 0)
                parts.Add($"Отброшено строк источника: {loaded.Source.Rejected.Count}.");
            Status = string.Join(' ', parts);
            if (plan.Changed) _main.AppendLog($"hosts: {plan.Applied.Count} записей приложения ({loaded.Origin}), копия прежнего файла: {_manager.BackupPath}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = Explain(e);
            _main.AppendLog("! hosts: " + Status);
        }
        finally
        {
            RefreshEntries();
            IsBusy = false;
        }
    }

    private async Task RemoveAsync()
    {
        if (_manager is null) return;
        IsBusy = true;
        try
        {
            await Task.Yield();
            var plan = _manager.Apply([]);
            Status = plan.Changed ? "Записи приложения убраны из hosts, ваши строки остались как были." : "Записей приложения в hosts нет.";
            if (plan.Changed) _main.AppendLog("hosts: блок приложения убран");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = Explain(e);
            _main.AppendLog("! hosts: " + Status);
        }
        finally
        {
            RefreshEntries();
            IsBusy = false;
        }
    }

    private static string Explain(Exception e) => e is UnauthorizedAccessException
        ? "нет доступа к файлу hosts. Приложение запущено от администратора? Не стоит ли на файле «только чтение» и не блокирует ли его антивирус?"
        : "не удалось изменить hosts: " + e.Message;

    private void RefreshEntries()
    {
        Entries.Clear();
        if (_manager is null) return;
        try
        {
            foreach (var e in _manager.CurrentBlock()) Entries.Add(e.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
