namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Тела ручек нитей сцен (добавлены после КТ-1: контракт перечислял DTO, но не обёртки с ревизией).
// Revision — ревизия, от которой считал фронт; устарела — 409 revision_conflict со свежим состоянием.
// Ответ каждой мутации — VideoThreadsStateDto

// Новая сцена: Folder — папка в video/** проекта ("" — не задана; у личного чата только ""), Settings —
// не заданы, берутся из префов области, Name — не задано, «Сцена N»
public sealed record VideoSceneCreateRequest(string? Folder, VideoSceneSettingsDto? Settings, string? Name, long Revision);

public sealed record VideoSceneFocusRequest(VideoFocusDto Focus, long Revision);

public sealed record VideoSceneSettingsRequest(VideoSceneSettingsDto? Settings, long Revision);

public sealed record VideoSceneCurrentRequest(string? VersionId, long Revision);
