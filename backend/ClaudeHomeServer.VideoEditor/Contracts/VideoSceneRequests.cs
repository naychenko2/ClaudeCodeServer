namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Тела ручек нитей сцен (добавлены после КТ-1: контракт перечислял DTO, но не обёртки с ревизией).
// Revision — ревизия, от которой считал фронт; устарела — 409 revision_conflict со свежим состоянием.
// Ответ каждой мутации — VideoThreadsStateDto

// Новая сцена: Folder — папка в video/** проекта ("" — не задана; у личного чата только ""), Settings —
// не заданы, берутся из префов области, Name — не задано, «Сцена N»
public sealed record VideoSceneCreateRequest(string? Folder, VideoSceneSettingsDto? Settings, string? Name, long Revision);

public sealed record VideoSceneFocusRequest(VideoFocusDto Focus, long Revision);

// Force — сменить пропорции сцены фильма вопреки замку («Всё равно 9:16»): без него 409 film_format_locked
public sealed record VideoSceneSettingsRequest(VideoSceneSettingsDto? Settings, long Revision, bool? Force = null);

// Тело 409 film_format_locked: что держит замок. Aspect — пропорции фильма, которые просили сменить
public sealed record VideoFormatLockedError(string Error, string Code, VideoFormatLockDto LockedBy);

public sealed record VideoSceneCurrentRequest(string? VersionId, long Revision);
