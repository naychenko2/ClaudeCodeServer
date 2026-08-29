using System.Text;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Тонкая обёртка над локальным LLM. Сегодня реализации две — Ollama (диалект /api/chat)
// и llama-server (OpenAI-совместимый /v1/chat/completions). Маршрутизация «какое действие
// идёт на локаль» живёт в LocalActionRouter и LocalActionOverridesStore; здесь — только
// «как разговаривать с выбранным движком». Раньше на этом месте был класс OllamaClient;
// теперь он стал одной из реализаций ILocalLlmClient, а потребители (CheapTextRunner,
// LocalActionRouter, SessionManager) держат интерфейс и не знают, с кем говорят.
//
// Конфиг читает ЕДИНСТВЕННАЯ точка LocalLlmOptions.Read(IConfiguration): каждое поле
// берёт LocalLlm:X → при отсутствии Ollama:X → дефолт. Так старые тесты, которые
// выключают локаль через ["Ollama:Model"] = "", продолжают работать без правок.

public interface ILocalLlmClient
{
    bool Enabled { get; }
    string BaseUrl { get; }
    string Model { get; }
    string TextModel { get; }
    // "ollama" | "llama-server" — для учёта расхода и UI (OllamaUsageInfo.Provider).
    string ProviderKey { get; }

    // Один синхронный чат-ход со структурированным JSON-выводом. jsonFormat — либо
    // полноценная JSON-схема, либо строка "json" (просто JSON-object). null при любой
    // ошибке/таймауте — вызывающий откатывается на следующий шаг цепочки.
    Task<string?> ChatJsonAsync(
        string systemPrompt, string userPrompt, object jsonFormat, CancellationToken ct = default,
        string? model = null, int? timeoutMs = null, int? numPredict = null, int? numCtx = null,
        string? ownerId = null, string? label = null);

    // Свободнотекстовая генерация (без schema): единый prompt → строка ответа. numCtx —
    // для Ollama; llama-server игнорирует (контекст фиксируется ключом -c при старте).
    Task<string?> GenerateTextAsync(
        string prompt, string? model, TimeSpan timeout, int numPredict, int numCtx,
        string? ownerId = null, string? label = null, CancellationToken ct = default);

    // Один разговорный ход голосового режима: полный messages[] → короткий ответ.
    // onDelta != null — потоковый режим: куски текста уходят вызывающему по границе
    // предложения через общий StreamSentenceBuffer (озвучка стартует, не дожидаясь
    // конца ответа).
    Task<ChatTurnResult> ChatTurnAsync(
        IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
        int numPredict, int numCtx, string? ownerId,
        Func<string, Task>? onDelta = null, CancellationToken ct = default);

    // Прогрев: холостой вызов, чтобы модель загрузилась в память заранее. Best-effort.
    Task WarmUpAsync(CancellationToken ct = default);
}

// Реплика диалога для ChatTurnAsync: role = system|user|assistant. На уровне namespace,
// а не реализации: SessionManager собирает список до того, как знает, какой движок
// выбран.
public sealed record ChatMsg(string Role, string Content);

// Результат разговорного хода: текст ответа (null — пустой/сбойный вызов) и usage
// для ResultMessage ленты. null usage — счётчики движок не отдал.
public sealed record ChatTurnResult(string? Text, UsageInfo? Usage);

// Транспортные настройки локального движка: общая точка чтения для обеих реализаций.
// Приоритет: LocalLlm:{X} → Ollama:{X} → дефолт. Поля Ollama оставлены как фолбэк,
// чтобы старые конфиги (только секция Ollama) продолжали работать без правок.
public sealed record LocalLlmOptions(
    string Provider,
    string BaseUrl,
    string ApiKey,
    string Model,
    string TextModel,
    int TimeoutMs,
    string KeepAlive,
    bool DisableThinking)
{
    // Поддержанные значения ключа Provider. Любая другая строка — fail-open на ollama
    // (без падения конфига): маршрут по-прежнему работает через старый класс.
    public const string Ollama = "ollama";
    public const string LlamaServer = "llama-server";

    public static LocalLlmOptions Read(IConfiguration config)
    {
        // Provider — только из новой секции: Ollama:Provider не существовало, обратной
        // совместимости тут не требуется. Дефолт ollama — старая ветка.
        var provider = (config["LocalLlm:Provider"] ?? Ollama).Trim().ToLowerInvariant();
        if (provider != Ollama && provider != LlamaServer) provider = Ollama;

        // BaseUrl: LocalLlm → Ollama. Дефолт ollama-порта — исторический.
        var baseUrl = (config["LocalLlm:BaseUrl"] ?? config["Ollama:BaseUrl"] ?? "http://localhost:11434")
            .TrimEnd('/');

        // ApiKey — локальная модель llama-server может быть поднята с --api-key.
        var apiKey = config["LocalLlm:ApiKey"] ?? "";

        var model = config["LocalLlm:Model"] ?? config["Ollama:Model"] ?? "";

        // TextModel — опциональная отдельная модель для текстовых действий; пусто → Model.
        var textModel = config["LocalLlm:TextModel"] ?? config["Ollama:TextModel"] ?? "";
        if (string.IsNullOrWhiteSpace(textModel)) textModel = model;

        // KeepAlive — целое (секунды; -1 = вечно) либо duration-строка ("5m").
        var keepAlive = config["LocalLlm:KeepAlive"] ?? config["Ollama:KeepAlive"] ?? "-1";

        var timeoutMs = int.TryParse(config["LocalLlm:TimeoutMs"] ?? config["Ollama:TimeoutMs"], out var t)
            ? t : 4000;

        // DisableThinking — прокидываем в chat_template_kwargs:{enable_thinking:false} у
        // llama-server, чтобы qwen-стиль моделей не уходил в размышления. У Ollama эта
        // галочка уже была жёстко включена через think:false и не настраивалась.
        var disableThinking = !string.Equals(
            config["LocalLlm:DisableThinking"], "false", StringComparison.OrdinalIgnoreCase);

        return new LocalLlmOptions(provider, baseUrl, apiKey, model, textModel, timeoutMs,
            keepAlive, disableThinking);
    }
}

// Накопитель потоковых кусков: флашит по границе предложения либо принудительно по
// порогу символов. Используется обоими движками, чтобы поведение озвучки совпадало —
// различие только в источнике кусков (NDJSON Ollama vs SSE llama-server).
//
// hardFlushChars — порог принудительного флаша; модель, пишущая длинный период без
// точки, держала бы ленту и озвучку в тишине.
public sealed class StreamSentenceBuffer
{
    private const int DefaultHardFlushChars = 40;

    private readonly StringBuilder _full = new();
    private readonly StringBuilder _pending = new();
    private readonly int _hardFlushChars;

    public StreamSentenceBuffer(int hardFlushChars = DefaultHardFlushChars)
    {
        _hardFlushChars = hardFlushChars;
    }

    public string FullText => _full.ToString();

    // Сложить кусок текста. Возвращает true, если надо отдать накопленное вызывающему
    // (по границе предложения или по порогу). fullText накапливается в любом случае.
    public bool Append(string piece)
    {
        if (string.IsNullOrEmpty(piece)) return false;
        _full.Append(piece);
        _pending.Append(piece);
        if (_pending.Length >= _hardFlushChars || EndsSentence(_pending))
            return true;
        return false;
    }

    // Забрать накопленное и сбросить буфер.
    public string Flush()
    {
        if (_pending.Length == 0) return "";
        var chunk = _pending.ToString();
        _pending.Clear();
        return chunk;
    }

    // Конец предложения по последнему непробельному символу накопленного куска.
    private static bool EndsSentence(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            var ch = sb[i];
            if (char.IsWhiteSpace(ch)) continue;
            return ch is '.' or '!' or '?' or '…';
        }
        return false;
    }
}

// Срезать из текста блок <think>…</think> целиком. Страховка для моделей в стиле qwen,
// которые иногда всё равно выдают размышления, несмотря на enable_thinking:false.
public static class ThinkingStripper
{
    private static readonly System.Text.RegularExpressions.Regex Pattern =
        new(@"<think>[\s\S]*?</think>\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Strip(string text) =>
        string.IsNullOrEmpty(text) ? text : Pattern.Replace(text, "");
}
