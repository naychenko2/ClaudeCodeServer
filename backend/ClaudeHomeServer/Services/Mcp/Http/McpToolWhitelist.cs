using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Белый список инструментов сервера для профиля провайдера (<c>LlmProviderConfig.KeepMcpTools</c>).
/// Отвечает на один вопрос: какие инструменты сервера <c>name</c> видит и может звать чат,
/// от имени которого пришёл запрос.
///
/// Профиль резолвится по цепочке «сессия-вызыватель → её эффективная модель → провайдер»:
/// заголовок <c>X-Caller-Session-Id</c> кладёт в конфиг хода наш же код (ClaudeSession), сессия
/// проверяется на принадлежность владельцу токена. Все звенья — свойства СЕССИИ, не хода,
/// поэтому инвариант стабильности состава <c>tools/list</c> не нарушается (McpToolsetStabilityTests):
/// в пределах одной сессии ответ постоянен.
///
/// Фильтра нет (сервер отдаётся целиком) во всех случаях, когда профиль не определён: запрос
/// не от хода (нет заголовка), сессия чужая или уже удалена, модель не принадлежит стороннему
/// провайдеру, у провайдера нет списка для этого сервера. «Резать» умеет только явно заданный
/// список; урезание общего набора живёт отдельной осью (TrimMcpServers/KeepMcpServers,
/// целыми серверами).
///
/// ЧЕСТНАЯ ГРАНИЦА ЭТОГО ФИЛЬТРА: он защищает от ошибки модели, а НЕ от намеренного обхода.
/// Пропуск заголовка X-Caller-Session-Id снимает фильтр, а под --bare у исполнителя есть Bash
/// и лежащий на диске конфиг хода с адресом узла и сервисным JWT владельца — то есть curl мимо
/// заголовка вызовет и отфильтрованный инструмент (ревью 2026-09-06, M-1). Шире собственных
/// прав это не пускает: подмена ЧУЖОГО id закрыта (GetOwned вернёт null — чужая сессия тоже
/// даёт «фильтра нет», а не чужой профиль), сузить или перенаправить фильтр нельзя.
/// Закрыть обход можно резолвом сессии из хвоста маршрута при отсутствии заголовка — у восьми
/// тулсетов из девяти хвост И ЕСТЬ сессия-вызыватель; заведено отдельной задачей.
/// </summary>
public sealed class McpToolWhitelist(
    SessionManager sessions,
    ModelAssignmentResolver assignments,
    LlmProviderRegistry providers)
{
    /// <summary>
    /// Разрешённые имена инструментов сервера или null — фильтра нет (сервер целиком).
    /// </summary>
    public IReadOnlySet<string>? AllowedTools(string server, McpToolCallContext context)
    {
        if (string.IsNullOrWhiteSpace(context.CallerSessionId)) return null;
        // GetOwned, а не GetById: сессию называет клиент, и чужая (или несуществующая) не смеет
        // ни ослабить, ни ужесточить набор — просто не даёт профиля
        var session = sessions.GetOwned(context.CallerSessionId, context.OwnerId);
        if (session is null) return null;

        // Та же формула, что у ClaudeSession.EffectiveModel: место сессии → назначение места
        // поверх модели чата. Пустая Session.Model означает «по назначению места», и без
        // резолва провайдер локальной модели не нашёлся бы вовсе.
        var model = assignments.Resolve(UsageKeyFor(session), session.Model, context.OwnerId)
            ?? session.Model;
        var provider = providers.ResolveByModel(model);
        if (provider is null || provider.KeepMcpTools.Count == 0) return null;

        // Ключи словаря приходят из конфига и регистр не гарантируют — ищем как KeepMcpServers,
        // по OrdinalIgnoreCase (словарь крошечный, перебор дешевле второго индекса)
        string[]? tools = null;
        foreach (var (key, value) in provider.KeepMcpTools)
            if (string.Equals(key, server, StringComparison.OrdinalIgnoreCase)) { tools = value; break; }

        // Пустой список = «ключа нет»: пустой массив в IConfiguration неотличим от отсутствующей
        // секции, и трактовать его как «не отдавать ничего» значило бы наказывать за форму записи
        return tools is not { Length: > 0 }
            ? null
            : new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Разрешён ли вызов инструмента. Второй гейт: имя инструмента модель может знать из
    /// прошлого опыта или угадать, и без проверки на вызов фильтр состава был бы косметикой.
    /// </summary>
    public bool Allows(string server, string tool, McpToolCallContext context) =>
        AllowedTools(server, context) is not { } allowed || allowed.Contains(tool);

    // Место применения по признакам сессии — та же формула, что SessionManager.UsageKeyFor,
    // ClaudeSession.UsageKey и ModelsController.UsageKeyFor (исполнитель задач → персона → чат)
    private static string UsageKeyFor(Models.Session s) =>
        s.TaskExecution || s.TaskId is not null ? LocalActionCatalog.TasksExecutor
        : !string.IsNullOrWhiteSpace(s.PersonaId) ? LocalActionCatalog.ChatPersona
        : LocalActionCatalog.ChatNew;
}
