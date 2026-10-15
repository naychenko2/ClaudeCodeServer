namespace ClaudeHomeServer.Services.Media;

// Шов «история медиа-генераций: где лежит результат» (ADR-030 §3). Реализации — в вертикалях,
// каждая в своей: image-editor, audio-editor, video-editor, local-media (и lora). Шов
// необязательный: у выключенной подсистемы реализации нет → 410 source_disabled, а не 500
// (правило отключаемости ADR-014). Связь «вертикаль → вертикаль» не появляется: источник
// читает только LocalJobOrigin (Core) и свои собственные id.
public interface IMediaHistoryResultSource
{
    // Ключ источника: LocalJobOrigin.Source* (local-media | image-editor | audio-editor |
    // video-editor | lora). Контроллер подбирает реализацию по origin.Source записи.
    string Source { get; }

    // Разрешить файл результата для варианта n (0-based). Возвращает VariantCount (валидация
    // n), HostPath/ContentType (файл) или Reason (410). null — источник не разобрался (нет
    // задачи/папки): читатель отвечает 410 missing.
    Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct);
}

// Ссылка на запись истории: всё, что сервер знает из журнала (prompt_id, владелец, проект,
// job_id источника, сессия). Путь с клиента не приходит — только id записи и номер варианта.
public sealed record MediaHistoryRef(
    string PromptId,
    string? OwnerId,
    string? ProjectId,
    string? SourceJobId,
    string? SessionId);

// Причина, почему файла нет (410). Wire-имена (expired/missing/…) — в контроллере; список
// открытый, фронт показывает незнакомую причину как «Файл недоступен».
public enum MediaHistoryGoneReason
{
    // expired — истёк срок рабочей папки редактора или задачи стора
    Expired,
    // missing — файла нет на месте (удалили)
    Missing,
    // project_gone — проект удалён
    ProjectGone,
    // local_project — проект переехал на устройство, его файлы не на сервере
    LocalProject,
    // source_disabled — подсистема источника выключена, реализации шва нет
    SourceDisabled,
    // no_result — исход не completed (ошибка, отмена, прерывание): результата и не было
    NoResult,
}

// Итог разрешения: число вариантов (валидация n) и файл (HostPath/ContentType) или причина
// (Reason). HostPath — полный путь на сервере; ContentType — по сигнатуре файла.
public sealed record MediaHistoryResolution(
    int VariantCount,
    string? HostPath,
    string? ContentType,
    MediaHistoryGoneReason? Reason);
