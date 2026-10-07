using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Котировка → запуск строго по QuoteId (как у звука): цена видна до запуска. Содержимое сцены
// (текст, кадры, настройки) сервер берёт из нити; запуск обязан прийти с теми же длительностью и
// числом вариантов, от которых посчитана цена, иначе отказ.
// ContextRevision (ADR-023 §Д2.1, КТ-5): при заданной ревизии сцена и кадры берутся из контекста чата на этой
// ревизии (основной video-scene, референсы frame-a/frame-b), SceneId тела игнорируется; текст поля ввода едет
// в Params.request. Ревизия не совпала со стором или с ревизией котировки — 409 context_changed

// Котировка ничего не пишет ни в префы, ни в сцену, поэтому у Format поля by и at необязательны и игнорируются: клиент
// спрашивает цену конкретной ступени, не выдавая себя за автора желания (проверяет VideoQuoteWritesNothingTests)
// Format — желание формата ЭТОГО запуска (необязательно: нет — берётся из настроек сцены, затем префов, ADR-025);
// Aspect — прежнее поле, синоним Format.Aspect
public sealed record VideoQuoteRequest(
    string SessionId,
    string SceneId,
    string? Provider,
    string? Model,
    int? Count,
    int? DurationSec,
    string? Aspect,
    bool? Sound,
    long? ContextRevision = null,
    MediaFormat? Format = null);

// Price.Unit — usd | credits | free (local бесплатен); Amount null — цена станет известна после запуска.
// Eta — полное время запуска, у «Макс» local H3 это съёмка + апскейл (ADR-025, решение 1); UpscaleEta — та его часть,
// что занимает апскейл (для подписи «съёмка ~5,5 мин, затем увеличение ~15 мин»). PostStep == "upscale" при Eta == null —
// апскейл ролика такой длины не замерен: «время не замерено», число не выдумываем. ShotEta — часть Eta на саму съёмку
// (только при PostStep; сервер отдаёт замер, клиент разность Eta − UpscaleEta не считает). Нет замера съёмки — поля нет
public sealed record VideoPriceDto(double? Amount, string Unit, bool Approx, string Source, int? Eta, int? QueueLength,
    int? UpscaleEta = null, string? PostStep = null, int? ShotEta = null);

public sealed record VideoQuoteResponse(
    string QuoteId,
    string Provider,
    string Model,
    int Count,
    int DurationSec,
    VideoPriceDto Price,
    string License,
    bool Heavy,
    DateTime ExpiresAt,
    ResolvedFormat? Format = null);

// Превью подрезки кадров (dryRun, ADR-025 §5): тот же расчёт, что у съёмки, ничего не пишет и не тратит. Anchor — рамка
// (по центру, если не указана); Aspect — пропорции формата сцены (не указаны — берутся из сцены, затем префов)
public sealed record VideoFramesPreviewRequest(string SessionId, string SceneId, string? Aspect = null, string? Anchor = null);

// По кадру: исходный размер и пропорции, размер результата и рамка в долях исходника (0..1). Подрезки нет
// (Cropped = false) — пропорции совпали, Crop = вся картинка
public sealed record VideoFramePreviewDto(int SourceWidth, int SourceHeight, string SourceAspect, bool Cropped, int ResultWidth,
    int ResultHeight, double X, double Y, double W, double H);

public sealed record VideoFramesPreviewDto(string Aspect, string Anchor, VideoFramePreviewDto? FrameA, VideoFramePreviewDto? FrameB);

// Котировка соседа при отказе поставщика: запускает её только человек, сервер на соседа сам не
// переходит никогда. Reason — почему предложили
public sealed record RetryQuote(string Provider, string Model, VideoQuoteResponse Quote, string Reason);

public sealed record VideoLaunchRequest(
    string QuoteId,
    string SessionId,
    string SceneId,
    string? Initiator = null,
    JsonObject? Params = null,
    long? Seed = null,
    long? ContextRevision = null);

// Либо JobId, либо Error с кодом (VideoEditorErrors.*). Retry — котировка соседа у отказа поставщика
public sealed record VideoLaunchResult(string? JobId, string? ErrorCode, string? Error, RetryQuote? Retry);

// «Повторить увеличение»: JobId — новая задача (стадия upscaling, отмена — DELETE jobs/{jobId}/upscale),
// SourceVersionId — версия 768p, из которой она растёт. Готовый ролик станет НОВОЙ версией сцены, исходная остаётся
public sealed record VideoUpscaleRetryResult(string JobId, string SourceVersionId);

// «Сохранить сцену» в проект: версия → файл video/<фильм>/scene-NN.mp4 (перезаписи нет → .v2.mp4)
// вместе с кадрами-нитями в кадры/. Реализация — блок 2. FilmPath — необязательный полный путь открытого фильма
// (video/<папка>/<любое имя>.film): есть — сцена встаёт именно в него, а папка сцены = папка фильма (Folder игнорируется);
// нет — прежнее правило: фильм <папка>/<имя папки>.film
public sealed record SaveSceneRequest(
    string SessionId,
    string SceneId,
    string? VersionId,
    string? Folder,
    string? FileName,
    string? FilmPath = null);

public sealed record SaveSceneResult(string Path, IReadOnlyList<string> FramePaths, bool AddedToFilm);
