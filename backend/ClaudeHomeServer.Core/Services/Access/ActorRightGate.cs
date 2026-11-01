namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Правило таблицы «инструмент → право» для агента участника (S2c-6). <see cref="Requires"/> —
/// право, которое обязан иметь актор хода в проекте чата; null — инструмент открыт актору без
/// проверки права (чтение или осознанно открытая запись, причина — в имени фабрики).
/// </summary>
public readonly record struct ActorToolRule(ProjectRight? Requires)
{
    /// <summary>Читающий инструмент: рамку и зону уже держит Narrow, права записи не нужно.</summary>
    public static ActorToolRule Read { get; } = new(null);

    /// <summary>
    /// Запись задач агентом редактора — ОСОБО открыта (решение Андрея 01.11, Q1): временно, пишется
    /// от имени владельца. Отдельная явная строка allow, а не дырка default-allow; читатель при этом
    /// не получает записи (агент читателя не больше человека-читателя), поэтому право — WriteContent.
    /// </summary>
    public static ActorToolRule TasksOpenQ1 { get; } = new(ProjectRight.WriteContent);

    /// <summary>Нужно право <paramref name="right"/> в проекте чата.</summary>
    public static ActorToolRule Needs(ProjectRight right) => new(right);
}

/// <summary>
/// Таблица «сервер/инструмент → правило» (S2c-6). Ключ — <c>{сервер}/{инструмент}</c>. DEFAULT-DENY:
/// инструмента нет в таблице — актору-участнику отказ; тест сверяет таблицу с живыми каталогами
/// инструментов и краснеет, если у нового инструмента нет строки (не молчаливое «разрешено»).
/// </summary>
public static class ActorToolRights
{
    public const string Workspace = "wsp";
    public const string Tasks = "tasks";
    public const string Notes = "notes";

    private static readonly Dictionary<string, ActorToolRule> Table = Build();

    public static IReadOnlyDictionary<string, ActorToolRule> All => Table;

    public static string Key(string server, string tool) => server + "/" + tool;

    public static bool TryGet(string server, string tool, out ActorToolRule rule) =>
        Table.TryGetValue(Key(server, tool), out rule);

    private static Dictionary<string, ActorToolRule> Build()
    {
        var t = new Dictionary<string, ActorToolRule>(StringComparer.Ordinal);
        void Add(string server, ActorToolRule rule, params string[] tools)
        {
            foreach (var tool in tools) t[Key(server, tool)] = rule;
        }

        // --- wsp ---
        Add(Workspace, ActorToolRule.Read,
            "projects_list", "projects_get", "context_list",
            "files_tree", "files_read", "files_search",
            "files_document_read", "files_document_summary", "files_document_extract",
            "knowledge_search", "knowledge_status", "search_unified",
            "git_status", "git_diff", "git_log",
            "kb_list", "kb_get", "kb_search",
            "chats_list", "chats_history", "chats_report_up", "deploy_status");
        // Содержимое проекта: пишется правом WriteContent (владелец и редактор — да, читатель — нет)
        Add(Workspace, ActorToolRule.Needs(ProjectRight.WriteContent),
            "files_write", "files_mkdir", "files_rename", "files_delete", "files_to_markdown",
            "tags_apply", "tags_remove");
        // Ведение чатов — право на ход
        Add(Workspace, ActorToolRule.Needs(ProjectRight.RunTurn),
            "chats_create", "chats_send", "chats_update", "chats_archive", "chats_continue");
        // Настройки, git-запись, индексация знаний, выкатка и необратимое — только владелец
        Add(Workspace, ActorToolRule.Needs(ProjectRight.Configure),
            "projects_create", "projects_update", "knowledge_index",
            "git_commit", "git_stage", "kb_add_document",
            "chats_delete", "deploy_start", "deploy_rollback");

        // --- tasks ---
        Add(Tasks, ActorToolRule.Read,
            "tasks_list_projects", "tasks_list", "tasks_search", "tasks_get",
            "tasks_board_columns", "tasks_find_duplicate");
        Add(Tasks, ActorToolRule.TasksOpenQ1,
            "tasks_create", "tasks_update", "tasks_complete", "tasks_delete",
            "tasks_add_subtask", "tasks_toggle_subtask");
        Add(Tasks, ActorToolRule.Needs(ProjectRight.RunTurn), "tasks_run_executor");

        // --- notes ---
        Add(Notes, ActorToolRule.Read,
            "notes_list", "notes_search", "notes_read", "notes_semantic_search", "notes_suggest_title",
            "notes_backlinks", "notes_graph", "notes_annotations", "notes_thread", "notes_resolve");
        Add(Notes, ActorToolRule.Needs(ProjectRight.WriteContent),
            "notes_create", "notes_update", "notes_move", "notes_delete",
            "notes_annotate", "notes_reply", "notes_set_status", "notes_daily", "notes_promote_task");

        return t;
    }
}

/// <summary>
/// Шов проверки актора в пишущих MCP-инструментах (S2c-6). Пишущие инструменты агента участника ходят
/// с сервисным токеном АРЕНДАТОРА (<c>context.OwnerId</c> — владелец), поэтому без проверки по
/// АКТОРУ агент редактора мог бы то, что человеку-редактору запрещено на REST. Токен не меняется
/// (ключ хранилищ); проверка идёт внутри вызова и состав <c>tools/list</c> не трогает.
/// </summary>
public interface IActorRightGate
{
    /// <summary>
    /// Текст отказа или null — вызов разрешён. Актор берётся на сервере из реестра хода по
    /// <paramref name="callerSessionId"/>, не из заголовка клиента. Актор неизвестен (фон, исполнитель
    /// без инициатора) или совпадает с арендатором — права арендатора, null. Иначе: инструмента нет
    /// в таблице — отказ (default-deny); есть право — <c>IProjectAccess.Resolve(actor, project, right)</c>.
    /// </summary>
    string? Denied(string server, string tool, string? callerSessionId, string ownerId, string? projectId);
}

/// <inheritdoc cref="IActorRightGate"/>
public sealed class ActorRightGate(ITurnActorRegistry actors, IProjectAccess access) : IActorRightGate
{
    public const string DeniedText =
        "Недоступно: в общем проекте у вашей роли нет права на это действие. Попросите владельца проекта.";

    public string? Denied(string server, string tool, string? callerSessionId, string ownerId, string? projectId)
    {
        if (string.IsNullOrEmpty(callerSessionId)) return null;
        var actor = actors.Actor(callerSessionId, ownerId);
        if (string.IsNullOrEmpty(actor) || actor == ownerId) return null;

        if (!ActorToolRights.TryGet(server, tool, out var rule)) return DeniedText;
        if (rule.Requires is not { } right) return null;
        return access.Resolve(actor, projectId, right) is null ? DeniedText : null;
    }
}
