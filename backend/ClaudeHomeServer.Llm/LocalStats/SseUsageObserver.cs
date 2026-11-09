using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Итог наблюдателя SSE-потока (RequestUsage): токены, TTFT, длительность/скорость генерации
// и попадание в кэш. Числа nullable — движок не отдал поле (на экране «—»).
public sealed record RequestUsage
{
    // Итоговый input: из message_delta (перекрытие), иначе из message_start.
    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    // cache_read_input_tokens; null — движок поле не отдал (не 0).
    public long? CacheReadTokens { get; init; }

    public long? CacheCreationTokens { get; init; }

    // От старта запроса до ПЕРВОГО content_block_delta (thinking-дельты тоже считаются).
    public TimeSpan? Ttft { get; init; }

    // ПОСЛЕДНЯЯ дельта − ПЕРВАЯ дельта (длительность генерации).
    public TimeSpan? GenDuration { get; init; }

    // OutputTokens / GenDuration; null при нулевой/отсутствующей длительности (одна дельта).
    public double? GenTps { get; init; }

    // cache_read / (input + cache_read + cache_creation); null, если знаменатель 0 или
    // движок не отдал cache_read_input_tokens.
    public double? CacheHitPerc { get; init; }
}

// Наблюдатель SSE-потока (vLLM, Anthropic-совместимый /v1/messages): ведёт состояние ОДНОГО
// запроса и по Complete() отдаёт итог RequestUsage (токены, TTFT, скорость, попадание в кэш).
//
// Принимает строки SSE КУСКАМИ (кусок может прийти посреди строки): незавершённая строка
// остаётся в буфере до следующего куска. Класс НЕ меняет и не буферизует исходный поток —
// читает только копию кусков (склейка/буферизация потока — шаг 16, не здесь).
//
// Устойчивость: мусорные строки, `event:` без `data:`, JSON-обрыв, `[DONE]`, CRLF — не
// бросают исключений (метрика теряется, наблюдатель остаётся в рабочем состоянии).
public sealed class SseUsageObserver
{
    private readonly TimeProvider _time;

    // Текущая (незавершённая) строка: кусок может закончиться посреди неё.
    // Потолок 64 КБ (по аналогии с cgroup-потолком ccs-agents.slice): при превышении
    // наблюдатель «отравляется» — дальнейшие Feed игнорируются, Complete отдаёт нули.
    private readonly StringBuilder _line = new();
    private const int MaxLineLength = 64 * 1024;
    // Потолок накопителя многострочной `data:`: 256 КБ. Злоупотребление (один гигантский
    // data: без переводов строк) не должно раздувать память наблюдателя.
    private const int MaxDataLength = 256 * 1024;
    private bool _poisoned;

    // SSE-событие: `event:`-строка (без значения) и накопленный `data:` (мульти-строковый).
    private string? _eventType;
    private readonly StringBuilder _data = new();

    // Потоковый декодер UTF-8: хвост многобайтового символа (кириллица) держится между
    // кусками, не рвётся на границе буфера. Буфер символов под максимальный кусок (16 КБ).
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _decodeBuffer = new char[16 * 1024];

    // Для тестов (InternalsVisibleTo): весь декодированный ввод — проверка, что многобайтовые
    // символы не рвутся на границе 1-байтовых кусков.
    internal readonly StringBuilder DecodedInput = new();

    // Usage: input из message_start, перекрытие из message_delta; остальные поля — из message_delta.
    private long? _inputFromStart;
    private long? _inputFromDelta;
    private long? _output;
    private long? _cacheRead;
    private long? _cacheCreation;

    // Время дельт: TTFT (старт → первая дельта) и GenDuration (последняя − первая).
    // requestStart — момент создания наблюдателя (= момент получения заголовков ответа: шлюз
    // создаёт наблюдатель сразу после SendAsync с ResponseHeadersRead). НЕ время события
    // message_start: vLLM шлёт message_start вместе с первым токеном (после prefill, в том же
    // TCP-батче), и ttft = firstDelta − message_start ≈ 0. Зафиксировано замером: заголовки
    // 0.01с, message_start+первый дельта 0.45с.
    private DateTimeOffset? _requestStart;
    private DateTimeOffset? _firstDelta;
    private DateTimeOffset? _lastDelta;
    // Оценка накопленных токенов запроса: на каждую content_block_delta прибавляем оценку
    // числа токенов в тексте дельты (1 токен ≈ 4 символа, минимум 1). НЕ число дельт: при
    // DFlash одна дельта несёт 2–8 токенов, и счётчик дельт × глобальный коэффициент
    // (TokensPerStep) систематически ошибался. Оценка по тексту — per-request, не глобальная.
    private int _tokenEstimate;

    // M5: колбэк на каждую content_block_delta с текущим _deltaCount (≈ число токенов
    // запроса). Шлюз подписывает его на метод счётчика хода (OnTokenCount). Все сбои
    // колбэка глотаются наблюдателем (Feed не рвёт поток клиента).
    public Action<int>? OnTokenCount;

    public SseUsageObserver(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        // TTFT-база = момент создания наблюдателя (заголовки получены). message_start не
        // перекрывает: ??= в OnMessageStart видит уже установленное значение.
        _requestStart = _time.GetUtcNow();
    }

    // true — пришла хотя бы одна content_block_delta (первый токен). Шлюз по этому флагу
    // сообщает приёмнику «пришёл первый токен» (фаза «читает промпт → генерирует»).
    public bool SawFirstDelta => _firstDelta is not null;

    // Принимает кусок SSE (строка; байты — через Encoding.UTF8). Кусок может закончиться
    // посреди строки — незавершённая часть остаётся в _line до следующего куска.
    // При превышении потолка MaxLineLength наблюдатель «отравляется»: дальнейшие Feed
    // игнорируются, Complete отдаёт нулевой итог (все поля null).
    public void Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || _poisoned) return;
        DecodedInput.Append(chunk);
        _line.Append(chunk);

        // Потолок: одна «строка» (между \n) не должна превышать MaxLineLength.
        if (_line.Length > MaxLineLength)
        {
            _poisoned = true;
            return;
        }

        // Разбираем по завершённым строкам; последняя (без \n) остаётся в _line.
        while (true)
        {
            var idx = IndexOfNewline(_line);
            if (idx < 0) break;
            var line = _line.ToString(0, idx);
            _line.Remove(0, idx + 1);
            ProcessLine(line);
        }
    }

    // Принимает кусок SSE БАЙТАМИ: потоковое UTF-8-декодирование (System.Text.Decoder)
    // корректно держит хвост многобайтового символа (кириллица) между кусками — в отличие
    // от Encoding.GetString по кускам, который рвёт символ на границе буфера.
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || _poisoned) return;
        // flush:false — хвост незавершённого многобайтового символа остаётся в декодере.
        _decoder.Convert(bytes, _decodeBuffer, flush: false, out _, out var charsUsed, out _);
        if (charsUsed > 0)
            Feed(new string(_decodeBuffer, 0, charsUsed));
    }

    // Завершение запроса: возвращает итог RequestUsage.
    // При «отравлении» (потолок _line) — нулевой итог (все поля null).
    public RequestUsage Complete()
    {
        if (_poisoned)
            return new RequestUsage();

        // Остаток в _line (последняя строка без \n) тоже обрабатываем.
        if (_line.Length > 0)
        {
            ProcessLine(_line.ToString());
            _line.Clear();
        }

        var input = _inputFromDelta ?? _inputFromStart;
        var cacheRead = _cacheRead;

        // GenDuration: от старта запроса (заголовки) до последней дельты — включает prefill.
        // НЕ «последняя − первая дельта»: при DFlash дельты приходят пачками (несколько токенов
        // за дельту), и окно «первый→последний дельта» короче реального времени генерации,
        // завышая genTps. С requestStart (момент заголовков, см. конструктор) genTps =
        // output_tokens / (prefill + decode) — та же «воспринимаемая» скорость, что живая.
        TimeSpan? genDuration = null;
        if (_requestStart is not null && _lastDelta is not null)
            genDuration = _lastDelta - _requestStart;

        double? genTps = null;
        if (genDuration is { } gd && gd > TimeSpan.Zero && _output is { } outTok && outTok > 0)
            genTps = outTok / gd.TotalSeconds;

        // TTFT: от старта запроса до первой дельты.
        TimeSpan? ttft = null;
        if (_requestStart is not null && _firstDelta is not null)
            ttft = _firstDelta - _requestStart;

        // CacheHitPerc: cache_read / (input + cache_read + cache_creation).
        double? cacheHit = null;
        if (cacheRead is not null && input is not null)
        {
            var denom = input + (cacheRead ?? 0) + (_cacheCreation ?? 0);
            if (denom > 0)
                cacheHit = (double)(cacheRead ?? 0) / denom;
        }

        return new RequestUsage
        {
            InputTokens = input,
            OutputTokens = _output,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = _cacheCreation,
            Ttft = ttft,
            GenDuration = genDuration,
            GenTps = genTps,
            CacheHitPerc = cacheHit,
        };
    }

    // Индекс первого символа перевода строки (\n или \r); -1 — нет.
    private static int IndexOfNewline(StringBuilder sb)
    {
        for (var i = 0; i < sb.Length; i++)
        {
            var c = sb[i];
            if (c == '\n' || c == '\r') return i;
        }
        return -1;
    }

    // Одна завершённая строка SSE. Пустая строка — граница события: dispatch накопленного.
    private void ProcessLine(string line)
    {
        var trimmed = line.TrimEnd('\r');
        if (trimmed.Length == 0)
        {
            Dispatch();
            return;
        }
        if (trimmed.StartsWith("event:", StringComparison.Ordinal))
        {
            _eventType = trimmed["event:".Length..].Trim();
            return;
        }
        if (trimmed.StartsWith("data:", StringComparison.Ordinal))
        {
            var payload = trimmed["data:".Length..].TrimStart();
            if (_data.Length > 0) _data.Append('\n');
            _data.Append(payload);
            // Потолок на накопитель data:: один гигантский data: не должен раздувать память.
            if (_data.Length > MaxDataLength)
                _poisoned = true;
            return;
        }
        // `:` (комментарий SSE) и прочий мусор — игнорируем.
    }

    // Граница события: разбираем накопленный data: (если есть) и сбрасываем буферы.
    private void Dispatch()
    {
        var data = _data.Length > 0 ? _data.ToString() : null;
        var evt = _eventType;
        _data.Clear();
        _eventType = null;
        if (data is null) return;
        TryProcessEvent(evt, data);
    }

    // Разбор одного SSE-события. Мусор/обрыв — без исключений (метрика теряется).
    private void TryProcessEvent(string? evt, string data)
    {
        if (data == "[DONE]") return;

        JsonElement? el;
        try
        {
            el = JsonSerializer.Deserialize<JsonElement>(data);
        }
        catch
        {
            return; // JSON-обрыв/мусор — событие теряется, наблюдатор жив.
        }
        if (el is null) return;
        var e = el.Value;
        if (e.ValueKind != JsonValueKind.Object) return;

        // Тип события: из поля "type" JSON, иначе из `event:`-строки.
        var type = e.TryGetProperty("type", out var t) ? t.GetString() : evt;

        switch (type)
        {
            case "message_start":
                OnMessageStart(e);
                break;
            case "content_block_delta":
                OnContentBlockDelta(e);
                break;
            case "message_delta":
                OnMessageDelta(e);
                break;
            // content_block_start/stop, message_stop и прочее — для usage не нужны.
        }
    }

    // message_start: input_tokens. TTFT-база (_requestStart) уже зафиксирована в
    // конструкторе (момент заголовков); ??= — страховка, если наблюдатель создан иначе.
    private void OnMessageStart(JsonElement e)
    {
        _requestStart ??= _time.GetUtcNow();
        if (e.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("input_tokens", out var inTok)
            && inTok.TryGetInt64(out var v))
        {
            _inputFromStart = v;
        }
    }

    // content_block_delta: фиксируем время дельты (TTFT — первая, GenDuration — первая/последняя).
    // M5: колбэк OnTokenCount — оценка накопленных токенов запроса (по тексту дельты) для
    // живой скорости. 1 токен ≈ 4 символа (эвристика для латиницы/кириллицы); минимум 1 на
    // дельту (пустая дельта всё равно несёт ≥1 токен). Per-request, не глобальный TokensPerStep.
    private void OnContentBlockDelta(JsonElement e)
    {
        var now = _time.GetUtcNow();
        if (_firstDelta is null)
            _firstDelta = now;
        _lastDelta = now;
        _tokenEstimate += EstimateTokens(e);
        try { OnTokenCount?.Invoke(_tokenEstimate); }
        catch { /* сбой колбэка не рвёт поток: клиент уже получил байты */ }
    }

    // Оценка числа токенов в дельте по длине текста: 1 токен ≈ 4 символа, минимум 1.
    // text_delta/thinking_delta → delta.text; без текста (например, tool_use) → 1.
    private static int EstimateTokens(JsonElement e)
    {
        if (e.TryGetProperty("delta", out var delta)
            && delta.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            var len = text.GetString()?.Length ?? 0;
            return Math.Max(1, len / 4);
        }
        return 1;
    }

    // message_delta: usage ПЕРЕКРЫВАЕТ message_start (input), плюс output/cache_read/cache_creation.
    private void OnMessageDelta(JsonElement e)
    {
        if (e.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var inTok) && inTok.TryGetInt64(out var v))
                _inputFromDelta = v;
            if (usage.TryGetProperty("output_tokens", out var outTok) && outTok.TryGetInt64(out var v2))
                _output = v2;
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt64(out var v3))
                _cacheRead = v3;
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt64(out var v4))
                _cacheCreation = v4;
        }
    }
}
