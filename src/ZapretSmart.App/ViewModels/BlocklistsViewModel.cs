using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Lists;

namespace ZapretSmart.App.ViewModels;

public sealed partial class SubscriptionStatus(ListSubscription subscription) : ObservableObject
{
    public ListSubscription Subscription { get; } = subscription;
    public string Title => Subscription.Title;

    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string? _error;
}

public sealed partial class BlocklistsViewModel : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>1 домен, 2 домена, 5 доменов, 11 доменов, 21 домен.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var word = (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
        return $"{n.ToString("N0", Ru)} {word}";
    }

    private static string Records(int n) => Plural(n, "запись", "записи", "записей");

    private readonly MainWindowViewModel _main;
    private readonly ListUpdater _updater;

    public BlocklistsViewModel(MainWindowViewModel main, ListUpdater updater)
    {
        _main = main;
        _updater = updater;
        foreach (var s in Subscriptions.All) Items.Add(new SubscriptionStatus(s));
        RefreshInfo();
        RefreshAuto();
    }

    public ObservableCollection<SubscriptionStatus> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private bool _isUpdating;

    [ObservableProperty] private string _autoInfo = "";

    private bool CanUpdate() => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateNow() => UpdateAsync(force: true);

    /// <summary>Фоновое обновление при запуске: только отсутствующие и старше суток.</summary>
    public Task UpdateStaleAsync() => UpdateAsync(force: false);

    private async Task UpdateAsync(bool force)
    {
        if (IsUpdating) return;
        var due = Items.Where(i => force || _updater.IsStale(i.Subscription, DateTime.UtcNow)).ToList();
        if (due.Count == 0) return;

        IsUpdating = true;
        try
        {
            foreach (var item in due)
            {
                item.Info = "обновляется…";
                var r = await Task.Run(() => _updater.UpdateAsync(item.Subscription, CancellationToken.None));
                item.Error = r.Error is null ? null : r.Error + (r.Notes.Count > 0 ? ". " + string.Join("; ", r.Notes) : "");
                _main.AppendLog(r.Updated
                    ? $"Списки: «{item.Title}» обновлён, {Records(r.Entries)} ({string.Join("; ", r.Notes)})"
                    : $"! Списки: «{item.Title}» не обновлён: {item.Error}");
            }
        }
        finally
        {
            IsUpdating = false;
            RefreshInfo();
        }
    }

    [RelayCommand]
    private void ClearAuto()
    {
        _main.ListStore.ClearAutoList();
        RefreshAuto();
    }

    [RelayCommand]
    private void RefreshAuto()
    {
        var n = _main.ListStore.AutoListCount();
        AutoInfo = n == 0 ? "Автосписок пуст" : "В автосписке " + Plural(n, "домен", "домена", "доменов");
    }

    private void RefreshInfo()
    {
        foreach (var item in Items)
        {
            var at = _updater.LastUpdatedUtc(item.Subscription);
            item.Info = at is null
                ? "ещё не загружен"
                : $"{Records(_updater.CountEntries(item.Subscription))} · обновлён {at.Value.ToLocalTime():dd.MM HH:mm}";
        }
    }
}
