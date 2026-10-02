using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Котировка → запуск строго по QuoteId (как у звука): цена видна до запуска. Содержимое сцены
// (текст, кадры, настройки) сервер берёт из нити; запуск обязан прийти с теми же длительностью и
// числом вариантов, от которых посчитана цена, иначе отказ

public sealed record VideoQuoteRequest(
    string SessionId,
    string SceneId,
    string? Provider,
    string? Model,
    int? Count,
    int? DurationSec,
    string? Aspect,
    bool? Sound);

// Price.Unit — usd | credits | free (local бесплатен); Amount null — цена станет известна после запуска
public sealed record VideoPriceDto(double? Amount, string Unit, bool Approx, string Source, int? Eta, int? QueueLength);

public sealed record VideoQuoteResponse(
    string QuoteId,
    string Provider,
    string Model,
    int Count,
    int DurationSec,
    VideoPriceDto Price,
    string License,
    bool Heavy,
    DateTime ExpiresAt);

// Котировка соседа при отказе поставщика: запускает её только человек, сервер на соседа сам не
// переходит никогда. Reason — почему предложили
public sealed record RetryQuote(string Provider, string Model, VideoQuoteResponse Quote, string Reason);

public sealed record VideoLaunchRequest(
    string QuoteId,
    string SessionId,
    string SceneId,
    string? Initiator = null,
    JsonObject? Params = null,
    long? Seed = null);

// Либо JobId, либо Error с кодом (VideoEditorErrors.*). Retry — котировка соседа у отказа поставщика
public sealed record VideoLaunchResult(string? JobId, string? ErrorCode, string? Error, RetryQuote? Retry);

// «Сохранить сцену» в проект: версия → файл video/<фильм>/scene-NN.mp4 (перезаписи нет → .v2.mp4)
// вместе с кадрами-нитями в кадры/. Реализация — блок 2
public sealed record SaveSceneRequest(
    string SessionId,
    string SceneId,
    string? VersionId,
    string? Folder,
    string? FileName);

public sealed record SaveSceneResult(string Path, IReadOnlyList<string> FramePaths, bool AddedToFilm);
