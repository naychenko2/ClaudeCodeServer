using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// КОНТРАКТЫ ДНЯ 1 (ADR-022, раздел «Контракты»): форма JSON, на которую опирается фронт. Правка поля —
// отдельным коммитом `refactor(videoEditor): контракт …` и строкой в доклад, молча не менять.
// Сводные примеры каждого DTO лежат в ADR, их сверяет тест VideoContractExamplesTests.

// Кадр сцены: Kind = "image" — версия нити редактора картинок (ThreadId, VersionId, Follow: кадр сам
// переходит на новую версию нити) или Kind = "file" — картинка проекта (Path от корня проекта через «/»).
// Поля чужого вида не выводятся. FileName — человеческое имя загруженного «С компьютера» файла («кадр-а.png»),
// для подписи; идентичность кадра он не меняет, сервер кладёт его при загрузке и держит рядом с файлом.
// Initiator — human | agent: кто сделал версию, на которую кадр перешёл по follow (кладёт только FilmFrameFollower из
// события «Картинок»); не указан у кадра, выбранного руками или агентом через настройки. По нему фронт ставит «✦ Claude»
// только у правки агента: сам факт «кадр сменился без моего клика» правку человека в «Картинках» от агентской не отличает.
public sealed record FrameRef(
    string Kind,
    string? ThreadId = null,
    string? VersionId = null,
    bool? Follow = null,
    string? Path = null,
    string? FileName = null,
    string? Initiator = null)
{
    public const string KindImage = "image";
    public const string KindFile = "file";

    public static FrameRef Image(string threadId, string versionId, bool follow = true, string? initiator = null) =>
        new(KindImage, ThreadId: threadId, VersionId: versionId, Follow: follow, Initiator: initiator);

    public static FrameRef File(string path, string? fileName = null) => new(KindFile, Path: path, FileName: fileName);
}

// Настройки сцены: что снимать и чем. null у поля — сцена его не задаёт, берётся из префов
public sealed record VideoSceneSettingsDto(
    FrameRef? FrameA,
    FrameRef? FrameB,
    string Text,
    string? Provider,
    string? Model,
    int? DurationSec,
    string? Aspect,
    bool? Sound,
    int? Count);

// Цена в своей валюте поставщика: Currency — usd | credits | local (local бесплатен, Amount = 0)
public sealed record VideoCostDto(string Currency, double Amount);

// Снимок входов в момент съёмки: по нему считается «Кадр/текст изменён после съёмки».
// FrameA и FrameB — подписи кадров: «image:{threadId}:{versionId}» или «file:{path}»
public sealed record VideoInputsSnapshotDto(string Text, string? FrameA, string? FrameB);

// Версия клипа — один вариант запуска. File — путь в рабочей папке модуля от её корня; клиент читает
// файл ручкой GET …/scenes/{sceneId}/versions/{versionId}/file. License — лицензия модели на момент запуска
public sealed record VideoClipVersionDto(
    string VersionId,
    int Number,
    string JobId,
    int Variant,
    string Provider,
    string Model,
    double DurationSec,
    long SizeBytes,
    bool HasSound,
    string? License,
    VideoCostDto? Cost,
    string Initiator,
    VideoInputsSnapshotDto Inputs,
    DateTime CreatedAt);

// Запуск в нить: якорь в ленте — по JobId. Status — VideoLaunchStatus.*. Interrupted дублирует
// Status == "interrupted" ради простого условия на фронте: запуск оборвал перезапуск сервера,
// готовые версии сохранены
public sealed record VideoLaunchDto(
    string JobId,
    DateTime At,
    string Status,
    bool Interrupted,
    string Initiator,
    string Provider,
    string Model,
    int Count,
    string? Prompt,
    string? License,
    string? Error);

public static class VideoLaunchStatus
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    // Запуск оборвал перезапуск сервера: реестр задач в памяти, результата не будет
    public const string Interrupted = "interrupted";
}

public static class VideoInitiators
{
    public const string Human = "human";
    public const string Agent = "agent";
}

// «Кадр/текст изменён — переснять»: что поменялось относительно снимка текущей версии
public sealed record VideoSceneStaleDto(bool Text, bool FrameA, bool FrameB, string? VersionId);

// Какие версии в каком файле проекта лежат («Сохранить сцену»)
public sealed record VideoSavedFileDto(string VersionId, string Path);

// Место сцены в фильме проекта: Path — файл .film, Position — номер строки с нуля
public sealed record VideoFilmRefDto(string Path, int Position);

// Сцена — нить: один клип между кадром A и кадром B, версии, запуски, снимок для «переснять»
public sealed record VideoSceneDto(
    string SceneId,
    string Name,
    string Folder,
    VideoSceneSettingsDto Settings,
    IReadOnlyList<VideoClipVersionDto> Versions,
    string? CurrentVersionId,
    IReadOnlyList<VideoLaunchDto> Launches,
    VideoSceneStaleDto? Stale,
    IReadOnlyList<VideoSavedFileDto> SavedFiles,
    VideoFilmRefDto? FilmRef,
    DateTime CreatedAt);

// Фокус чата: сцена в работе и открытый фильм (путь .film). null — нет
public sealed record VideoFocusDto(string? SceneId, string? FilmPath);

// Нити сцен чата одним куском: Revision растёт с каждой записью, запись со старой ревизией — 409
// со свежим состоянием в теле
public sealed record VideoThreadsStateDto(VideoFocusDto Focus, long Revision, IReadOnlyList<VideoSceneDto> Scenes);

// Состояние модуля для чата одним запросом: нити, каталог, префы
public sealed record VideoStateDto(VideoThreadsStateDto Threads, VideoCatalogDto Catalog, VideoPrefsDto Prefs);

// Выбор человека для области: null у поля — не настраивал
public sealed record VideoPrefsDto(
    string? Provider,
    string? Model,
    int? DurationSec,
    string? Aspect,
    bool? Sound,
    int? Count);

// Каталог для полосы и панели: заведённые поставщики в порядке показа, у каждого — доступен ли он
// В ЭТОЙ области и почему нет. AutoProviders — порядок, в каком «Авто» их перебирает (одна точка
// AutoCandidates): фронт не считает его сам
public sealed record VideoCatalogDto(
    IReadOnlyList<VideoProviderDto> Providers,
    string AutoModelId,
    int MaxCount,
    IReadOnlyList<string> AutoProviders);

public sealed record VideoProviderDto(
    string Key,
    string Label,
    string PriceUnit,
    bool Available,
    string? Reason,
    IReadOnlyList<VideoModelDto> Models);

// Durations и Aspects — что модель принимает; LastFrame — умеет ли «кадр A → кадр B»
public sealed record VideoModelDto(
    string Id,
    string Label,
    IReadOnlyList<int> Durations,
    IReadOnlyList<string> Aspects,
    bool Sound,
    bool LastFrame,
    string? License);
