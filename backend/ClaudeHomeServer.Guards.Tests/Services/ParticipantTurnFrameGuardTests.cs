using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож рамки по всему tools/list платформенного MCP (S2a-5, default-deny по образцу
/// <see cref="SubsystemBoundaryCoverageTests"/>): каждый инструмент каждого зарегистрированного
/// тулсета обязан иметь явную пометку в манифесте ниже — «отказывает в узкой рамке» (D) или
/// «проектный» (P: работает в проекте чата и не отдаёт личное). Свежий инструмент или свежий
/// тулсет без пометки — красный: состав tools/list меняется решением человека, а не молча.
///
/// Состав НЕ вводится руками: список серверов — из <see cref="ClaudeHomeServer.Services.Mcp.Http.McpToolsetRegistry"/>
/// (DI, та же точка, что у транспорта), список инструментов — из tools/list каждого сервера
/// от лица чата общего проекта. Манифест собран из таблиц отчётов S2a-3a (cbaaabf1) и
/// S2a-3b (a5e6fd0f + доработка 266119ac); расхождения таблицы с поведением — в отчёте задачи.
///
/// Проверка поведенческая, а не только по пометкам: фикстура сеет личные данные владельца с
/// уникальными маркерами (PERSONAL-*) — проект, чат с сообщением, задача, заметка личного
/// vault, файл, память глобальной и сферной персон, полка сферы, персона-маркер, база Dify —
/// и каждый инструмент вызывается из чата общего проекта (узкая рамка по ITurnFrameSource,
/// заголовок X-Caller-Session-Id). В ответе не должно быть ни маркеров, ни id личных данных.
/// Для D-строк дополнительно обязателен отказ (isError). Аргументы P-строк там, где инструмент
/// принимает id, — личные id: без рамки эти вызовы вернули бы помеченное содержимое и тест
/// покраснел — то есть сторож держит саму рамку, а не только манифест.
///
/// Сознательные границы метода (записаны в причинах строк и в отчёте задачи):
/// - внешние платные вызовы (генерации редакторов/local-media, поиск Perplexity, one-shot
///   persona_ask) в стороже не оплачиваются: их строки вызываются с аргументами, которые
///   тулсет отсекает до поставщика; отсутствие личного в них держится отсутствием личных
///   хранилищ в зависимостях этих тулсетов (проверено по конструкторам);
/// - Dify — стаб-хендлер (тот же приём, что в McpNarrowFramePersonalSpaceTests): личная база
///   с маркером и база проекта — в каталоге, сеть не трогается;
/// - строки манифеста для инструментов, которых нет в составе tools/list этого тестового
///   хоста (например write-инструменты Dify в режиме search-only), пропускаются (Skip), а
///   обратное направление — «в составе нет строки» — красное (default-deny).
/// </summary>
public class ParticipantTurnFrameGuardTests : IClassFixture<ParticipantFrameFixture>
{
    private const string D = "D"; // отказывает в узкой рамке
    private const string P = "P"; // проектный: работает в проекте чата, личное не отдаёт

    private readonly ParticipantFrameFixture _fx;

    public ParticipantTurnFrameGuardTests(ParticipantFrameFixture fx) => _fx = fx;

    private sealed record Row(string Server, string Tool, string Disposition, string Reason, string Route, string Args);

    // ──────────────────────────────────────────────────────────────────────────────
    // Манифест: инструмент → режет/проектный → причина. Строки — из таблиц S2a-3a/3b
    // (исправленных под код ревью R-S2a-3b) и из чтения тулсетов без рамки (их причины —
    // «нет гейта рамки; operative данные — проект/чат, личных хранилищ в зависимостях нет»).
    // Аргументы — шаблоны с подстановкой id фикстуры ({{...}}); Route — хвост /mcp/{server}/…
    // ──────────────────────────────────────────────────────────────────────────────
    private static readonly Row[] Manifest =
    [
        // --- wsp (S2a-3a; WorkspaceToolset.NarrowDenied, строки указаны) ---
        new("wsp", "context_list", P, "контекст текущего чата — не личные данные владельца (WorkspaceToolset.cs:340)", "{{sc}}", "{}"),
        new("wsp", "search_unified", D, "поиск по всему пространству владельца, включая личное (:343-346)", "{{sc}}", "{\"query\":\"{{tag}}\"}"),
        new("wsp", "deploy_start", D, "выкатка проекта — пространство владельца (:343)", "{{sc}}", "{}"),
        new("wsp", "deploy_rollback", D, "(:343)", "{{sc}}", "{}"),
        new("wsp", "deploy_status", D, "(:343)", "{{sc}}", "{}"),
        new("wsp", "files_delete", D, "безвозвратное удаление — закрыто целиком (:344)", "{{sc}}", "{\"path\":\"x\"}"),
        new("wsp", "chats_delete", D, "безвозвратное удаление чужого чата (:344)", "{{sc}}", "{\"sessionId\":\"{{pc}}\"}"),
        new("wsp", "projects_create", D, "проекты — пространство владельца (:344)", "{{sc}}", "{\"name\":\"x\"}"),
        new("wsp", "projects_update", D, "(:344)", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"name\":\"x\"}"),
        new("wsp", "kb_list", D, "базы Dify владельца (:345)", "{{sc}}", "{}"),
        new("wsp", "kb_get", D, "(:345)", "{{sc}}", "{\"id\":\"x\"}"),
        new("wsp", "kb_search", D, "(:345)", "{{sc}}", "{\"query\":\"{{tag}}\"}"),
        new("wsp", "kb_add_document", D, "(:345)", "{{sc}}", "{}"),
        new("wsp", "projects_get", P, "чужой projectId — отказ (:350); своего проекта — работает; в вызове личный проект", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "projects_list", P, "фильтруется зоной плана: только свой проект (:397-403,470-472)", "{{sc}}", "{}"),
        new("wsp", "files_tree", P, "чужой projectId — отказ (:350); своего — работает; в вызове личный проект (в нём PERSONAL-файл)", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "files_read", P, "то же; в вызове личный файл владельца", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"path\":\"{{pf}}\"}"),
        new("wsp", "files_write", P, "то же; в вызове запись в личный проект", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"path\":\"PERSONAL-файл2-{{tag}}.md\",\"content\":\"x\"}"),
        new("wsp", "files_search", P, "то же; поиск по личному проекту", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"query\":\"{{tag}}\"}"),
        new("wsp", "files_mkdir", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"path\":\"PERSONAL-папка-{{tag}}\"}"),
        new("wsp", "files_rename", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"path\":\"{{pf}}\",\"newPath\":\"PERSONAL-файл3-{{tag}}.md\"}"),
        new("wsp", "files_document_read", P, "то же; документ личного проекта", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"path\":\"{{pf}}\"}"),
        new("wsp", "files_document_extract", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"path\":\"{{pf}}\"}"),
        new("wsp", "files_document_summary", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"path\":\"{{pf}}\"}"),
        new("wsp", "files_to_markdown", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"path\":\"{{pf}}\"}"),
        new("wsp", "git_status", P, "чужой projectId — отказ (:350); своего — работает; в вызове личный проект", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "git_diff", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "git_log", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "git_commit", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "git_stage", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "knowledge_search", P, "чужой projectId — отказ (:350); своего — работает; в вызове личный проект", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"query\":\"{{tag}}\"}"),
        new("wsp", "knowledge_status", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "knowledge_index", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "tags_apply", P, "сущность обязана принадлежать своему проекту (:356-359); в вызове личная задача", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"entityType\":\"task\",\"entityId\":\"{{pt}}\",\"tags\":[\"{{tag}}\"]}"),
        new("wsp", "tags_remove", P, "то же", "{{sc}}", "{\"projectId\":\"{{pp}}\",\"entityType\":\"task\",\"entityId\":\"{{pt}}\",\"tags\":[\"{{tag}}\"]}"),
        new("wsp", "chats_archive", P, "чужой/невидимый sessionId — отказ (:363-369); своего проекта — работает; в вызове личный чат", "{{sc}}", "{\"sessionId\":\"{{pc}}\"}"),
        new("wsp", "chats_continue", P, "то же", "{{sc}}", "{\"sessionId\":\"{{pc}}\"}"),
        new("wsp", "chats_list", P, "без projectId — личные чаты владельца → отказ (:352); со своим проектом — список этого проекта; в вызове личный проект", "{{sc}}", "{\"projectId\":\"{{pp}}\"}"),
        new("wsp", "chats_create", P, "без projectId — личные чаты владельца → отказ (:352); со своим проектом — работает (в вызове — свой проект чата)", "{{sc}}", "{\"projectId\":\"{{sp}}\"}"),
        new("wsp", "chats_history", P, "то же; в вызове личный чат (в нём PERSONAL-сообщение)", "{{sc}}", "{\"sessionId\":\"{{pc}}\"}"),
        new("wsp", "chats_report_up", P, "то же", "{{sc}}", "{\"sessionId\":\"{{pc}}\"}"),
        new("wsp", "chats_send", P, "то же", "{{sc}}", "{\"sessionId\":\"{{pc}}\",\"text\":\"{{tag}}\"}"),
        new("wsp", "chats_update", P, "то же", "{{sc}}", "{\"sessionId\":\"{{pc}}\",\"title\":\"{{tag}}\"}"),

        // --- tasks (S2a-3a; TasksToolset.NarrowDenied :830-835, InScope/GetAccessible :875-919) ---
        new("tasks", "tasks_list_projects", D, "список проектов владельца (:831)", "{{sc}}", "{}"),
        new("tasks", "tasks_search", D, "поиск по всем задачам владельца (:831)", "{{sc}}", "{\"query\":\"{{tag}}\"}"),
        new("tasks", "tasks_list", P, "scope=all — личные задачи → отказ (:832); default scope — только свой проект (:156-158)", "{{sc}}", "{\"scope\":\"all\"}"),
        new("tasks", "tasks_get", P, "личная задача — «не найдена или недоступна» (:884-885)", "{{sc}}", "{\"id\":\"{{pt}}\"}"),
        new("tasks", "tasks_update", P, "личная задача — отказ (GetAccessible :884)", "{{sc}}", "{\"id\":\"{{pt}}\",\"title\":\"{{tag}}\"}"),
        new("tasks", "tasks_complete", P, "то же", "{{sc}}", "{\"id\":\"{{pt}}\"}"),
        new("tasks", "tasks_delete", P, "то же", "{{sc}}", "{\"id\":\"{{pt}}\"}"),
        new("tasks", "tasks_add_subtask", P, "то же", "{{sc}}", "{\"taskId\":\"{{pt}}\",\"title\":\"{{tag}}\"}"),
        new("tasks", "tasks_toggle_subtask", P, "то же", "{{sc}}", "{\"taskId\":\"{{pt}}\"}"),
        new("tasks", "tasks_run_executor", P, "то же; личная задача не запускается", "{{sc}}", "{\"id\":\"{{pt}}\"}"),
        new("tasks", "tasks_create", P, "чужой projectId — отказ (:833-835); default — свой проект (:219); в вызове личный проект", "{{sc}}", "{\"title\":\"PERSONAL-задача2-{{tag}}\",\"projectId\":\"{{pp}}\"}"),
        new("tasks", "tasks_find_duplicate", P, "кандидаты только своего проекта (:733-743); в вызове заголовок личной задачи", "{{sc}}", "{\"title\":\"задача {{tag}}\"}"),
        new("tasks", "tasks_board_columns", P, "доска своего проекта — данных владельца нет", "{{sc}}", "{}"),

        // --- notes (S2a-3a; NotesToolset :380-399) ---
        new("notes", "notes_graph", D, "граф по всем источникам, включая личный vault (:384)", "{{sc}}", "{}"),
        new("notes", "notes_semantic_search", D, "поиск по всем источникам (:384)", "{{sc}}", "{\"query\":\"{{tag}}\"}"),
        new("notes", "notes_daily", D, "дневник — личная поверхность vault (:385)", "{{sc}}", "{}"),
        new("notes", "notes_resolve", D, "обработка комментариев — личная операция vault (:385)", "{{sc}}", "{}"),
        new("notes", "notes_list", P, "source ≠ своего проекта → отказ (:387); default — только свой источник (:98-99); в вызове личный vault", "{{sc}}", "{\"source\":\"personal\"}"),
        new("notes", "notes_search", P, "фильтруется своим источником (:108-110); маркер ищется по всем источникам", "{{sc}}", "{\"query\":\"{{tag}}\"}"),
        new("notes", "notes_create", P, "source ≠ своего проекта → отказ (:391); default — источник проекта; в вызове личный vault", "{{sc}}", "{\"title\":\"PERSONAL-заметка2-{{tag}}\",\"content\":\"x\",\"source\":\"personal\"}"),
        new("notes", "notes_annotate", P, "scope ≠ своего проекта → отказ (:392)", "{{sc}}", "{\"path\":\"PERSONAL-документ-{{tag}}.md\",\"anchorText\":\"x\",\"comment\":\"y\"}"),
        new("notes", "notes_annotations", P, "(:393)", "{{sc}}", "{}"),
        new("notes", "notes_move", P, "targetSource/id не своего проекта → отказ (:394-396); в вызове личная заметка", "{{sc}}", "{\"id\":\"{{pn}}\",\"targetSource\":\"personal\"}"),
        new("notes", "notes_read", P, "id не своего проекта → отказ (:399); карточка своей заметки фильтруется (:418-424); в вызове личная заметка", "{{sc}}", "{\"id\":\"{{pn}}\"}"),
        new("notes", "notes_update", P, "то же", "{{sc}}", "{\"id\":\"{{pn}}\",\"content\":\"{{tag}}\"}"),
        new("notes", "notes_delete", P, "то же", "{{sc}}", "{\"id\":\"{{pn}}\"}"),
        new("notes", "notes_suggest_title", P, "то же", "{{sc}}", "{\"id\":\"{{pn}}\"}"),
        new("notes", "notes_backlinks", P, "упоминания чужих источников убираются (:256); в вызове заголовок личной заметки", "{{sc}}", "{\"title\":\"PERSONAL-заметка-{{tag}}\"}"),
        new("notes", "notes_thread", P, "нить чужого источника → отказ (:399); карточка фильтруется", "{{sc}}", "{\"id\":\"{{pn}}\"}"),
        new("notes", "notes_set_status", P, "id не своего проекта → отказ (:399)", "{{sc}}", "{\"id\":\"{{pn}}\",\"status\":\"open\"}"),
        new("notes", "notes_promote_task", P, "то же", "{{sc}}", "{\"id\":\"{{pn}}\"}"),

        // --- memory (S2a-3b + R-S2a-3b п.4; MemoryToolset :104-113 глобальная/сферная персона, :358 полка сферы) ---
        // 10 инструментов memory_* × 3 зоны персоны: глобальная — режется, сферная — режется
        // (решение 30.10), проектная — работает (контекст команды, не личное пространство).
        new("memory", "memory_remember", D, "личная память глобальной персоны (:110)", "{{gp}}/-", "{\"type\":\"semantic\",\"text\":\"PERSONAL-секрет-{{tag}}\"}"),
        new("memory", "memory_recall", D, "(:110)", "{{gp}}/-", "{\"query\":\"{{tag}}\"}"),
        new("memory", "memory_search", D, "(:110)", "{{gp}}/-", "{\"query\":\"{{tag}}\"}"),
        new("memory", "memory_list", D, "(:110)", "{{gp}}/-", "{}"),
        new("memory", "memory_forget", D, "(:110)", "{{gp}}/-", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_rethink", D, "(:110)", "{{gp}}/-", "{\"id\":\"нет-такого-{{tag}}\",\"text\":\"x\"}"),
        new("memory", "memory_to_note", D, "(:110)", "{{gp}}/-", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_from_note", D, "(:110)", "{{gp}}/-", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_get_focus", D, "(:110)", "{{gp}}/-", "{}"),
        new("memory", "memory_clear_focus", D, "(:110)", "{{gp}}/-", "{}"),
        new("memory", "memory_remember", D, "память сферной персоны — личное пространство (R-S2a-3b п.4, :113)", "{{sr}}/-", "{\"type\":\"semantic\",\"text\":\"PERSONAL-сферный-секрет-{{tag}}\"}"),
        new("memory", "memory_recall", D, "(:113)", "{{sr}}/-", "{\"query\":\"{{tag}}\"}"),
        new("memory", "memory_search", D, "(:113)", "{{sr}}/-", "{\"query\":\"{{tag}}\"}"),
        new("memory", "memory_list", D, "(:113)", "{{sr}}/-", "{}"),
        new("memory", "memory_forget", D, "(:113)", "{{sr}}/-", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_rethink", D, "(:113)", "{{sr}}/-", "{\"id\":\"нет-такого-{{tag}}\",\"text\":\"x\"}"),
        new("memory", "memory_to_note", D, "(:113)", "{{sr}}/-", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_from_note", D, "(:113)", "{{sr}}/-", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_get_focus", D, "(:113)", "{{sr}}/-", "{}"),
        new("memory", "memory_clear_focus", D, "(:113)", "{{sr}}/-", "{}"),
        new("memory", "memory_remember", P, "память проектной персоны — контекст команды, гейта нет (:118-213)", "{{ppp}}/{{sp}}", "{\"type\":\"semantic\",\"text\":\"рабочий факт {{tag}}\"}"),
        new("memory", "memory_recall", P, "то же", "{{ppp}}/{{sp}}", "{\"query\":\"{{tag}}\"}"),
        new("memory", "memory_search", P, "то же", "{{ppp}}/{{sp}}", "{\"query\":\"{{tag}}\"}"),
        new("memory", "memory_list", P, "то же", "{{ppp}}/{{sp}}", "{}"),
        new("memory", "memory_forget", P, "то же", "{{ppp}}/{{sp}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_rethink", P, "то же", "{{ppp}}/{{sp}}", "{\"id\":\"нет-такого-{{tag}}\",\"text\":\"x\"}"),
        new("memory", "memory_to_note", P, "то же", "{{ppp}}/{{sp}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_from_note", P, "то же; id заметки — несуществующий (личную не зовёт)", "{{ppp}}/{{sp}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "memory_get_focus", P, "то же", "{{ppp}}/{{sp}}", "{}"),
        new("memory", "memory_clear_focus", P, "то же", "{{ppp}}/{{sp}}", "{}"),
        new("memory", "sphere_memory_search", D, "полка сферы — личное пространство владельца (:358); в вызове проект сферы с помеченной полкой", "-/{{psi}}", "{\"query\":\"{{tag}}\"}"),
        new("memory", "sphere_memory_list", D, "(:358)", "-/{{psi}}", "{}"),
        new("memory", "sphere_memory_remember", D, "(:358)", "-/{{psi}}", "{\"text\":\"PERSONAL-полка2-{{tag}}\"}"),
        new("memory", "sphere_memory_forget", D, "(:358)", "-/{{psi}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "sphere_memory_adopt", D, "(:358)", "-/{{psi}}", "{}"),
        new("memory", "team_memory_remember", P, "командная память проекта — контекст проекта (А2 явно), гейта нет (:215-303)", "-/{{sp}}", "{\"text\":\"договорённость рамки {{tag}}\"}"),
        new("memory", "team_memory_search", P, "то же", "-/{{sp}}", "{\"query\":\"{{tag}}\"}"),
        new("memory", "team_memory_list", P, "то же", "-/{{sp}}", "{}"),
        new("memory", "team_memory_forget", P, "то же", "-/{{sp}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("memory", "team_memory_update", P, "то же", "-/{{sp}}", "{\"id\":\"нет-такого-{{tag}}\",\"text\":\"x\"}"),
        new("memory", "dossier_lookup", P, "паспорта изменений проекта — контекст проекта, гейта нет (:312-343)", "-/{{sp}}", "{\"query\":\"{{tag}}\"}"),
        new("memory", "dossier_get", P, "то же", "-/{{sp}}", "{\"id\":\"нет-такого-{{tag}}\"}"),

        // --- personas (S2a-3b + R-S2a-3b; PersonasToolset :116-120,149-172,180-184,241-268,332-344,494-501) ---
        new("personas", "personas_create", D, "персона — собственность владельца (:116-120)", "{{sc}}", "{\"name\":\"PERSONAL-персона2-{{tag}}\",\"role\":\"Роль\"}"),
        new("personas", "personas_update", D, "(:116-120)", "{{sc}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("personas", "personas_delete", D, "(:116-120)", "{{sc}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("personas", "personas_set_default", D, "(:116-120)", "{{sc}}", "{}"),
        new("personas", "personas_generate_avatar", D, "деньги владельца на аватар (:116-120)", "{{sc}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("personas", "personas_ai_team", D, "(:116-120)", "{{sc}}", "{}"),
        new("personas", "personas_automation_list", D, "(:116-120)", "{{sc}}", "{}"),
        new("personas", "personas_automation_create", D, "(:116-120)", "{{sc}}", "{}"),
        new("personas", "personas_automation_update", D, "(:116-120)", "{{sc}}", "{}"),
        new("personas", "personas_automation_delete", D, "(:116-120)", "{{sc}}", "{}"),
        new("personas", "personas_automation_test", D, "(:116-120)", "{{sc}}", "{}"),
        new("personas", "personas_mcp_list", D, "реестр MCP — аккаунт владельца (А2 явно, :116-120)", "{{sc}}", "{}"),
        new("personas", "personas_mcp_grant", D, "(:116-120)", "{{sc}}", "{}"),
        new("personas", "personas_list", P, "scope=global/all — отказ (:149); scope=context фильтруется: сферные убраны, глобальные — только привязанные к проекту (:168-172); фикстура сеет маркерную глобальную и сферную", "{{sc}}", "{\"scope\":\"context\"}"),
        new("personas", "personas_get", P, "глобальная/сферная — отказ (:180-183); проектная — работает; в вызове маркерная глобальная", "{{sc}}", "{\"id\":\"{{gp}}\"}"),
        new("personas", "personas_bindings_list", P, "(:241-243); в вызове маркерная глобальная", "{{sc}}", "{\"id\":\"{{gp}}\"}"),
        new("personas", "personas_suggest_bindings", D, "резан целиком: каталог подбора — всё пространство владельца (R-S2a-3b п.2, :250-256)", "{{sc}}", "{\"id\":\"{{ppp}}\"}"),
        new("personas", "personas_bindings_set", P, "целевая глобальная — отказ (:266-267); проектная — работает; безопасные аргументы (привязка tool), knowledge-целевые id в стороже не проверяются — замечено в отчёте", "{{sc}}", "{\"id\":\"{{ppp}}\",\"bindings\":[]}"),
        new("personas", "persona_ask", P, "глобальная/сферная — отказ (:494-497); проектная — работает; несуществующая персона: платный one-shot в стороже не оплачивается", "{{sc}}", "{\"personaId\":\"нет-такого-{{tag}}\",\"question\":\"{{tag}}\"}"),
        new("personas", "knowledge_search", P, "узкая рамка — только база проекта чата (:332-344, R-S2a-3b п.1); в вызове личная база маркера", "{{sc}}", "{\"datasetId\":\"{{dl}}\",\"query\":\"{{tag}}\"}"),

        // --- notifications (S2a-3b; NotificationsToolset :63-65,69-99) ---
        new("notifications", "notifications_list", D, "личный ящик владельца по всем проектам (:63-65)", "{{sc}}", "{}"),
        new("notifications", "notifications_mark_read", D, "(:63-65)", "{{sc}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("notifications", "notifications_delete", D, "(:63-65)", "{{sc}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("notifications", "notifications_create", P, "сообщение владельцу о работе ЭТОГО проекта — сознательное решение R-S2a-3b, не чтение ящика (:69-99)", "{{sc}}", "{\"title\":\"работа проекта {{tag}}\",\"body\":\"{{tag}}\"}"),

        // --- higgsfield (S2a-3b; HiggsfieldToolset :230-232, отказ до проверки токена) ---
        new("higgsfield", "generate_image", D, "аккаунт Higgsfield — деньги владельца (:230-232)", "{{sc}}", "{}"),
        new("higgsfield", "generate_video", D, "(:230-232)", "{{sc}}", "{}"),
        new("higgsfield", "generate_audio", D, "(:230-232)", "{{sc}}", "{}"),
        new("higgsfield", "job_status", D, "история генераций аккаунта (:230-232)", "{{sc}}", "{\"job_id\":\"нет-такого-{{tag}}\"}"),
        new("higgsfield", "jobs_wait", D, "(:230-232)", "{{sc}}", "{\"job_ids\":[\"нет-такого-{{tag}}\"]}"),
        new("higgsfield", "show_generation_by_ids", D, "(:230-232)", "{{sc}}", "{}"),
        new("higgsfield", "media_import_url", D, "запись в хранилище аккаунта (:230-232)", "{{sc}}", "{\"url\":\"http://127.0.0.1:1/x\"}"),
        new("higgsfield", "media_upload", D, "(:230-232)", "{{sc}}", "{}"),
        new("higgsfield", "media_upload_widget", D, "(:230-232)", "{{sc}}", "{}"),
        new("higgsfield", "models_explore", P, "публичный справочник моделей: не читает аккаунт, не тратит деньги (:231)", "{{sc}}", "{}"),

        // --- dify (S2a-3b; DifyToolset :101-109,149; фикстура: стаб-каталог + база проекта) ---
        new("dify", "search_knowledge", P, "только база этого чата проекта; явный чужой dataset_id — отказ (:107-109); в вызове личная база маркера", "{{sc}}", "{\"dataset_id\":\"{{dl}}\",\"query\":\"{{tag}}\"}"),
        new("dify", "list_documents", P, "работает на базе проекта (:116-142)", "{{sc}}", "{}"),
        new("dify", "list_segments", P, "то же; безопасные аргументы", "{{sc}}", "{\"document_id\":\"нет-такого-{{tag}}\"}"),
        new("dify", "add_segments", P, "то же; search-only режима базы проекта (валидационный отказ до поставщика)", "{{sc}}", "{}"),
        new("dify", "create_document_by_text", P, "то же", "{{sc}}", "{\"text\":\"документ проекта {{tag}}\"}"),
        new("dify", "create_document_by_file", P, "то же", "{{sc}}", "{}"),
        new("dify", "update_document_by_text", P, "то же", "{{sc}}", "{}"),
        new("dify", "update_document_by_file", P, "то же", "{{sc}}", "{}"),
        new("dify", "delete_document", P, "то же", "{{sc}}", "{\"document_id\":\"нет-такого-{{tag}}\"}"),
        new("dify", "list_datasets", P, "фильтруется: только база этого проекта (:149); без рамки каталог дал бы PERSONAL-базу", "{{sc}}", "{}"),
        new("dify", "create_dataset", D, "базы живут в пространстве владельца (:104-106)", "{{sc}}", "{\"name\":\"PERSONAL-база2-{{tag}}\"}"),
        new("dify", "delete_dataset", D, "(:104-106)", "{{sc}}", "{\"dataset_id\":\"{{dl}}\"}"),

        // --- plugins (S2a-3b; PluginsToolset :100-103, отказ до гейтов подсистемы) ---
        new("plugins", "draft_create", D, "черновики в пространстве владельца data/plugins/{owner}/_drafts", "{{sc}}", "{}"),
        new("plugins", "draft_write_file", D, "(:100-103)", "{{sc}}", "{}"),
        new("plugins", "draft_validate", D, "(:100-103)", "{{sc}}", "{}"),
        new("plugins", "draft_preview", D, "(:100-103)", "{{sc}}", "{}"),
        new("plugins", "draft_delete_file", D, "(:100-103)", "{{sc}}", "{}"),

        // --- тулсеты без рамки S2a-3: оперируют данными чата/проекта, личных хранилищ в
        // зависимостях нет (проверено по конструкторам тулсетов); пометка обязательна —
        // default-deny ---
        new("codegraph", "codegraph_find", P, "граф кода проекта чата; CodeGraphService/QueryService — проектные", "{{sc}}", "{\"query\":\"SessionManager\"}"),
        new("codegraph", "codegraph_neighbors", P, "то же", "{{sc}}", "{\"node\":\"SessionManager\"}"),
        new("codegraph", "codegraph_hubs", P, "то же", "{{sc}}", "{}"),
        new("dev", "build", P, "сборка проекта чата (DevToolset: SessionManager/ProjectManager)", "{{sc}}", "{\"target\":\"x\"}"),
        new("dev", "start_stand", P, "то же", "{{sc}}", "{\"service\":\"x\"}"),
        new("dev", "stop_stand", P, "то же", "{{sc}}", "{\"service\":\"x\"}"),
        new("tests", "run_tests", P, "тесты проекта чата (TestsToolset)", "{{sc}}", "{\"target\":\"x\"}"),
        new("tests", "ui_probe", P, "замер поднятого стенда чата", "{{sc}}", "{}"),
        new("tests", "pregate", P, "предгейт проекта чата", "{{sc}}", "{}"),
        new("turn-context", "context_state", P, "стор контекста ЭТОГО чата (IChatContextStore по сессии)", "{{sc}}", "{\"kind\":\"x\",\"ref\":\"x\"}"),
        new("turn-context", "context_attach", P, "то же", "{{sc}}", "{\"kind\":\"x\",\"ref\":\"x\"}"),
        new("turn-context", "context_detach", P, "то же", "{{sc}}", "{\"kind\":\"x\",\"ref\":\"x\"}"),
        new("watch", "watch_list", P, "сторожа этого чата (WatchdogStore по сессии)", "{{sc}}", "{}"),
        new("watch", "watch_start", P, "то же", "{{sc}}", "{\"name\":\"сторож-рамки\",\"poll_command\":\"exit 1\",\"interval_seconds\":60}"),
        new("watch", "watch_cancel", P, "то же", "{{sc}}", "{\"watch_id\":\"нет-такого-{{tag}}\"}"),
        new("websearch", "web_search", P, "внешний поиск (Perplexity), личных хранилищ нет; пустой query — до платного запроса", "{{sc}}", "{\"query\":\"\"}"),
        new("websearch", "web_read", P, "чтение внешней страницы; loopback-url — без внешней сети", "{{sc}}", "{\"url\":\"http://127.0.0.1:1/\"}"),
        new("widgets", "widget_show", P, "виджет в этот чат (IMcpStaticToolset, без хвоста маршрута)", "", "{\"title\":\"рамка\",\"html\":\"<p>{{tag}}</p>\"}"),
        new("local-media", "local_models", P, "локальная генерация проекта (свой GPU, не аккаунт и не деньги владельца)", "{{sc}}", "{}"),
        new("local-media", "local_generate_image", P, "то же; пустой аргументный набор — до постановки задачи", "{{sc}}", "{}"),
        new("local-media", "local_edit_image", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_face_detail", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_image_to_video", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_reference_to_video", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_text_to_video", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_video_inpaint", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_video_upscale", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_job_status", P, "то же", "{{sc}}", "{\"job_id\":\"нет-такого-{{tag}}\"}"),
        new("local-media", "local_jobs_wait", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_speech", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_transcribe", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_voice_convert", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_voice_train", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_music_generate", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_music_edit", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_audio_enhance", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_audio_separate", P, "то же", "{{sc}}", "{}"),
        new("local-media", "local_audio_to_midi", P, "то же", "{{sc}}", "{}"),
        // медиа-редакторы: хранилища ключуются тенантом проекта (сторожа ImageEditor/AudioEditor/
        // VideoEditorTenantKeyGuardTests), не вызывающим; провайдер в тестовом хосте не поднят —
        // вызовы до валидации/конфигурации, без денег владельца
        new("image-editor", "image_new", P, "нитка редактора этого чата (тенант проекта)", "{{sc}}", "{}"),
        new("image-editor", "image_generate", P, "то же; пустой prompt — до поставщика", "{{sc}}", "{\"prompt\":\"\"}"),
        new("image-editor", "image_state", P, "то же", "{{sc}}", "{}"),
        new("image-editor", "image_cancel", P, "то же", "{{sc}}", "{}"),
        new("image-editor", "image_suggest_prompt", P, "то же", "{{sc}}", "{}"),
        new("image-editor", "image_focus", P, "то же", "{{sc}}", "{}"),
        new("image-editor", "image_find_faces", P, "то же", "{{sc}}", "{}"),
        new("audio-editor", "audio_new", P, "задача редактора звука этого чата (тенант проекта)", "{{sc}}", "{}"),
        new("audio-editor", "audio_generate", P, "то же", "{{sc}}", "{}"),
        new("audio-editor", "audio_state", P, "то же", "{{sc}}", "{}"),
        new("audio-editor", "audio_cancel", P, "то же", "{{sc}}", "{}"),
        new("audio-editor", "audio_suggest_prompt", P, "то же", "{{sc}}", "{}"),
        new("audio-editor", "audio_concat", P, "то же", "{{sc}}", "{}"),
        new("audio-editor", "audio_focus", P, "то же", "{{sc}}", "{}"),
        new("audio-editor", "audio_voices", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_new", P, "задача редактора видео этого чата (тенант проекта)", "{{sc}}", "{}"),
        new("video-editor", "video_shoot", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_state", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_cancel", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_suggest_prompt", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_film_build", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_film_edit", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_focus", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_save_scene", P, "то же", "{{sc}}", "{}"),
        new("video-editor", "video_scene_set", P, "то же", "{{sc}}", "{}"),
        new("architecture", "arch_context", P, "граф архитектуры проекта чата (ArchitectureModelStore по проекту)", "{{sc}}", "{}"),
        new("architecture", "arch_search", P, "то же", "{{sc}}", "{\"query\":\"SessionManager\"}"),
        new("architecture", "arch_get_element", P, "то же", "{{sc}}", "{\"id\":\"нет-такого-{{tag}}\"}"),
        new("architecture", "arch_create_element", P, "то же", "{{sc}}", "{}"),
        new("architecture", "arch_update_element", P, "то же", "{{sc}}", "{}"),
        new("architecture", "arch_delete_element", P, "то же", "{{sc}}", "{}"),
        new("architecture", "arch_set_connection", P, "связь графа архитектуры этого проекта чата", "{{sc}}", "{}"),
        new("video-editor", "video_wait", P, "ожидание задачи видеоэдитора этого чата (тенант проекта)", "{{sc}}", "{}"),
    ];

    public static IEnumerable<object[]> ManifestRows() =>
        Manifest.Select(r => new object[] { r.Server, r.Tool, r.Disposition, r.Reason, r.Route, r.Args });

    /// <summary>
    /// default-deny: каждый инструмент, который тулсет реально отдаёт в tools/list от лица чата
    /// общего проекта, обязан иметь строку в манифесте. Новый инструмент/тулсет без пометки —
    /// красный (тот же приём, что SubsystemBoundaryCoverageTests по таблицам Boundaries).
    /// </summary>
    [Fact]
    public void СоставToolsList_КаждыйИнструментИмеетПометкуВМанифесте()
    {
        // Защита от вакуумного прохода (доказано мутациями SubsystemBoundaryCoverageTests):
        // пустой или мелкий набор серверов дал бы зелёный при нуле проверки.
        _fx.Served.Count.Should().BeGreaterThanOrEqualTo(15,
            "сторож обязан видеть все зарегистрированные тулсеты (ориентир — 21)");
        _fx.Served.Values.Sum(v => v.Count).Should().BeGreaterThanOrEqualTo(150,
            "платформенный tools/list — сотни инструментов; меньше — значит сбор состава сломалась");

        var labeled = Manifest.Select(r => (r.Server, r.Tool)).ToHashSet();
        var unlabeled = _fx.Served
            .SelectMany(kv => kv.Value.Select(t => ($"{kv.Key}/{t}", kv.Key, t)))
            .Where(x => !labeled.Contains((x.Item2, x.Item3)))
            .Select(x => x.Item1)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        unlabeled.Should().BeEmpty(
            "каждый инструмент платформенного MCP обязан иметь пометку в манифесте ParticipantTurnFrameGuardTests "
            + "(«D: отказывает в узкой рамке» или «P: проектный — работает в проекте чата и не отдаёт личное») "
            + "со строкой-причиной. Если инструмент новый — добавь строку; если тулсет новый — "
            + "заведи его раздел. Без пометки неопределённое поведение в общий чат не приезжает. "
            + "Без пометки:\n" + string.Join("\n", unlabeled));
    }

    /// <summary>
    /// Поведенческая часть: каждый инструмент из состава — вызов от лица чата общего проекта
    /// (узкая рамка) с аргументами строки манифеста. В ответе — ноль маркеров и id личных данных
    /// фикстуры; для D-строк дополнительно обязателен отказ. Строки инструментов, которых нет
    /// в составе этого тестового хоста (search-only Dify, неподнятые модули), пропускаются.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(ManifestRows))]
    public async Task Инструменты_ВУзкойРамке_НеОтдаютЛичныеДанные(
        string server, string tool, string disposition, string reason, string route, string args)
    {
        Skip.If(!_fx.Served.ContainsKey(server),
            $"сервер «{server}» не зарегистрирован в этом тестовом хосте (модуль не поднят)")
            ;
        Skip.If(!_fx.Served[server].Contains(tool),
            $"«{server}/{tool}» нет в составе tools/list этого тестового хоста (например search-only Dify)")
            ;

        var resolvedArgs = _fx.Resolve(args);
        var (text, isError) = await _fx.CallAsync(server, _fx.Resolve(route), tool,
            JsonNode.Parse(resolvedArgs)!);

        if (disposition == D)
            isError.Should().BeTrue(
                $"[{server}/{tool}] обязан отказывать в узкой рамке ({reason}); получил: {text}");

        // Главное для обоих классов: ни маркеров, ни id/названий личных данных владельца.
        // Подстроки, которые есть в аргументах вызывающего (id из args), — не вынос:
        // тот же критерий, что у McpNarrowFrameTests (задача проверяется по названию,
        // а не по id, который вызывающий и так знает).
        _fx.AssertNoPersonal($"[{server}/{tool}]: {text}", resolvedArgs);
    }
}

/// <summary>
/// Фикстура сторожа: личный мир владельца с маркерами PERSONAL-* и общий проект с чатом.
/// Тот же порядок, что у McpNarrowFrameTests / McpNarrowFramePersonalSpaceTests (S2a-3a/3b):
/// личные данные сеются от лица личного чата (широкая рамка), вызовы проверяются от лица
/// чата общего проекта (узкая). Dify — стаб-хендлер: каталог с личной базой-маркером и базой
/// проекта; сети нет. Состав tools/list собирается здесь же — от лица общего чата.
/// </summary>
public sealed class ParticipantFrameFixture : IDisposable
{
    public TestWebApplicationFactory Factory { get; }
    public HttpClient Client { get; }

    public string Tag { get; } = Guid.NewGuid().ToString("N")[..8];

    // личные данные владельца (маркеры)
    public string PersonalProject = "";
    public string PersonalChat = "";
    public string PersonalTask = "";
    public string PersonalNote = "";
    public string PersonalFile = "";
    public string GlobalPersona = "";
    public string Sphere = "";
    public string SpherePersona = "";
    public string PersonalInSphere = "";
    public string PersonalDataset = "";

    // контекст общего проекта
    public string SharedProject = "";
    public string SharedChat = "";
    public string ProjectPersona = "";
    public string ProjectDataset = "";

    /// <summary>Сервер → инструменты, которые тулсет реально отдаёт в tools/list от лица общего чата.</summary>
    public Dictionary<string, SortedSet<string>> Served { get; } = new(StringComparer.Ordinal);

    private sealed class DifyStub(string tag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = (path, request.Method.Method) switch
            {
                ("/datasets", "POST") => "{\"id\":\"ds-created-1\"}",
                ("/datasets", _) => "{\"data\":["
                    + $"{{\"id\":\"ds-personal-{tag}\",\"name\":\"testuser:kb:PERSONAL-база-{tag}\",\"permission\":\"only_me\",\"document_count\":0,\"word_count\":0,\"created_at\":0}},"
                    + $"{{\"id\":\"ds-project-{tag}\",\"name\":\"testuser:kb:Проект-база-{tag}\",\"permission\":\"only_me\",\"document_count\":0,\"word_count\":0,\"created_at\":0}},"
                    + "{\"id\":\"ds-common-1\",\"name\":\"ОБЩАЯ-база\",\"permission\":\"all_team_members\",\"document_count\":0,\"word_count\":0,\"created_at\":0},"
                    + "{\"id\":\"ds-foreign-1\",\"name\":\"otheruser:kb:чужая-база\",\"permission\":\"only_me\",\"document_count\":0,\"word_count\":0,\"created_at\":0}"
                    + "],\"has_more\":false}",
                var (p, _) when p.EndsWith("/segments") => "{\"data\":[]}",
                // Dify retrieve: KnowledgeService читает records[]. Личная база отвечает
                // помеченным содержимым — без рамки любой поиск по ней красит сторож;
                // база проекта — нейтральным.
                var (p, _) when p.EndsWith("/retrieve") => p.Contains($"ds-personal-{tag}")
                    ? "{\"query\":\"q\",\"records\":[{\"segment\":{\"content\":\"PERSONAL-фрагмент-" + tag
                      + "\",\"document\":{\"id\":\"doc-personal-1\",\"name\":\"PERSONAL-документ-" + tag + "\"}},\"score\":0.9}],\"document_search_count\":1}"
                    : "{\"query\":\"q\",\"records\":[{\"segment\":{\"content\":\"рабочий фрагмент проекта\",\"document\":{\"id\":\"doc-project-1\",\"name\":\"Проект-документ\"}},\"score\":0.9}],\"document_search_count\":1}",
                var (p, _) when p.StartsWith("/datasets/") && p.Contains("/documents") => "{\"data\":[],\"has_more\":false,\"total\":0}",
                _ => "{\"data\":[],\"has_more\":false}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    public ParticipantFrameFixture()
    {
        var dify = new DifyStub(Tag);
        Factory = new TestWebApplicationFactory
        {
            // IsConfigured=true без сети: DifyToolset/каталог ходят в записывающий хендлер
            // (тот же приём, что McpNarrowFramePersonalSpaceTests). PerplexityOptions подменяется
            // целиком: Program.cs:1137 создаёт его эagerly из конфига — ExtraConfig к этому моменту
            // ещё не применён, а ExtraServices идёт после встроенных регистраций. Состав
            // websearch-tools/list зависит от настройки (WebSearchToolset.cs:41); платных запросов
            // тест не делает: пустой query и loopback-url отсекаются до поставщика.
            ExtraServices = services => services
                .AddSingleton(new ClaudeHomeServer.Services.WebSearch.PerplexityOptions { ApiKey = "test-key" })
                .AddSingleton<KnowledgeService>(sp =>
                    new KnowledgeService(
                        new StubHttpClientFactory(dify),
                        Options.Create(new DifyOptions { ApiUrl = "http://dify-fake.test", ApiKey = "test-key" }),
                        sp.GetRequiredService<WorkspaceKnowledgeStore>())),
        };
        Client = Factory.CreateAuthenticatedClient();
        ArrangeAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => Factory.Dispose();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<string> PostJsonAsync(string url, object body)
    {
        var resp = await Client.PostAsJsonAsync(url, body);
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task<string> CreateProjectAsync(string name, string? groupId = null) =>
        await PostJsonAsync("/api/projects", new { name, groupId });

    private async Task<string> CreateSessionAsync(string projectId) =>
        await PostJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });

    private async Task<string> CreatePersonaAsync(string name, string scope, string? projectId = null, string? sphereId = null) =>
        await PostJsonAsync("/api/personas", new { name, role = "Роль", scope, projectId, sphereId });

    private async Task ArrangeAsync()
    {
        // ── личный мир владельца (широкая рамка: от лица личного чата) ──
        PersonalProject = await CreateProjectAsync($"PERSONAL-проект-{Tag}");
        PersonalChat = await CreateSessionAsync(PersonalProject);
        PersonalTask = await PostJsonAsync($"/api/projects/{PersonalProject}/tasks", new { title = $"PERSONAL-задача-{Tag}" });
        PersonalDataset = $"ds-personal-{Tag}"; // id из стаб-каталога Dify

        var (noteText, noteErr) = await CallAsyncRaw("notes", PersonalChat, "notes_create",
            new { title = $"PERSONAL-заметка-{Tag}", content = "личное содержимое", source = "personal" }, PersonalChat);
        noteErr.Should().BeFalse(noteText);
        PersonalNote = IdOf(JsonSerializer.Deserialize<JsonElement>(noteText));

        PersonalFile = $"PERSONAL-файл-{Tag}.md";
        var (fileText, fileErr) = await CallAsyncRaw("wsp", PersonalChat, "files_write",
            new { projectId = PersonalProject, path = PersonalFile, content = "личное содержимое" }, PersonalChat);
        fileErr.Should().BeFalse(fileText);

        // сообщение в личном чате — чтобы чужой chats_history без рамки возвращал маркер
        (await Client.PostAsJsonAsync($"/api/sessions/{PersonalChat}/messages",
            new { text = $"PERSONAL-сообщение-{Tag}", wait = "none" })).EnsureSuccessStatusCode();

        GlobalPersona = await CreatePersonaAsync($"PERSONAL-персона-{Tag}", "global");
        var (memText, memErr) = await CallAsyncRaw("memory", $"{GlobalPersona}/-", "memory_remember",
            new { type = "semantic", text = $"PERSONAL-секрет-{Tag}" }, PersonalChat);
        memErr.Should().BeFalse(memText);

        // сфера: личная группировка владельца; её персона и её полка — маркеры
        (await Client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.Spheres}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        // паспорта изменений — чтобы строки манифеста dossier_* были в составе и на вызове
        (await Client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.ChangeDossiersRecall}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        Sphere = await PostJsonAsync("/api/project-groups", new { name = $"сфера-{Tag}" });
        SpherePersona = await CreatePersonaAsync($"PERSONAL-сферная-{Tag}", "sphere", sphereId: Sphere);
        PersonalInSphere = await CreateProjectAsync($"PERSONAL-сфера-проект-{Tag}", Sphere);
        var (shelfText, shelfErr) = await CallAsyncRaw("memory", $"-/{PersonalInSphere}", "sphere_memory_remember",
            new { text = $"PERSONAL-полка-{Tag}" }, PersonalChat);
        shelfErr.Should().BeFalse(shelfText);
        // та же форма, что у анти-теста McpNarrowFramePersonalSpaceTests: персона+проект сферы
        var (spherMemText, spherMemErr) = await CallAsyncRaw("memory", $"{SpherePersona}/{PersonalInSphere}", "memory_remember",
            new { type = "semantic", text = $"PERSONAL-сферный-секрет-{Tag}" }, PersonalChat);
        spherMemErr.Should().BeFalse(spherMemText);

        // ── общий проект (с участником-редактором) и его чат ──
        SharedProject = await CreateProjectAsync($"Общий-{Tag}");
        Factory.Services.GetRequiredService<ProjectManager>().SetMembers(SharedProject,
            [new ProjectMember { UserId = "участник-" + Tag, Role = ProjectRole.Editor }]);
        SharedChat = await CreateSessionAsync(SharedProject);
        ProjectPersona = await CreatePersonaAsync($"КОМАНДА-{Tag}", "project", projectId: SharedProject);

        // база знаний этого проекта — та же запись стора, что делает живая связка
        // (формулу дефолтного датасета DifyToolset/PersonasToolset читает живьём)
        ProjectDataset = $"ds-project-{Tag}";
        var projects = Factory.Services.GetRequiredService<ProjectManager>();
        var store = Factory.Services.GetRequiredService<WorkspaceKnowledgeStore>();
        var wk = store.GetOrCreate(projects.GetById(SharedProject)!.RootPath);
        wk.DifyDatasetId = ProjectDataset;
        store.Save(wk);

        // ── состав tools/list от лица общего чата — источник правды манифеста ──
        var registry = Factory.Services.GetRequiredService<ClaudeHomeServer.Services.Mcp.Http.McpToolsetRegistry>();
        foreach (var server in registry.Names)
        {
            // Состав может быть пустым по внешней зависимости хоста (higgsfield — снимок
            // справочника из сети, HiggsfieldToolset.ToolsFor): такие строки манифеста
            // пропускаются (Skip), а общий размер набора держит вакуумный порог в первом
            // тесте. Пустой состав здесь — не находка сторожа, а граница тестового хоста.
            var names = await ListToolsAsync(server);
            Served[server] = new SortedSet<string>(names, StringComparer.Ordinal);
        }
    }

    /// <summary>tools/list сервера от лица общего чата (маршрут — по контракту тулсета).</summary>
    private async Task<List<string>> ListToolsAsync(string server)
    {
        var resp = await Client.PostAsJsonAsync(UrlFor(server, Resolve(RouteFor(server))),
            new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"tools/list «{server}»");
        var tools = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools");
        return tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    // Хвост маршрута по контракту тулсета: статический (widgets) — без хвоста; memory —
    // {personaId}/{projectId} (состав BuildTools зависит от разрешения хвоста: MemoryToolset.cs:520-534);
    // остальные параметризованные — id чата-вызывателя.
    private string RouteFor(string server) => server switch
    {
        "widgets" => "",
        "memory" => "{{ppp}}/{{sp}}",
        _ => "{{sc}}",
    };

    private static string UrlFor(string server, string route) =>
        route.Length == 0 ? $"/mcp/{server}" : $"/mcp/{server}/{route}";

    /// <summary>Вызов инструмента от лица чата общего проекта (узкая рамка по заголовку).</summary>
    public async Task<(string Text, bool IsError)> CallAsync(string server, string route, string tool, JsonNode args) =>
        await CallAsyncJson(server, route, tool, args.ToJsonString());

    public async Task<(string Text, bool IsError)> CallAsyncRaw(string server, string route, string tool, object args, string callerChat) =>
        await CallAsyncJson(server, route, tool, JsonSerializer.Serialize(args), callerChat);

    private async Task<(string Text, bool IsError)> CallAsyncJson(string server, string route, string tool, string argsJson, string? caller = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, UrlFor(server, route))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = 1, method = "tools/call",
                    @params = new { name = tool, arguments = JsonNode.Parse(argsJson) },
                }),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Caller-Session-Id", caller ?? SharedChat);
        var resp = await Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "MCP отвечает 200 даже на отказ — isError в теле");
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, isError);
    }

    public string Resolve(string template) => template
        .Replace("{{tag}}", Tag)
        .Replace("{{sc}}", SharedChat)
        .Replace("{{sp}}", SharedProject)
        .Replace("{{pc}}", PersonalChat)
        .Replace("{{pp}}", PersonalProject)
        .Replace("{{pt}}", PersonalTask)
        .Replace("{{pn}}", PersonalNote)
        .Replace("{{pf}}", PersonalFile)
        .Replace("{{gp}}", GlobalPersona)
        .Replace("{{sr}}", SpherePersona)
        .Replace("{{psi}}", PersonalInSphere)
        .Replace("{{ppp}}", ProjectPersona)
        .Replace("{{dp}}", ProjectDataset)
        .Replace("{{dl}}", PersonalDataset);

    public JsonNode ResolveArgs(string argsTemplate) => JsonNode.Parse(Resolve(argsTemplate))!;

    /// <summary>
    /// Ни маркеров, ни id личных данных — в тексте ответа любого инструмента. Подстроки,
    /// переданные в аргументах вызова (argsJson), не проверяются: эхо собственных id
    /// вызывающего — не вынос (тот же критерий, что у Tasks_УзкаяРамка_ЛичнаяЗадачаБезПроекта_ПоId_Отказ
    /// в McpNarrowFrameTests: там личная задача проверяется по названию, а не по id).
    /// </summary>
    public void AssertNoPersonal(string text, string? argsJson = null)
    {
        foreach (var (label, value) in new (string, string)[]
        {
            ("маркер PERSONAL", "PERSONAL"),
            ("личный проект", PersonalProject),
            ("личный чат", PersonalChat),
            ("личная задача", PersonalTask),
            ("личная заметка", PersonalNote),
            ("глобальная персона", GlobalPersona),
            ("сферная персона", SpherePersona),
            ("проект сферы", PersonalInSphere),
            ("личная база Dify", PersonalDataset),
        })
        {
            if (value.Length == 0 || (argsJson is not null && argsJson.Contains(value))) continue;
            text.Should().NotContain(value, $"вынес {label}");
        }
    }
}
