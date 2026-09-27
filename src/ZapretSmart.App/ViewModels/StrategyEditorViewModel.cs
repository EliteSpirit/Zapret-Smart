using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Lists;
using ZapretSmart.Core.Storage;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.App.ViewModels;

public sealed partial class ListToggle(string id, string title, bool isChecked, Action changed) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;

    [ObservableProperty] private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>Метка списка в режиме просмотра. Accent — список обновляется сам (подписка или автосписок).</summary>
public sealed record ProfileChip(string Text, bool IsAccent);

public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly Action _changed;

    public ProfileEditorViewModel(StrategyEditorViewModel owner, Profile? profile)
    {
        Owner = owner;
        _changed = () =>
        {
            OnPropertyChanged(nameof(ScopeHint));
            OnPropertyChanged(nameof(FilterSummary));
            owner.Revalidate();
        };
        var hostlists = profile?.Hostlists ?? ["general", Subscriptions.BlockedDomains.Id];
        var ipsets = profile?.Ipsets ?? [];
        foreach (var id in owner.HostlistIds.Union(hostlists))
            HostToggles.Add(new ListToggle(id, Title(id), hostlists.Contains(id), _changed));
        foreach (var id in owner.IpsetIds.Union(ipsets))
            IpToggles.Add(new ListToggle(id, Title(id), ipsets.Contains(id), _changed));
        _autoHostlist = profile?.AutoHostlist ?? true;
        _argsText = profile is null ? "filter-tcp=443\ndpi-desync=multisplit\ndpi-desync-split-pos=1,midsld" : string.Join('\n', profile.Args);
    }

    private static string Title(string id) => Subscriptions.IsSubscription(id) ? id + " (обновляется сам)" : id;

    public StrategyEditorViewModel Owner { get; }

    /// <summary>Номер профиля, как в сообщениях проверки («Профиль 2, строка 3»).</summary>
    [ObservableProperty] private int _number = 1;

    /// <summary>«TCP 80 · UDP 443» из filter-tcp/filter-udp. Пусто, если профиль ловит весь перехватываемый трафик.</summary>
    public string FilterSummary
    {
        get
        {
            var parts = new List<string>();
            foreach (var line in Args)
            {
                if (line.StartsWith("filter-tcp=", StringComparison.Ordinal)) parts.Add("TCP " + line["filter-tcp=".Length..]);
                else if (line.StartsWith("filter-udp=", StringComparison.Ordinal)) parts.Add("UDP " + line["filter-udp=".Length..]);
            }
            return string.Join(" · ", parts);
        }
    }

    public IReadOnlyList<ProfileChip> Chips =>
        HostToggles.Where(t => t.IsChecked).Select(t => new ProfileChip(t.Id, Subscriptions.IsSubscription(t.Id)))
            .Concat(AutoHostlist ? [new ProfileChip("автосписок", true)] : [])
            .Concat(IpToggles.Where(t => t.IsChecked).Select(t => new ProfileChip(t.Id, Subscriptions.IsSubscription(t.Id))))
            .DefaultIfEmpty(new ProfileChip("без списков: весь трафик на своих портах", false))
            .ToList();

    public static IReadOnlyList<string> OptionNames { get; } = EngineOptionCatalog.Names.Order(StringComparer.Ordinal).ToList();

    public ObservableCollection<ListToggle> HostToggles { get; } = [];
    public ObservableCollection<ListToggle> IpToggles { get; } = [];

    [ObservableProperty]
    private bool _autoHostlist;

    [ObservableProperty]
    private string _argsText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddOptionCommand))]
    private string? _selectedOption;

    partial void OnAutoHostlistChanged(bool value) => _changed();
    partial void OnArgsTextChanged(string value) => _changed();

    public string ScopeHint =>
        HostToggles.Any(t => t.IsChecked) || IpToggles.Any(t => t.IsChecked) || AutoHostlist
            ? ""
            : "Без списков профиль применяется ко всему трафику на своих портах.";

    public IReadOnlyList<string> Args =>
        ArgsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public Profile ToProfile() => new()
    {
        Hostlists = HostToggles.Where(t => t.IsChecked).Select(t => t.Id).ToList(),
        Ipsets = IpToggles.Where(t => t.IsChecked).Select(t => t.Id).ToList(),
        AutoHostlist = AutoHostlist,
        Args = Args,
    };

    private bool CanAddOption() => SelectedOption is not null && EngineOptionCatalog.TryGet(SelectedOption.Trim(), out _);

    [RelayCommand(CanExecute = nameof(CanAddOption))]
    private void AddOption()
    {
        EngineOptionCatalog.TryGet(SelectedOption!.Trim(), out var option);
        var line = option.IsValid(null) ? option.Name : option.Name + "=";
        ArgsText = ArgsText.TrimEnd('\n', ' ') + (ArgsText.Length > 0 ? "\n" : "") + line;
        SelectedOption = null;
    }
}

public sealed partial class StrategyEditorViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private bool _loading;

    public StrategyEditorViewModel(MainWindowViewModel main)
    {
        _main = main;
        ReloadListIds();
        Profiles.CollectionChanged += (_, _) =>
        {
            for (var i = 0; i < Profiles.Count; i++) Profiles[i].Number = i + 1;
        };
    }

    public IReadOnlyList<string> HostlistIds { get; private set; } = [];
    public IReadOnlyList<string> IpsetIds { get; private set; } = [];
    public ObservableCollection<ProfileEditorViewModel> Profiles { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    public ObservableCollection<StrategyItem> Items => _main.Strategies;
    public ObservableCollection<string> LoadErrors => _main.LoadErrors;

    [ObservableProperty]
    private StrategyItem? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    [NotifyPropertyChangedFor(nameof(SaveStatus))]
    private string? _editingId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    private bool _isReadOnly;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsSaved))]
    private bool _isValid;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsSaved))]
    [NotifyPropertyChangedFor(nameof(SaveStatus))]
    private bool _isDirty;

    /// <summary>Кнопка «Удалить» открывает подтверждение, поэтому её доступность нужна отдельным свойством.</summary>
    public bool CanDeleteSelected => CanDelete();

    public bool IsSaved => IsValid && !IsDirty;

    public string SaveStatus =>
        !IsDirty ? "Сохранено"
        : EditingId is null ? "Новая стратегия ещё не сохранена"
        : "Есть несохранённые изменения";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _tcp = "";
    [ObservableProperty] private string _udp = "";
    [ObservableProperty] private string _notice = "";

    partial void OnNameChanged(string value) => Revalidate();
    partial void OnDescriptionChanged(string value) => Revalidate();
    partial void OnTcpChanged(string value) => Revalidate();
    partial void OnUdpChanged(string value) => Revalidate();

    private bool _silentSelection;

    private void SelectSilently(StrategyItem? item)
    {
        _silentSelection = true;
        SelectedItem = item;
        _silentSelection = false;
    }

    partial void OnSelectedItemChanged(StrategyItem? value)
    {
        if (!_silentSelection && value is not null && value.Strategy.Id != EditingId) Load(value);
    }

    public void SyncWith(IEnumerable<StrategyItem> items)
    {
        ReloadListIds();
        var match = items.FirstOrDefault(i => i.Strategy.Id == EditingId);
        if (match is not null)
            SelectSilently(match);
        else if (!(EditingId is null && IsDirty))
            // Несохранённый черновик («Новая»/«Копия») не выбрасываем, когда список перечитывается извне.
            SelectedItem = items.FirstOrDefault();
    }

    private void ReloadListIds()
    {
        HostlistIds = _main.ListStore.HostlistIds();
        IpsetIds = _main.ListStore.IpsetIds();
    }

    private void Load(StrategyItem item)
    {
        _loading = true;
        var s = item.Strategy;
        EditingId = s.Id;
        IsReadOnly = item.IsPreset;
        Name = s.Name;
        Description = s.Description ?? "";
        Tcp = s.Intercept.Tcp ?? "";
        Udp = s.Intercept.Udp ?? "";
        Profiles.Clear();
        foreach (var p in s.Profiles) Profiles.Add(new ProfileEditorViewModel(this, p));
        Notice = item.IsPreset ? "Встроенную стратегию менять нельзя. Нажмите «Копия», чтобы сделать свою на её основе." : "";
        _loading = false;
        Revalidate();
        IsDirty = false;
    }

    public void Revalidate()
    {
        if (_loading) return;
        IsDirty = true;
        Errors.Clear();
        foreach (var e in StrategyValidator.Validate(Build(EditingId ?? "draft"))) Errors.Add(e);
        IsValid = Errors.Count == 0;
    }

    private Strategy Build(string id) => new()
    {
        Id = id,
        Name = Name.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Intercept = new Intercept
        {
            Tcp = string.IsNullOrWhiteSpace(Tcp) ? null : Tcp.Trim(),
            Udp = string.IsNullOrWhiteSpace(Udp) ? null : Udp.Trim(),
        },
        Profiles = Profiles.Select(p => p.ToProfile()).ToList(),
    };

    [RelayCommand]
    private void New()
    {
        _loading = true;
        EditingId = null;
        SelectSilently(null);
        IsReadOnly = false;
        Name = "Новая стратегия";
        Description = "";
        Tcp = "443";
        Udp = "";
        Profiles.Clear();
        Profiles.Add(new ProfileEditorViewModel(this, null));
        Notice = "";
        _loading = false;
        Revalidate();
    }

    [RelayCommand]
    private void Copy()
    {
        if (SelectedItem is null) return;
        var source = SelectedItem;
        Load(source);
        _loading = true;
        EditingId = null;
        SelectSilently(null);
        IsReadOnly = false;
        Name = source.Name + " (копия)";
        Notice = "";
        _loading = false;
        Revalidate();
    }

    [RelayCommand]
    private void AddProfile()
    {
        Profiles.Add(new ProfileEditorViewModel(this, null));
        Revalidate();
    }

    [RelayCommand]
    private void RemoveProfile(ProfileEditorViewModel profile)
    {
        Profiles.Remove(profile);
        Revalidate();
    }

    private bool CanSave() => !IsReadOnly && IsValid && IsDirty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var id = EditingId ?? UserStrategyStore.NewId();
        try
        {
            _main.UserStrategies.Save(Build(id));
        }
        catch (Exception e) when (e is StrategyRejectedException or IOException or UnauthorizedAccessException)
        {
            Errors.Add("Не сохранено: " + e.Message);
            return;
        }
        EditingId = id;
        IsDirty = false;
        if (_main.IsRunning && _main.SelectedStrategy?.Strategy.Id == id)
            _main.AppendLog("Стратегия сохранена. Изменения применятся после перезапуска обхода.");
        _main.ReloadStrategies(id);
    }

    private bool CanDelete() => !IsReadOnly && EditingId is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        try
        {
            _main.UserStrategies.Delete(EditingId!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Errors.Add("Не удалено: " + e.Message);
            return;
        }
        EditingId = null;
        IsDirty = false;
        SelectSilently(null);
        _main.ReloadStrategies(null);
    }
}
