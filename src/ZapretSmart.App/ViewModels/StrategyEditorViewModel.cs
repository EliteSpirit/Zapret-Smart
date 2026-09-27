using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Storage;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.App.ViewModels;

public sealed record ListChoice(string? Id, string Title)
{
    public static readonly ListChoice All = new(null, "все домены");
}

public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly Action _changed;

    public ProfileEditorViewModel(StrategyEditorViewModel owner, Profile? profile)
    {
        Owner = owner;
        _changed = owner.Revalidate;
        _hostlist = owner.ListChoices.FirstOrDefault(c => c.Id == profile?.Hostlist) ?? ListChoice.All;
        _argsText = profile is null ? "filter-tcp=443\ndpi-desync=multisplit\ndpi-desync-split-pos=1,midsld" : string.Join('\n', profile.Args);
    }

    public StrategyEditorViewModel Owner { get; }

    public static IReadOnlyList<string> OptionNames { get; } = EngineOptionCatalog.Names.Order(StringComparer.Ordinal).ToList();

    [ObservableProperty]
    private ListChoice _hostlist;

    [ObservableProperty]
    private string _argsText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddOptionCommand))]
    private string? _selectedOption;

    partial void OnHostlistChanged(ListChoice value) => _changed();
    partial void OnArgsTextChanged(string value) => _changed();

    public IReadOnlyList<string> Args =>
        ArgsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public Profile ToProfile() => new() { Hostlist = Hostlist.Id, Args = Args };

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
        ReloadListChoices();
    }

    public ObservableCollection<ListChoice> ListChoices { get; } = [];
    public ObservableCollection<ProfileEditorViewModel> Profiles { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    public ObservableCollection<StrategyItem> Items => _main.Strategies;

    [ObservableProperty]
    private StrategyItem? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private string? _editingId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _isReadOnly;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isValid;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

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
        ReloadListChoices();
        var match = items.FirstOrDefault(i => i.Strategy.Id == EditingId);
        if (match is not null)
            SelectSilently(match);
        else if (EditingId is not null || SelectedItem is null)
            SelectedItem = items.FirstOrDefault();
    }

    private void ReloadListChoices()
    {
        var current = ListChoices.ToList();
        var ids = _main.Lists.Ids();
        if (current.Count == ids.Count + 1 && current.Skip(1).Select(c => c.Id).SequenceEqual(ids)) return;
        ListChoices.Clear();
        ListChoices.Add(ListChoice.All);
        foreach (var id in ids) ListChoices.Add(new ListChoice(id, id));
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
        catch (IOException e)
        {
            Errors.Add("Не удалено: " + e.Message);
            return;
        }
        EditingId = null;
        SelectSilently(null);
        _main.ReloadStrategies(null);
    }
}
