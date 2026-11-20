namespace ClaudeHomeServer.Services.Llm;

// Скорость ответа локальной модели (фича «Скорость ответа», docs/features/llm-answer-speed-spec.md):
// шлюз LLM сообщает каждый запрос к локальному движку — момент отправки вверх, момент первого
// токена, момент завершения и выходные токены из usage. Сборщик копит это по дорожкам статистики
// и отдаёт агрегаты за окна 5m/1h для панели «Локальные модели».
//
// Определение — сквозное, как публикуют Artificial Analysis и OpenRouter: полное время запроса
// от отправки до последнего токена (время до первого токена в него входит). «Скорость, пока
// пишет» (GenTps наблюдателя SseUsageObserver) остаётся отдельной справочной метрикой — здесь её нет.
//
// Окна half-open: в [now−окно, now] входят запросы, ЗАВЕРШЁННЫЕ в нём; ровно на границе
// now−окно — входит, на такт раньше — нет. Нет данных — null, не 0 (контракт REST).
public interface ILocalModelAnswerSpeed
{
    // Запрос пошёл вверх. startedAt — момент НЕПОСРЕДСТВЕННО ПЕРЕД отправкой запроса апстриму:
    // полное время запроса включает установку соединения и ожидание заголовков.
    // track — дорожка статистики (LocalModelStatsPoller.MainTrackName или имя именованного движка).
    void OnRequestStarted(string requestId, string track, DateTimeOffset startedAt);

    // Пришёл первый токен (первая content_block_delta).
    void OnFirstToken(string requestId, DateTimeOffset at);

    // Запрос завершён: outputTokens — из usage ответа (null — движок usage не отдал).
    void OnRequestCompleted(string requestId, long? outputTokens, DateTimeOffset completedAt);

    // Агрегаты дорожки за окна 5m и 1h. Пустая дорожка — окна с null-метриками и requests 0,
    // а не null-ответ: блок на REST есть всегда, когда сборщик есть.
    AnswerSpeedSnapshot SnapshotOf(string track);
}

// Агрегаты одного окна. Метрики nullable: нет данных — null, не 0.
public sealed record AnswerSpeedWindow
{
    // Имя окна на wire: "5m" | "1h".
    public required string Window { get; init; }

    // Сквозная скорость, ток/с: Σ токенов ÷ Σ полного времени запросов окна. В суммы идут только
    // запросы с известными токенами и ненулевой длительностью; таких нет — null.
    public double? EndToEndTps { get; init; }

    // Медиана tokens/duration по запросам с tokens ≥ MinTokensForMedian (короткие ответы сильно
    // занижают оценку).
    public double? MedianTps { get; init; }

    // Время до первого токена, с: медиана и 90-й перцентиль по всем запросам с первым токеном.
    public double? MedianTtftSec { get; init; }
    public double? P90TtftSec { get; init; }

    // Число запросов, завершённых в окне.
    public int Requests { get; init; }
}

// Снимок агрегатов дорожки: оба окна сразу и порог токенов, которым пользовалась медиана
// (фронт подписывает им число в подсказке).
public sealed record AnswerSpeedSnapshot
{
    public required IReadOnlyList<AnswerSpeedWindow> Windows { get; init; }
    public int MinTokensForMedian { get; init; }
}
