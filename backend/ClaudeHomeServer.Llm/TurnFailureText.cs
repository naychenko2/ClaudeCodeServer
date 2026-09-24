using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Llm;

// Единственная точка человеческих формулировок сбоя хода. Сырые тексты .NET-исключений
// («Идет закрытие канала.», «Cannot access a disposed object…») и английские тексты CLI
// («API Error: 529 Overloaded…») в ленту чата не попадают — они уходят в Details ошибки и в
// лог сервера, а человек читает отсюда. Функции чистые: ни логов, ни состояния.
public static class TurnFailureText
{
    // Общий сбой хода на нашей стороне: причину человек всё равно не починит, поэтому
    // формулировка одна на все неопознанные исключения.
    public const string Generic =
        "Ход прервался — что-то пошло не так на стороне сервера. Отправьте сообщение ещё раз.";

    // Запись в stdin закрывающегося CLI: новое сообщение пришло, пока предыдущий ход
    // ещё сворачивался. Единственный случай, где человеку есть что сделать осмысленно.
    public const string PipeClosing =
        "Ход прервался — предыдущий ответ ещё завершался, когда пришло новое сообщение. Отправьте его ещё раз.";

    // Перегрузка провайдера (529/overloaded/5xx). Показывается только когда подмены модели
    // следом НЕ было — иначе ход состоялся, и хватает спокойного маркера подмены.
    public const string Overloaded =
        "Модель перегружена — сервис временно не справляется. Попробуйте через минуту.";

    // Нет выхода в сеть: исходящий прокси не отвечает, поэтому запрос не дошёл НИ ДО ОДНОЙ
    // модели. Формулировка уводит от ложного вывода «сервис не отвечает → сменю модель»:
    // смена модели здесь не лечит ничего, все они ходят наружу одним каналом.
    public const string EgressDown =
        "Нет связи с интернетом — запрос не дошёл ни до одной модели.\n\n"
        + "Проверьте соединение (прокси/VPN) и отправьте сообщение ещё раз. "
        + "Смена модели тут не поможет: наружу они ходят одним каналом.";

    // Локальная модель не поднята (vLLM/llama.cpp выключен, порт не слушает или модель
    // в статусе unloaded). Pre-flight проба LocalEndpointProbe сняла состояние ДО хода,
    // поэтому идти по цепочке смысла нет — соседний шаг сменит провайдера, но не эндпоинт.
    // Формулировка по образцу EgressDown: смена модели не лечит, эндпоинт один.
    public const string LocalModelDown =
        "Локальная модель не запущена — ход на ней не выполнится.\n\n"
        + "Проверьте, поднят ли движок (vLLM/llama.cpp) и загружена ли модель, "
        + "и отправьте сообщение ещё раз. "
        + "Смена модели не поможет: локальный эндпоинт один и тот же.";

    // Окно 1M недоступно: суффикс [1m] тир-алиаса запрошен, живых подписок с доступом к нему
    // нет, и ни один шаг цепочки не смог подхватить ход. Раньше тут был тихий срез в 200K —
    // для длинного чата это мина (контекст сотни тысяч токенов в 200K не влезет, человек
    // получит переполнение вместо причины). Текст не утверждает, что разговор НЕ поместится
    // в 200K: размера разговора мы в этот момент не знаем, а чат на десять тысяч токенов
    // поместится прекрасно — прежняя формулировка была ровно тем враньём, которое эта ветка
    // и лечит. Решение оставлено человеку: кнопка «продолжить в стандартном окне» под
    // карточкой (POST /api/chats/{id}/window-1m/drop).
    public const string Window1MUnavailable =
        "Окно 1M сейчас недоступно: ни у одной подписки нет к нему доступа, и остальные модели цепочки тоже не подошли.\n\n"
        + "Дождитесь сброса лимита или продолжите чат на другом провайдере. "
        + "Если разговор короткий, его можно продолжить в стандартном окне 200 тысяч токенов — кнопка ниже.";

    // Локальный проект (ADR-016 §2): через шлюз ходят только подписки на `claude setup-token`,
    // а живого такого аккаунта в пуле нет. На API-ключ ход сам не переходит — это решает владелец.
    public const string LocalProjectsNeedSetupToken =
        "Для локальных проектов нужна подписка на `claude setup-token`: живого аккаунта с таким токеном сейчас нет.\n\n"
        + "Подключите подписку через `claude setup-token` в настройках пула или выберите модель стороннего провайдера.";

    // Тумблер LlmGateway:AllowSubscriptions выключен: подписки через шлюз не ходят вовсе.
    public const string GatewaySubscriptionsDisabled =
        "Подписки Claude через шлюз локальных проектов выключены администратором.\n\n"
        + "Выберите модель стороннего провайдера или попросите включить подписки.";

    // Модель хода резолвится в сторонний провайдер, но у него нет ключа или адреса.
    public const string GatewayProviderNotConfigured =
        "Провайдер выбранной модели не настроен на сервере, ход через шлюз не пройдёт.";

    // Перерасход командной строки: ClaudeSession не смог стартовать процесс CLI
    // (Win32 ERROR_FILENAME_EXCED_RANGE, код 206) — итоговая длина аргументов
    // (--append-system-prompt + --resume + --mcp-config + путь к exe)
    // перевалила лимит Windows в 32 767 символов. Срезка нестабильных секций уже применена,
    // иначе ход вообще не ушёл бы в процесс; остаток — стабильные секции и слой персоны,
    // которые под нож не идут. Сменить модель бесполезно: длина промпта от выбора пары
    // не зависит. По образцу EgressDown/LocalModelDown: формулировка уводит от ложного
    // вывода «сервис не отвечает → сменю модель», иначе фолбэк крутил бы 5 пар впустую
    // (инцидент 2026-09-07, чат 74f1c3d6).
    public const string PromptOverflow =
        "Промпт хода превысил лимит командной строки — системный промпт и обвязка не " +
        "помещаются в вызов процесса даже после срезки нестабильных секций.\n\n"
        + "Сократите объём системного промпта: уберите или упростите контракт персоны, " +
        "снизьте число привязок (базы знаний, заметки, MCP-серверы), уберите материалы " +
        "контекста чата и код-граф — всё это входит в промпт. "
        + "Деление чата и смена модели не помогут: длина промпта от них не зависит.";

    // Локаль-независимые коды закрывающегося именованного канала Windows:
    // ERROR_NO_DATA (232, «Идет закрытие канала») и ERROR_BROKEN_PIPE (109).
    private const int ErrorNoDataHResult = unchecked((int)0x800700E8);
    private const int ErrorBrokenPipeHResult = unchecked((int)0x8007006D);

    // Те же ситуации фразами: HResult приходит не всегда (Linux-раннер, обёрнутые исключения),
    // а формулировка зависит от языка системы — держим и русскую, и английские.
    private static readonly string[] PipePhrases =
    [
        "закрытие канала",
        "pipe is being closed",
        "pipe has been ended",
        "broken pipe",
        "pipe is broken",
    ];

    // Текст для ленты по пойманному исключению хода. Разворачиваем обёртки
    // (AggregateException/TargetInvocation), чтобы не пропустить IOException внутри.
    public static string ForException(Exception? ex)
    {
        foreach (var inner in Unwrap(ex))
        {
            if (inner is IOException io && LooksLikePipeClosing(io))
                return PipeClosing;
            // Отказ устройства локального проекта («… офлайн — ход не запущен.») — текст уже
            // человеческий и единственный, по которому понятно, что включить
            if (inner is Execution.DeviceExecRefusedException refused)
                return refused.Message;
        }
        return Generic;
    }

    // Текст для ленты по сырому тексту ошибки CLI. null — формулировки нет: вызывающий
    // оставляет сырой текст видимым (лучше непонятный, но настоящий текст, чем выдуманный).
    // CLI не нашёл разговор для --resume там, где запущен: транскрипт убрала плановая уборка
    // CLI или он живёт на другой машине (у локального проекта — только на его устройстве,
    // ADR-016). Сервер --resume не снимает и чат с чистого листа молча не начинает: решение
    // о потере контекста — за человеком. Смена модели не лечит — resume тот же.
    public const string ResumeTranscriptMissing =
        "Память этого разговора не найдена там, где запускается модель — ход не выполнен.\n\n"
        + "Транскрипт CLI удалён или остался на другой машине (у локального проекта он живёт только на его устройстве). "
        + "Продолжить с прежним контекстом нельзя — начните новый чат.";

    // Маркер отказа CLI в result.errors: «No conversation found with session ID: <id>»
    private const string NoConversationMarker = "No conversation found with session ID";

    // Человеческий текст по списку result.errors CLI (subtype error_during_execution): null —
    // среди ошибок нет распознанной, показывать как есть
    public static string? ForResultErrors(IEnumerable<string> errors) =>
        errors.Any(e => e.Contains(NoConversationMarker, StringComparison.OrdinalIgnoreCase))
            ? ResumeTranscriptMissing
            : null;

    public static string? ForCliError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Contains("overload", StringComparison.OrdinalIgnoreCase)) return Overloaded;
        foreach (var phrase in ServerErrorPhrases)
            if (raw.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return Overloaded;
        // Код 5xx в тексте («API Error: 529 …»): 529 у Anthropic — та же перегрузка.
        return HttpServerErrorCode.IsMatch(raw) ? Overloaded : null;
    }

    // Канонические reason phrases 5xx — по образцу TurnErrorClassifier.ProviderErrorPhrases
    private static readonly string[] ServerErrorPhrases =
    [
        "internal server error",
        "bad gateway",
        "service unavailable",
        "gateway timeout",
    ];

    // Код 5xx отдельным числом: «529», «(503)», «Error 500:» — но не кусок «1529»/«5000»
    private static readonly Regex HttpServerErrorCode =
        new(@"(?<!\d)5\d{2}(?!\d)", RegexOptions.Compiled);

    private static bool LooksLikePipeClosing(IOException io)
    {
        if (io.HResult is ErrorNoDataHResult or ErrorBrokenPipeHResult) return true;
        foreach (var phrase in PipePhrases)
            if (io.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Само исключение плюс его вложенные (включая ветки AggregateException)
    private static IEnumerable<Exception> Unwrap(Exception? ex)
    {
        while (ex is not null)
        {
            yield return ex;
            if (ex is AggregateException agg)
            {
                foreach (var branch in agg.InnerExceptions)
                    foreach (var nested in Unwrap(branch))
                        yield return nested;
                yield break;
            }
            ex = ex.InnerException;
        }
    }
}
