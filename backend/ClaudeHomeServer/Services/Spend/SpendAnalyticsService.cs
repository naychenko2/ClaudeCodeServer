using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Spend;

// Фильтры среза (значения id разрезов). Owner для не-админа принудительно = текущий
// пользователь (SpendAccess), поэтому чужие данные на уровне запросов недостижимы.
public sealed record SpendFilter(
    string? Owner = null, string? Project = null, string? Chat = null, string? Task = null,
    string? Persona = null, string? Provider = null, string? Model = null, string? Source = null);

// Унифицированная строка расчётов: детальная запись (Turns=1) либо дневной агрегат.
// Detailed=false — строка из свёрнутого дня, листьев-ходов под ней нет.
public sealed record SpendSlice(
    DateOnly Date, string OwnerId, string? ProjectId, string? SessionId, string? TaskId,
    string? PersonaId, string Provider, string? Model, string Source,
    long Input, long Output, long CacheRead, long CacheCreation,
    double Cost, int Generations, int Turns, bool Detailed);

public sealed record SpendTokensDto(long Input, long Output, long CacheRead, long CacheCreation, long Total);

public sealed record SpendDayDto(string Date, bool Aggregated, long Total,
    Dictionary<string, long> BySource, int FalGenerations);

public sealed record SpendCardRowDto(string Key, string? Name, string? Meta,
    SpendTokensDto Tokens, int Turns, int FalGenerations);

public sealed record SpendPivotNodeDto(string Key, string? Name, string? Meta,
    SpendTokensDto Tokens, int Turns, int FalGenerations, bool HasDetail);

public sealed record SpendTurnDto(string Id, DateTime Timestamp, string OwnerId, string? UserName,
    string? SessionId, string? ChatName, string? ProjectId, string? ProjectName,
    string? TaskId, string? TaskTitle, string? PersonaId, string? PersonaName,
    string Provider, string? Model, string Source, string? Label,
    SpendTokensDto Tokens, int Generations, long DurationMs, bool Own);

public sealed record SpendTurnsPageDto(int Total, bool WindowClamped, IReadOnlyList<SpendTurnDto> Items);

public sealed record SpendNeighborDto(string Id, DateTime Timestamp, long Total);

public sealed record SpendPassportDto(SpendTurnDto Turn, IReadOnlyList<SpendNeighborDto> Neighbors);

public sealed record SpendOverviewDto(
    string From, string To, int DetailDays, string WindowStart, bool AllUsers,
    SpendTokensDto Totals, int Turns, int FalGenerations,
    IReadOnlyList<SpendDayDto> ByDay,
    Dictionary<string, IReadOnlyList<SpendCardRowDto>> Cards,
    IReadOnlyList<SpendTurnDto> TopTurns);

public sealed record SpendWidgetDto(SpendTokensDto Today, SpendTokensDto Week,
    int TodayTurns, int WeekTurns, int WeekFalGenerations, IReadOnlyList<SpendDayDto> ByDay);

public sealed record SpendBadgeDto(string SessionId, SpendTokensDto Total, int Turns,
    SpendTurnDto? LastTurn);

// Запросы аналитики расхода поверх SpendStore: обзор, pivot-узлы, листья-ходы, паспорт хода,
// виджет «Домой», бейдж чата. Имена разрезов резолвятся по живым реестрам (проект/чат/задача/
// персона/пользователь); удалённые сущности остаются строками с Name=null. Содержимое
// сообщений здесь не существует в принципе — храним и отдаём только метрики и названия.
public sealed class SpendAnalyticsService(SpendStore store, SessionManager sessions,
    ProjectManager projects, TaskManager tasks, PersonaManager personas, UserStore users,
    LlmProviderRegistry llmProviders)
{
    private const int CardLimit = 8;
    private const int TopTurnsLimit = 10;

    public SpendStore Store => store;

    // --- базовый слой: слайсы периода с фильтрами ---

    public List<SpendSlice> Slices(DateOnly from, DateOnly to, SpendFilter f)
    {
        var result = new List<SpendSlice>();
        foreach (var r in store.DetailsBetween(from, to))
            if (Match(r, f))
                result.Add(new SpendSlice(r.Date, r.OwnerId, r.ProjectId, r.SessionId, r.TaskId,
                    r.PersonaId, r.Provider, ResolveModel(r.Model, r.Provider), r.Source,
                    r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheCreationTokens,
                    r.CostUsd ?? 0, r.Generations, 1, Detailed: true));
        foreach (var d in store.DailyBetween(from, to))
            if (Match(d, f) && DateOnly.TryParse(d.Date, out var date))
                result.Add(new SpendSlice(date, d.OwnerId, d.ProjectId, d.SessionId, d.TaskId,
                    d.PersonaId, d.Provider, ResolveModel(d.Model, d.Provider), d.Source,
                    d.InputTokens, d.OutputTokens, d.CacheReadTokens, d.CacheCreationTokens,
                    d.CostUsd, d.Generations, d.Turns, Detailed: false));
        return result;
    }

    // Резолв модели для отображения/группировки: старые записи с пустой Model (собранные до
    // резолва в точке записи) на лету приводятся к дефолту подписки — иначе в pivot всплывёт
    // пустая группа, а фильтр по модели такие записи потеряет.
    private string ResolveModel(string? model, string provider) =>
        llmProviders.ResolveModelOrDefault(model, provider);

    private bool Match(SpendRecord r, SpendFilter f) =>
        (f.Owner is null || r.OwnerId == f.Owner)
        && (f.Project is null || (r.ProjectId ?? "") == f.Project)
        && (f.Chat is null || (r.SessionId ?? "") == f.Chat)
        && (f.Task is null || (r.TaskId ?? "") == f.Task)
        && (f.Persona is null || (r.PersonaId ?? "") == f.Persona)
        && (f.Provider is null || r.Provider == f.Provider)
        && (f.Model is null || ResolveModel(r.Model, r.Provider) == f.Model)
        && (f.Source is null || r.Source == f.Source);

    private bool Match(DailySpendRow r, SpendFilter f) =>
        (f.Owner is null || r.OwnerId == f.Owner)
        && (f.Project is null || (r.ProjectId ?? "") == f.Project)
        && (f.Chat is null || (r.SessionId ?? "") == f.Chat)
        && (f.Task is null || (r.TaskId ?? "") == f.Task)
        && (f.Persona is null || (r.PersonaId ?? "") == f.Persona)
        && (f.Provider is null || r.Provider == f.Provider)
        && (f.Model is null || ResolveModel(r.Model, r.Provider) == f.Model)
        && (f.Source is null || r.Source == f.Source);

    // --- обзор ---

    public SpendOverviewDto Overview(DateOnly from, DateOnly to, SpendFilter f, bool allUsers,
        string? currentUserId)
    {
        var slices = Slices(from, to, f);
        var acc = new Acc();
        foreach (var s in slices) acc.Add(s);

        // Полный ряд дней от from до to — фронту не нужно достраивать нули и границы
        var byDay = new List<SpendDayDto>();
        var perDay = slices.GroupBy(s => s.Date).ToDictionary(g => g.Key, g => g.ToList());
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var day = perDay.GetValueOrDefault(d) ?? [];
            var bySource = day.GroupBy(s => s.Source)
                .ToDictionary(g => g.Key, g => g.Sum(TotalOf));
            byDay.Add(new SpendDayDto(d.ToString("yyyy-MM-dd"),
                store.IsAggregated(d), day.Sum(TotalOf), bySource, day.Sum(s => s.Generations)));
        }

        var cards = new Dictionary<string, IReadOnlyList<SpendCardRowDto>>();
        if (allUsers) cards["users"] = Card(slices, "user");
        cards["projects"] = Card(slices, "project");
        cards["models"] = Card(slices, "model");
        cards["chats"] = Card(slices, "chat");
        cards["personas"] = Card(slices, "persona");
        cards["sources"] = Card(slices, "source");
        cards["providers"] = Card(slices, "provider");

        // own — от текущего пользователя, а не от фильтра среза: у админа в scope=all
        // f.Owner пуст, а при сужении user=X равен чужому владельцу (ревью Глеба, major-2)
        var topTurns = store.DetailsBetween(from, to)
            .Where(r => Match(r, f) && r.Source != SpendSources.Fal)
            .OrderByDescending(r => r.TotalTokens)
            .Take(TopTurnsLimit)
            .Select(r => ToTurnDto(r, currentUserId))
            .ToList();

        return new SpendOverviewDto(
            from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"),
            store.DetailDays, store.WindowStart.ToString("yyyy-MM-dd"), allUsers,
            acc.Tokens(), acc.Turns, acc.Generations, byDay, cards, topTurns);
    }

    private IReadOnlyList<SpendCardRowDto> Card(List<SpendSlice> slices, string groupBy) =>
        GroupNodes(slices, groupBy)
            .Take(CardLimit)
            .Select(n => new SpendCardRowDto(n.Key, n.Name, n.Meta, n.Tokens, n.Turns, n.FalGenerations))
            .ToList();

    // --- pivot ---

    public static readonly string[] PivotLevels = ["user", "project", "chat", "persona", "provider", "model", "source"];

    public IReadOnlyList<SpendPivotNodeDto> Pivot(string groupBy, DateOnly from, DateOnly to, SpendFilter f) =>
        GroupNodes(Slices(from, to, f), groupBy);

    private List<SpendPivotNodeDto> GroupNodes(List<SpendSlice> slices, string groupBy) =>
        GroupRaw(slices, groupBy)
            .Select(n =>
            {
                var (name, meta) = ResolveName(groupBy, n.Key);
                return new SpendPivotNodeDto(n.Key, name, meta, n.Tokens, n.Turns, n.FalGenerations, n.HasDetail);
            })
            .ToList();

    // Чистая группировка слайсов по разрезу (без резолва имён) — ядро pivot, тестируется отдельно
    internal static List<SpendPivotNodeDto> GroupRaw(List<SpendSlice> slices, string groupBy)
    {
        var nodes = new List<SpendPivotNodeDto>();
        foreach (var g in slices.GroupBy(s => KeyOf(s, groupBy)))
        {
            var acc = new Acc();
            var hasDetail = false;
            foreach (var s in g)
            {
                acc.Add(s);
                hasDetail |= s.Detailed;
            }
            nodes.Add(new SpendPivotNodeDto(g.Key, null, null, acc.Tokens(), acc.Turns, acc.Generations, hasDetail));
        }
        return [.. nodes.OrderByDescending(n => n.Tokens.Total + n.FalGenerations)];
    }

    private static string KeyOf(SpendSlice s, string groupBy) => groupBy switch
    {
        "user" => s.OwnerId,
        "project" => s.ProjectId ?? "",
        "chat" => s.SessionId ?? "",
        "persona" => s.PersonaId ?? "",
        "provider" => s.Provider,
        "model" => s.Model ?? "",
        "source" => s.Source,
        _ => throw new ArgumentException($"Неизвестный разрез: {groupBy}"),
    };

    // Название узла по живым реестрам; удалённые сущности — Name=null (фронт покажет
    // «удалено»). Meta — вспомогательный контекст узла (провайдер модели, тип чата и т.п.)
    private (string? Name, string? Meta) ResolveName(string groupBy, string key) => groupBy switch
    {
        "user" => (key.Length == 0 ? "Система" : users.GetById(key)?.Username, null),
        "project" => (key.Length == 0 ? "Вне проектов" : projects.GetById(key)?.Name, null),
        "chat" => ChatName(key),
        "persona" => key.Length == 0 ? ("Без персоны", null) : PersonaName(key),
        // Пустого ключа модели больше не бывает (Slices резолвит дефолт), но на случай
        // стороннего вызова GroupRaw с сырым null — честный технический fallback.
        "model" => (key.Length == 0 ? "Неизвестная модель" : key, null),
        _ => (key, null),
    };

    private (string? Name, string? Meta) ChatName(string sessionId)
    {
        if (sessionId.Length == 0) return ("Фоновые вызовы", null);
        var s = sessions.GetById(sessionId);
        if (s is null) return (null, null);
        var kind = s.TaskId is not null ? "task" : "chat";
        var name = s.Name;
        if (string.IsNullOrWhiteSpace(name) && s.TaskId is not null)
            name = tasks.GetById(s.TaskId)?.Title;
        return (string.IsNullOrWhiteSpace(name) ? "Без названия" : name, kind);
    }

    private (string? Name, string? Meta) PersonaName(string personaId)
    {
        var p = personas.GetByIdInternal(personaId);
        return p is null ? (null, null) : ($"{p.Role} ({p.Name})".Trim(), null);
    }

    // --- листья-ходы (только детальное окно) ---

    public SpendTurnsPageDto Turns(DateOnly from, DateOnly to, SpendFilter f,
        int limit, int offset, string? sort, string? currentUserId)
    {
        // Ходы существуют только в несвёрнутых днях; запрос за более ранний период честно
        // помечается WindowClamped — но только если за окном есть строки именно этого среза,
        // иначе плашка «часть ходов старше окна» показалась бы срезу, у которого за окном
        // пусто (ревью Глеба, minor-3)
        var clamped = store.DailyBetween(from, to).Any(d => Match(d, f));
        var items = store.DetailsBetween(from, to).Where(r => Match(r, f));
        items = sort == "time"
            ? items.OrderByDescending(r => r.Timestamp)
            : items.OrderByDescending(r => r.TotalTokens);
        var list = items.ToList();
        var page = list.Skip(offset).Take(limit)
            .Select(r => ToTurnDto(r, currentUserId))
            .ToList();
        return new SpendTurnsPageDto(list.Count, clamped, page);
    }

    public SpendPassportDto? Passport(string id, string? currentUserId, bool isAdmin)
    {
        var r = store.FindTurn(id);
        if (r is null) return null;
        if (!isAdmin && r.OwnerId != currentUserId) return null;

        var neighbors = new List<SpendNeighborDto>();
        if (r.SessionId is not null)
            neighbors = store.DetailsBetween(store.WindowStart, DateOnly.FromDateTime(DateTime.UtcNow))
                .Where(x => x.SessionId == r.SessionId && x.Source == r.Source)
                .OrderBy(x => x.Timestamp)
                .Select(x => new SpendNeighborDto(x.Id, x.Timestamp, x.TotalTokens))
                .ToList();
        return new SpendPassportDto(ToTurnDto(r, currentUserId), neighbors);
    }

    // --- виджет «Домой» и бейдж чата ---

    public SpendWidgetDto Widget(string userId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekAgo = today.AddDays(-6);
        var f = new SpendFilter(Owner: userId);
        var slices = Slices(weekAgo, today, f);

        var todayAcc = new Acc();
        var weekAcc = new Acc();
        var byDay = new List<SpendDayDto>();
        var perDay = slices.GroupBy(s => s.Date).ToDictionary(g => g.Key, g => g.ToList());
        for (var d = weekAgo; d <= today; d = d.AddDays(1))
        {
            var day = perDay.GetValueOrDefault(d) ?? [];
            foreach (var s in day)
            {
                weekAcc.Add(s);
                if (d == today) todayAcc.Add(s);
            }
            byDay.Add(new SpendDayDto(d.ToString("yyyy-MM-dd"), store.IsAggregated(d),
                day.Sum(TotalOf),
                day.GroupBy(s => s.Source).ToDictionary(g => g.Key, g => g.Sum(TotalOf)),
                day.Sum(s => s.Generations)));
        }
        return new SpendWidgetDto(todayAcc.Tokens(), weekAcc.Tokens(),
            todayAcc.Turns, weekAcc.Turns, weekAcc.Generations, byDay);
    }

    public SpendBadgeDto Badge(string sessionId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var f = new SpendFilter(Chat: sessionId);
        var acc = new Acc();
        foreach (var s in Slices(DateOnly.MinValue, today, f)) acc.Add(s);

        var last = store.DetailsBetween(store.WindowStart, today)
            .Where(r => r.SessionId == sessionId)
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefault();
        return new SpendBadgeDto(sessionId, acc.Tokens(), acc.Turns,
            last is null ? null : ToTurnDto(last, currentUserId: null));
    }

    // --- общее ---

    private static long TotalOf(SpendSlice s) => s.Input + s.Output + s.CacheRead + s.CacheCreation;

    private SpendTurnDto ToTurnDto(SpendRecord r, string? currentUserId)
    {
        var session = r.SessionId is null ? null : sessions.GetById(r.SessionId);
        var project = r.ProjectId is null ? null : projects.GetById(r.ProjectId);
        var task = r.TaskId is null ? null : tasks.GetById(r.TaskId);
        var persona = r.PersonaId is null ? null : personas.GetByIdInternal(r.PersonaId);
        return new SpendTurnDto(r.Id, r.Timestamp, r.OwnerId,
            r.OwnerId.Length == 0 ? "Система" : users.GetById(r.OwnerId)?.Username,
            r.SessionId, session?.Name, r.ProjectId, project?.Name,
            r.TaskId, task?.Title, r.PersonaId,
            persona is null ? null : $"{persona.Role} ({persona.Name})".Trim(),
            r.Provider, r.Model, r.Source, r.Label,
            new SpendTokensDto(r.InputTokens, r.OutputTokens, r.CacheReadTokens,
                r.CacheCreationTokens, r.TotalTokens),
            r.Generations, r.DurationMs,
            Own: currentUserId is not null && r.OwnerId == currentUserId);
    }

    private sealed class Acc
    {
        private long _in, _out, _cr, _cc;
        public int Turns { get; private set; }
        public int Generations { get; private set; }

        public void Add(SpendSlice s)
        {
            _in += s.Input;
            _out += s.Output;
            _cr += s.CacheRead;
            _cc += s.CacheCreation;
            Turns += s.Turns;
            Generations += s.Generations;
        }

        public SpendTokensDto Tokens() => new(_in, _out, _cr, _cc, _in + _out + _cr + _cc);
    }
}
