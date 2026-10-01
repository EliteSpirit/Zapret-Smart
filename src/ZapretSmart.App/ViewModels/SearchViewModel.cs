using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretSmart.Core.Community;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Lists;
using ZapretSmart.Core.Search;
using ZapretSmart.Core.Storage;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.App.ViewModels;

public sealed record ScoreRow(string Label, string Result, string Latency, bool IsFull, bool IsFailed = false)
{
    public bool IsZero => !IsFull && !IsFailed && Latency.Length == 0;

    public static ScoreRow From(CandidateScore s) => new(
        s.Candidate.Label,
        s.Error is not null ? "движок не запустился" : $"{s.Passed}/{s.Total}",
        s.Passed > 0 ? $"{s.MedianLatency.TotalMilliseconds:0} мс" : "",
        s.IsFull,
        s.Error is not null);
}

/// <summary>Шаг поиска. Пока поиск не запущен, шаги объясняют, что он будет делать; во время поиска показывают, где он сейчас.</summary>
public sealed partial class SearchStep(int number, string title, string hint, bool isLast) : ObservableObject
{
    public int Number { get; } = number;
    public string Title { get; } = title;
    public string Hint { get; } = hint;
    public bool IsLast { get; } = isLast;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
}

public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan EngineReadyTimeout = TimeSpan.FromSeconds(8);

    private readonly MainWindowViewModel _main;
    private CancellationTokenSource? _cts;
    private WinwsEngineHost? _host;
    private IReadOnlyList<string> _blocked = [];
    private string? _tempDir;

    public SearchViewModel(MainWindowViewModel main)
    {
        _main = main;
        _targetsText = string.Join('\n', SearchTargets.Default);
        Groups = SearchTargets.Groups.Select(g => new TargetGroupOption(g, this)).ToList();
        SyncGroups();
    }

    public IReadOnlyList<int> Budgets { get; } = [3, 6, 10, 20];

    public IReadOnlyList<SearchStep> Steps { get; } =
    [
        new(1, "Без обхода", "Какие из сайтов на самом деле заблокированы", false),
        new(2, "Перебор", "Два десятка техник обхода по очереди", false),
        new(3, "Доводка", "Подбор параметров для двух лучших", false),
        new(4, "Проверка", "Победитель проверяется ещё дважды", true),
    ];

    /// <summary>0 — поиск не идёт и не завершён; 1..4 — текущий шаг; 5 — все шаги пройдены.</summary>
    public void SetStep(int current)
    {
        foreach (var step in Steps)
        {
            step.IsDone = step.Number < current;
            step.IsActive = step.Number == current;
        }
    }

    public ObservableCollection<ScoreRow> Tested { get; } = [];

    public bool IsWindows => OperatingSystem.IsWindows();

    [ObservableProperty] private string _targetsText;
    [ObservableProperty] private int _budgetMinutes = 6;
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _resultName = "Найденная стратегия";

    /// <summary>Добавить к найденной стратегии QUIC (UDP 443): на нём Chrome грузит видео и Shorts YouTube.</summary>
    [ObservableProperty] private bool _addQuic = true;

    /// <summary>Добавить к найденной стратегии голос Discord (UDP): поиск его не проверяет и без этого не трогает.</summary>
    [ObservableProperty] private bool _addVoice = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveResultCommand))]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveResultCommand))]
    private CandidateScore? _best;

    [ObservableProperty]
    private bool _hasResult;

    private IReadOnlyList<string> Targets =>
        TargetsText.Split('\n').Select(SearchTargets.Normalize).Where(t => t.Length > 0).Distinct().ToList();

    /// <summary>Сервисы с CDN: галка добавляет в список все их цели или убирает их.</summary>
    public IReadOnlyList<TargetGroupOption> Groups { get; }

    private bool _syncingGroups;

    partial void OnTargetsTextChanged(string value) => SyncGroups();

    private void SyncGroups()
    {
        if (_syncingGroups || Groups is null) return;
        _syncingGroups = true;
        var selected = SearchTargets.SelectedGroups(Targets);
        foreach (var g in Groups) g.IsSelected = selected.Contains(g.Group.Id);
        _syncingGroups = false;
    }

    internal void SetGroup(TargetGroup group, bool on)
    {
        if (_syncingGroups) return;
        var targets = Targets.ToList();
        if (on) targets.AddRange(group.Targets.Where(t => !targets.Contains(t)));
        else targets.RemoveAll(group.Targets.Contains);
        TargetsText = string.Join('\n', targets);
    }

    private bool CanStart() => IsWindows && !IsSearching;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        var targets = Targets;
        var invalid = targets.Where(t => !SearchTargets.IsValid(t)).ToList();
        if (targets.Count == 0 || invalid.Count > 0)
        {
            Summary = targets.Count == 0 ? "Укажите хотя бы один домен." : "Это не домены и не адреса файлов: " + string.Join(", ", invalid);
            return;
        }

        IsSearching = true;
        _main.IsSearching = true;
        SetStep(0);
        HasResult = false;
        Best = null;
        Tested.Clear();
        Summary = "";
        _cts = new CancellationTokenSource();
        var bypassWasOn = _main.IsRunning;
        var runningId = _main.SelectedStrategy?.Strategy.Id;

        try
        {
            if (bypassWasOn)
            {
                _main.AppendLog("Поиск: основной обход остановлен на время поиска");
                await _main.StopEngineAsync();
            }

            _tempDir = Path.Combine(Path.GetTempPath(), "ZapretSmart-search-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
            await File.WriteAllLinesAsync(Path.Combine(_tempDir, SearchOptions.TargetsListId + ".txt"), SearchTargets.Hosts(targets));

            var layout = _main.Paths.Layout with { ListsDir = _tempDir, IpsetsDir = _tempDir };
            _host = new WinwsEngineHost(_main.Paths.EngineExe, layout, EngineReadyTimeout);
            var search = new StrategySearch(_host, new HttpsProber(ProbeTimeout));
            var progress = new Progress<SearchProgress>(OnProgress);
            var options = new SearchOptions { Targets = targets, TimeBudget = TimeSpan.FromMinutes(BudgetMinutes) };

            var result = await Task.Run(() => search.RunAsync(options, progress, _cts.Token));
            ApplyResult(result, targets);
        }
        catch (OperationCanceledException)
        {
            SetStep(0);
            Phase = "Поиск отменён";
        }
        catch (SearchAbortedException e)
        {
            SetStep(0);
            Phase = "Поиск прерван";
            Summary = e.Message + ". Запущено ли приложение от имени администратора? Не мешает ли антивирус?";
        }
        catch (FileNotFoundException)
        {
            SetStep(0);
            Phase = "Поиск прерван";
            Summary = "Не найден движок: " + _main.Paths.EngineExe;
        }
        catch (Exception e) when (e is StrategyRejectedException or IOException or UnauthorizedAccessException)
        {
            SetStep(0);
            Phase = "Поиск прерван";
            Summary = e.Message;
        }
        finally
        {
            Cleanup();
            IsSearching = false;
            _main.IsSearching = false;
            if (bypassWasOn && _main.StartCommand.CanExecute(null))
            {
                _main.SelectedStrategy = _main.Strategies.FirstOrDefault(x => x.Strategy.Id == runningId) ?? _main.SelectedStrategy;
                _main.AppendLog("Поиск: основной обход включается обратно");
                _main.StartCommand.Execute(null);
            }
        }
    }

    private bool CanCancel() => IsSearching;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanSaveResult() => !IsSearching && Best is not null;

    [RelayCommand(CanExecute = nameof(CanSaveResult))]
    private void SaveResult()
    {
        var name = string.IsNullOrWhiteSpace(ResultName) ? "Найденная стратегия" : ResultName.Trim();
        var id = UserStrategyStore.NewId();
        // Сайты, на которых стратегию нашли, кладём в собственный список: в general и blocked их может не быть.
        string[] lists = [id, "general", Subscriptions.BlockedDomains.Id];
        var udp = (AddQuic, AddVoice) switch
        {
            (true, true) => " " + UdpProfiles.UdpNote,
            (true, false) => " UDP: QUIC (видео и Shorts YouTube), настройки из готовой стратегии, поиском не проверялся. Голос Discord не перехватывается.",
            (false, true) => " UDP: голос Discord, настройки из готовой стратегии, поиском не проверялся. QUIC не перехватывается.",
            _ => " QUIC и голос Discord не перехватываются.",
        };
        var s = UdpProfiles.Add(CandidateGenerator.ToStrategy(Best!.Candidate, id, name, lists, autoHostlist: true,
            $"Найдена поиском {DateTime.Now:dd.MM.yyyy}: {Best.Passed}/{Best.Total} целей, {Best.MedianLatency.TotalMilliseconds:0} мс. {Best.Candidate.Label}. Списки: сайты из поиска ({id}), general, blocked и автосписок." + udp),
            AddQuic, AddVoice, lists);
        try
        {
            _main.ListStore.WriteHostlist(id, SearchTargets.Hosts(_blocked));
            _main.UserStrategies.Save(s);
        }
        catch (Exception e) when (e is StrategyRejectedException or IOException or UnauthorizedAccessException)
        {
            Summary += "\nНе сохранено: " + e.Message;
            return;
        }
        _main.ReloadStrategies(id);
        Summary += _main.IsRunning
            ? $"\nСохранено как «{name}». Сейчас работает другая стратегия: выключите обход и выберите новую на вкладке «Обход»."
            : $"\nСохранено как «{name}» и выбрано на вкладке «Обход».";
    }

    private void OnProgress(SearchProgress p)
    {
        SetStep(p.Phase switch
        {
            SearchPhase.Baseline => 1,
            SearchPhase.Explore => 2,
            SearchPhase.Refine => 3,
            SearchPhase.Verify => 4,
            SearchPhase.Done => Steps.Count + 1,
            _ => Steps.FirstOrDefault(s => s.IsActive)?.Number ?? 0,
        });
        Phase = p.Phase switch
        {
            SearchPhase.Baseline => "Проверка без обхода",
            SearchPhase.Explore => $"Перебор техник: {p.Message}",
            SearchPhase.Refine => $"Доводка: {p.Message}",
            SearchPhase.Verify => $"Повторная проверка: {p.Message}",
            _ => p.Message,
        };
        if (p.Tested is not null) Tested.Insert(0, ScoreRow.From(p.Tested));
    }

    public void ApplyResult(SearchResult r, IReadOnlyList<string> targets)
    {
        HasResult = true;
        SetStep(Steps.Count + 1);
        Best = r.Best;
        _blocked = r.Blocked;
        var lines = new List<string>();
        if (r.NotBlocked.Count > 0) lines.Add("Открываются и без обхода: " + SearchTargets.DisplayList(r.NotBlocked));
        if (r.Blocked.Count == 0)
        {
            lines.Add("Ни одна цель не заблокирована — искать нечего. Если сайты на самом деле не работают в браузере: проверьте, не включён ли VPN или другой обход (GoodbyeDPI, zapret) — тогда проверка идёт через них.");
        }
        else if (r.Best is null)
        {
            lines.Add("Ни одна техника не открыла заблокированные цели. Вероятно, блокировка по IP или подмена DNS: такое стратегия не обходит.");
        }
        else
        {
            lines.Add($"Лучшее: {r.Best.Candidate.Label}: {r.Best.Passed}/{r.Best.Total}, {r.Best.MedianLatency.TotalMilliseconds:0} мс, подтверждено повторными прогонами.");
            if (r.MissedByBest.Count > 0)
                lines.Add("С ней не открылись, хотя открывались другими вариантами (см. список ниже): " + SearchTargets.DisplayList(r.MissedByBest));
            if (r.Unreachable.Count > 0)
                lines.Add("Не открыл ни один вариант (вероятно, блокировка по IP или подмена DNS): " + SearchTargets.DisplayList(r.Unreachable));
        }
        if (r.BudgetExhausted) lines.Add("Время вышло до конца перебора — можно запустить поиск с большим лимитом.");
        if (_main.CommunityEnabled && r.Best is not null)
        {
            lines.Add(!CommunityReports.IsStandardTargets(targets)
                ? "В базу сообщества ничего не отправлено: цели отличаются от стандартного набора."
                : "Сервер базы сообщества ещё не запущен — ничего не отправлено.");
        }
        Summary = string.Join('\n', lines);
    }

    private void Cleanup()
    {
        _host?.Dispose();
        _host = null;
        _cts?.Dispose();
        _cts = null;
        if (_tempDir is null) return;
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        _tempDir = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _host?.Dispose();
    }
}

/// <summary>Галка сервиса на странице поиска.</summary>
public sealed partial class TargetGroupOption(TargetGroup group, SearchViewModel owner) : ObservableObject
{
    public TargetGroup Group { get; } = group;

    public string Title => $"{Group.Name}: {Group.Description}";

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => owner.SetGroup(Group, value);
}
