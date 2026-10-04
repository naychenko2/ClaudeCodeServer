namespace ClaudeHomeServer.Services.Media;

// Шов «локальная съёмка видео» для модуля «Видео» (ADR-022 §3): движок local-media (ComfyUI на своей GPU,
// шаблон ImageToVideo с узлом last_frame) живёт в вертикали Images, модуль на неё не ссылается.
// Реализация — адаптер в Images (напрямую в ComfyClient, мимо LocalMediaCollector); нет подсистемы images —
// нет и регистрации. Шов байтовый, в папку проекта ничего не пишет. Ticket — prompt_id ComfyUI: он приходит
// только от драйвера в процессе, снаружи его не передают.
public interface ILocalVideoMedia
{
    // Тумблеры local-media включены и ComfyUI отвечал недавно. Не блокирует: проверка живости кешируется
    bool Available { get; }

    // Тумблеры включены — поставщик заведён, даже если ComfyUI сейчас лежит
    bool Configured { get; }

    // Длина общей очереди ComfyUI (идущая плюс ждущие, с чужими прогонами); null — недоступен
    Task<int?> QueueLengthAsync(CancellationToken ct);

    // Ожидаемое время съёмки по таблице замеров local-media; null — не замерено или запрос неверен
    int? EtaSeconds(LocalVideoRequest request);

    Task<LocalVideoSubmitted> SubmitAsync(LocalVideoRequest request, CancellationToken ct);

    Task<LocalVideoPoll> PollAsync(string ticket, CancellationToken ct);

    // Снять задачу: ждущую — из очереди, идущую — прервать адресно по её prompt_id. Задачи в очереди нет — false
    Task<bool> CancelAsync(string ticket, CancellationToken ct);
}

// FirstFrame — обязательный первый кадр (байты картинки), LastFrame — последний (узел last_frame).
// Seconds — длительность (потолок — LocalMediaService.MaxVideoSeconds), Size — "full" | "half", Fast — ускоренный режим
public sealed record LocalVideoRequest(
    string Prompt,
    byte[] FirstFrame,
    byte[]? LastFrame = null,
    int Seconds = 5,
    string Size = "full",
    bool? Fast = null,
    long? Seed = null);

// Busy — очередь GPU занята или ComfyUI не ответил: «попробуй позже», а не ошибка запроса
public sealed record LocalVideoSubmitted(string? Ticket, int? QueuePosition, int? EtaSeconds, string? Error, bool Busy = false)
{
    public static LocalVideoSubmitted Fail(string error, bool busy = false) => new(null, null, null, error, busy);
}

public enum LocalVideoState { Queued, Running, Completed, Failed }

// Warning — временный сбой опроса (ComfyUI не ответил), задача при этом жива; Percent — доля шагов сэмплера 0..1
// по прогрессу ComfyUI (null — данных нет)
public sealed record LocalVideoPoll(
    LocalVideoState State,
    int? QueuePosition,
    LocalVideoFile? File,
    string? Error,
    string? Warning = null,
    double? Percent = null);

// Extension — с точкой («.mp4»); HasSound — в ролике есть звуковая дорожка
public sealed record LocalVideoFile(byte[] Bytes, string ContentType, string Extension, bool HasSound);
