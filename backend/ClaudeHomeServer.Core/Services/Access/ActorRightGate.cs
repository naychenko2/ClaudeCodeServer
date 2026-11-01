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

    /// <summary>
    /// Пишущее/исполняющее/тратящее деньги действие вне разрешённого круга (notes_*, files_*, tasks_*, чтение):
    /// у человека-участника это права Write/Configure, закрытые замком ProjectAccessService, поэтому агент
    /// участника не может больше человека. Реализовано правом Configure — его нет у редактора и читателя.
    /// </summary>
    public static ActorToolRule OwnerOnly { get; } = new(ProjectRight.Configure);
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
            "chats_list", "chats_history", "deploy_status");
        // Содержимое проекта: пишется правом WriteContent (владелец и редактор — да, читатель — нет)
        Add(Workspace, ActorToolRule.Needs(ProjectRight.WriteContent),
            "files_write", "files_mkdir", "files_rename", "files_delete", "files_to_markdown");
        // Всё остальное пишущее — только арендатор (решение 01.11: участнику открыты лишь notes_*, files_*,
        // tasks_* и чтение). Ведение чатов (chats_*) и теги закрыты: спорно для chats_report_up (доклад
        // вверх из чата исполнителя) и tags_* (метаданные чатов/задач) — закрыты default-deny, ждут
        // подтверждения на финальном ревью.
        Add(Workspace, ActorToolRule.OwnerOnly,
            "chats_create", "chats_send", "chats_update", "chats_archive", "chats_continue", "chats_report_up",
            "tags_apply", "tags_remove",
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

        // --- прочие серверы (доработка S2c-6): участнику открыто только чтение; пишущее, исполняющее и
        // тратящее GPU/деньги арендатора — OwnerOnly. Инструмента нет в таблице — отказ (default-deny).
        void Server(string server, string[] reads, string[] ownerOnly)
        {
            Add(server, ActorToolRule.Read, reads);
            Add(server, ActorToolRule.OwnerOnly, ownerOnly);
        }

        Server("architecture", ["arch_context", "arch_get_element", "arch_search"],
            ["arch_create_element", "arch_update_element", "arch_delete_element", "arch_set_connection"]);
        Server("audio-editor", ["audio_state", "audio_suggest_prompt", "audio_voices"],
            ["audio_cancel", "audio_concat", "audio_focus", "audio_generate", "audio_new"]);
        Server("image-editor", ["image_state", "image_find_faces", "image_suggest_prompt"],
            ["image_cancel", "image_focus", "image_generate", "image_new"]);
        Server("video-editor", ["video_state", "video_suggest_prompt", "video_wait"],
            ["video_cancel", "video_film_build", "video_film_edit", "video_focus", "video_new",
             "video_save_scene", "video_scene_set", "video_shoot"]);
        Server("codegraph", ["codegraph_find", "codegraph_hubs", "codegraph_neighbors"], []);
        Server("dev", [], ["build", "start_stand", "stop_stand"]);
        Server("tests", [], ["pregate", "run_tests", "ui_probe"]);
        Server("dify", ["list_datasets", "list_documents", "list_segments", "search_knowledge"],
            ["add_segments", "create_dataset", "create_document_by_file", "create_document_by_text",
             "delete_dataset", "delete_document", "update_document_by_file", "update_document_by_text"]);
        Server("local-media", ["local_job_status", "local_jobs_wait", "local_models"],
            ["local_audio_enhance", "local_audio_separate", "local_audio_to_midi", "local_edit_image",
             "local_face_detail", "local_generate_image", "local_image_to_video", "local_music_edit",
             "local_music_generate", "local_reference_to_video", "local_speech", "local_text_to_video",
             "local_transcribe", "local_video_inpaint", "local_video_upscale", "local_voice_convert",
             "local_voice_train"]);
        Server("memory",
            ["dossier_get", "dossier_lookup", "memory_get_focus", "memory_list", "memory_recall", "memory_search",
             "sphere_memory_list", "sphere_memory_search", "team_memory_list", "team_memory_search"],
            ["memory_clear_focus", "memory_forget", "memory_from_note", "memory_remember", "memory_rethink",
             "memory_to_note", "sphere_memory_adopt", "sphere_memory_forget", "sphere_memory_remember",
             "team_memory_forget", "team_memory_remember", "team_memory_update"]);
        Server("notifications", ["notifications_list"],
            ["notifications_create", "notifications_delete", "notifications_mark_read"]);
        // persona_ask запускает ход другой персоны (деньги арендатора) — закрыт; спорно
        Server("personas",
            ["personas_list", "personas_get", "personas_bindings_list", "personas_automation_list",
             "personas_mcp_list", "personas_suggest_bindings"],
            ["persona_ask", "personas_ai_team", "personas_automation_create", "personas_automation_delete",
             "personas_automation_test", "personas_automation_update", "personas_bindings_set",
             "personas_create", "personas_delete", "personas_generate_avatar", "personas_mcp_grant",
             "personas_set_default", "personas_update"]);
        Server("plugins", ["draft_preview", "draft_validate"],
            ["draft_create", "draft_delete_file", "draft_write_file"]);
        // context_attach/detach правят общую строку контекста чата (основной объект один на чат)
        Server("turn-context", ["context_state"], ["context_attach", "context_detach"]);
        // watch_start исполняет произвольный poll_command на сервере и пишет watchdogs.json
        Server("watch", ["watch_list"], ["watch_cancel", "watch_start"]);
        Server("websearch", ["web_read", "web_search"], []);
        // widget_show только рисует виджет в ленте этого же чата; спорно, открыт как чтение
        Server("widgets", ["widget_show"], []);

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
