using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Notes;

// Вертикаль заметок (волна 4C, шаг 2): Obsidian-совместимый vault — `[[wikilinks]]`,
// backlinks, граф, комментарии к документам, синк с Dify; AI-сводки и теги
// (`NotesAiService`); двусторонняя синхронизация чекбоксов заметок и задач
// (`NoteTaskSyncService`); авто-истечение заметок (`NoteExpiryService`).
// `UnifiedSearchService` остаётся в корне Services — он фасад поперёк Notes+Task,
// класть его в Notes нельзя (потянет Task), в Task нельзя (потянет Notes);
// отдельная задача на разрез.
//
// Известные границы (сознательные):
// 1) `ClaudeHomeServer.Services.Tasks` — префикс-шов через `NoteTaskSyncService`
//    (`TaskManager` + `CreateTaskRequest` + `UpdateTaskRequest`): мост чекбоксов
//    заметок и карточек задач. Шов снимается порядком — Tasks уже вертикаль,
//    направление одно: Notes → Tasks.
// 2) `ClaudeHomeServer.Services.Knowledge` — префикс-шов через `NotesKnowledgeService`
//    (`IKnowledgeSyncParticipant` + `KnowledgeService` + `KnowledgeSyncTarget`):
//    синхронизация заметок с Dify-датасетом per-owner; тот же шов, что у
//    `Knowledge` → `Memory`/`Dossiers` и у `Memory`/`Spend` → `Knowledge`.
// 3) `ClaudeHomeServer.Services.Llm` — префикс-шов через `NotesAiService`
//    (`ICheapTextRunner`) для тегов/сводок заметок. Префикс-шов по прецеденту
//    `Git`/`Backgrounds`/`Deploy`/`Changelog`/`ProjectIcons`/`Tasks`/`Docs`.
// 4) `ClaudeHomeServer.Hubs` — префикс-шов через `NoteTaskSyncService` и
//    `NoteExpiryService` (`IHubContext<SessionHub>`): рассылка `notes_changed` и
//    напоминания об истечении заметок. По прецеденту `Tasks`/`Git`/`Images`/
//    `ProjectServices`/`Terminal`/`Watchdog`.
// 5) Точечные допуски к корню `ClaudeHomeServer.Services.*` — «вертикаль → спинка»:
//    - `ProjectManager` — `NoteTaskSyncService` (projectId для промоута чекбокса
//      в задачу), `NoteExpiryService` (проекты для авто-истечения заметок),
//      `NotesService` (пути к папкам заметок).
//    - `UserStore` — `NotesKnowledgeService` (имя владельца → имя Dify-датасета
//      `{username}:notes`).
// 6) Точечный допуск к `ClaudeHomeServer.Protocol` — `NotesChangedMessage` в
//    `NoteTaskSyncService.BroadcastNoteChangedAsync` (материал-аргумент `SendAsync`,
//    поле state-машины). Префикс `ClaudeHomeServer.Protocol` снят (волна 3), оставлен
//    точный тип по образцу швов у `Spend`/`Memory`/`Dossiers`/`Watchdog`/`Terminal`.
//
// Шов `Notes → Models.Note` (record `Note`/`NoteDetail`/`UpdateNoteRequest`/
// `NoteSummary`/`NoteSemanticHit` остаётся в файле NotesKnowledgeService.cs —
// рядом с владельцем домена) идёт через `ClaudeHomeServer.Models` (SharedAllowedPrefixes).
public sealed class NotesSubsystem : IAppSubsystem
{
    public string Key => "notes";

    public string Title => "Заметки";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Singleton-ы без внутреннего порядка между собой; `NoteExpiryService` —
        // hosted поверх `NotesService` (читает заметки по проектам для авто-истечения).
        services.AddSingleton<NotesService>();
        services.AddSingleton<NotesKnowledgeService>();
        services.AddSingleton<NotesAiService>();
        services.AddSingleton<NoteTaskSyncService>();
        services.AddGatedHostedService<NoteExpiryService>(config);
    }
}