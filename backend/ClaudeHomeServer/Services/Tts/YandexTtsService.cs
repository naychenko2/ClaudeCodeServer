using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Tts;

// Синтез речи через Yandex SpeechKit API v3 (tts/v3/utteranceSynthesis) — озвучка ответов
// в голосовом режиме чата. Без ключа/folderId — IsConfigured=false, контроллер отдаёт 503
// и фронт уходит на голос браузера (speechSynthesis).
//
// Почему v3, а не v1 (тот отдавал готовый mp3 и переваривал 5000 символов за раз): в v3
// вдвое больше голосов, и при упаковке кусков он ещё и дешевле — точка безубыточности
// 121 символ, разбор в docs/research/speechkit-pricing.md §4. Цена перехода — лимит
// запроса 249 символов (замерено) и ответ не бинарём, а строками JSON с base64 внутри.
//
// Авторизация прежняя: Api-Key сервисного аккаунта с ролью ai.speechkit-tts.user (STT-роли
// недостаточно — при живом ключе будет 403).
// Итог синтеза: аудио (null — не вышло) и то, за сколько запросов Яндекс уже выставит счёт.
// Rub — те же запросы в рублях по цене из конфига, чтобы прайс знало ОДНО место.
public sealed record TtsResult(byte[]? Audio, int BilledRequests, double Rub)
{
    public static readonly TtsResult Nothing = new(null, 0, 0);
}

public class YandexTtsService(IHttpClientFactory http, IConfiguration config, ILogger<YandexTtsService> logger)
{
    public const string HttpClientName = "yandex-tts";
    private const string Endpoint = "https://tts.api.cloud.yandex.net/tts/v3/utteranceSynthesis";
    // Лимит одного запроса v3 — 249 символов (250 уже даёт 400 «Too long text»): длиннее
    // режем по предложениям и склеиваем. Фронт пакует куски под тот же потолок, так что
    // сюда обычно приезжает готовый пакет, а нарезка остаётся страховкой
    internal const int MaxCharsPerRequest = 249;

    private readonly string? _apiKey = config["Yandex:SpeechKit:ApiKey"];
    private readonly string? _folderId = config["Yandex:SpeechKit:FolderId"];
    // Цена одного запроса v3 в рублях: прайс Яндекса живёт своей жизнью, поэтому он в конфиге,
    // а не в коде. Дефолт — 0,1626 ₽ (снято 20.08.2026, docs/research/speechkit-pricing.md §1)
    private readonly double _rubPerRequest =
        config.GetValue<double?>("Yandex:SpeechKit:RubPerRequest") ?? 0.1626;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_folderId);

    // mp3-байты синтезированной речи; null — синтез не удался (не настроен, Яндекс ответил
    // ошибкой или недоступен). Причина уже в логе — вызывающему хватает «не вышло» для 502.
    //
    // BilledRequests — сколько запросов Яндекс успел принять и, значит, затарифицировать:
    // тарификация идёт ЗА ЗАПРОС, и куски, ушедшие до обрыва на середине, оплачены, даже
    // если целиком озвучка провалилась и Audio здесь null. Молчать о них — занижать счёт.
    // В счётчик попадают только подтверждённые ответы 2xx: отказ (400/403/5xx) не тарифицируется,
    // а про оборванный по таймауту запрос мы попросту не знаем — гадать хуже, чем недосчитать
    // одну единицу, и эта неопределённость здесь единственная.
    //
    // Голос приходит готовым (VoiceResolver): здесь он уже проверен по белому списку, роль
    // заведомо поддерживается этим голосом, скорость в границах. Второй раз не проверяем —
    // иначе получим два источника правды о том, чем говорит персона.
    public async Task<TtsResult> SynthesizeAsync(string text, VoiceChoice voice, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(text)) return TtsResult.Nothing;

        var client = http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(30);

        var billed = 0;
        using var result = new MemoryStream();
        foreach (var chunk in SplitForSynthesis(text, MaxCharsPerRequest))
        {
            byte[]? bytes;
            bool accepted;
            try
            {
                (bytes, accepted) = await SynthesizeChunkAsync(client, chunk, voice, ct);
            }
            catch (OperationCanceledException)
            {
                // Клиент ушёл (закрыл вкладку, нажал «Стоп»): наружу это уходит обычным
                // отказом, а не исключением — иначе вызывающий не успеет записать расход,
                // и уже оплаченные запросы просто исчезнут из учёта
                return new TtsResult(null, billed, Rub(billed));
            }
            // Принятый запрос оплачен независимо от того, вытащили ли мы из ответа аудио
            if (accepted) billed++;
            // причина в логе; полкуска озвучки хуже честного фолбэка, но уже оплаченные
            // запросы уезжают вызывающему — их посчитает учёт расхода
            if (bytes is null) return new TtsResult(null, billed, Rub(billed));
            result.Write(bytes);
        }
        return result.Length > 0
            ? new TtsResult(result.ToArray(), billed, Rub(billed))
            : new TtsResult(null, billed, Rub(billed));
    }

    // Рубли за N запросов; округление до копеек — иначе в JSONL расхода поедут хвосты double
    private double Rub(int requests) => Math.Round(requests * _rubPerRequest, 4);

    // Accepted — Яндекс ответил успехом, то есть запрос затарифицирован (даже если аудио в
    // ответе не оказалось). Отказ и обрыв связи — Accepted=false, см. комментарий у SynthesizeAsync.
    private async Task<(byte[]? Audio, bool Accepted)> SynthesizeChunkAsync(HttpClient client,
        string chunk, VoiceChoice voice, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {_apiKey}");
            req.Headers.TryAddWithoutValidation("x-folder-id", _folderId);
            req.Content = new StringContent(BuildPayload(chunk, voice), Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 403 при живом ключе — почти всегда не тот ключ, а не та РОЛЬ: без отдельного
                // сообщения причина полдня ищется не там (ключ-то работает в STT)
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    logger.LogWarning("SpeechKit отверг синтез (403): ключ жив, но у сервисного аккаунта " +
                                      "нет роли ai.speechkit-tts.user — выдай её в Yandex Cloud.");
                else
                    logger.LogWarning("SpeechKit ответил {Status} на синтез: {Body}",
                        (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
                return (null, false);
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var audio = ExtractAudio(body);
            if (audio is null)
                logger.LogWarning("SpeechKit ответил 200, но пригодного аудио в ответе нет: {Body}",
                    body.Length > 500 ? body[..500] : body);
            return (audio, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            // Недоступность/таймаут Яндекса — штатный случай: строку в лог пишет QuietHttpLogger,
            // здесь только «не вышло»
            return (null, false);
        }
    }

    // Тело запроса v3. hints — массив, где каждый элемент задаёт ОДНУ подсказку: голос,
    // скорость и роль в один объект не сложить.
    private static string BuildPayload(string text, VoiceChoice voice)
    {
        // Роль добавляется только когда она есть: пустая строка в hints — это для SpeechKit
        // не «нейтрально», а ошибка 400
        var hints = new List<object> { new { voice = voice.Voice }, new { speed = voice.Speed } };
        if (!string.IsNullOrWhiteSpace(voice.Role)) hints.Add(new { role = voice.Role });

        return JsonSerializer.Serialize(new
        {
            text,
            outputAudioSpec = new { containerAudio = new { containerAudioType = "MP3" } },
            hints,
            loudnessNormalizationType = "LUFS",
        });
    }

    // Ответ v3 — поток JSON-объектов, по строке на кусок аудио; данные лежат в
    // result.audioChunk.data (base64). Любая неожиданность — строка с ошибкой, строка без
    // аудио, оборванный на середине поток — это отказ ЦЕЛИКОМ: половина фразы в ушах хуже
    // честного фолбэка на голос браузера.
    internal static byte[]? ExtractAudio(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        using var audio = new MemoryStream();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
                if (!result.TryGetProperty("audioChunk", out var piece)) return null;
                if (!piece.TryGetProperty("data", out var data)) return null;
                audio.Write(data.GetBytesFromBase64());
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                return null;
            }
        }
        return audio.Length > 0 ? audio.ToArray() : null;
    }

    // Нарезка текста под лимит одного запроса: по границам предложений, предложение длиннее
    // лимита режется по словам. Чистая internal static функция — иначе её нечем тестировать.
    internal static List<string> SplitForSynthesis(string text, int maxLen)
    {
        var chunks = new List<string>();
        text = text?.Trim() ?? "";
        if (text.Length == 0) return chunks;
        if (text.Length <= maxLen) { chunks.Add(text); return chunks; }

        var sb = new StringBuilder();
        foreach (var sentence in SplitSentences(text))
        {
            if (sentence.Length > maxLen)
            {
                Flush(chunks, sb);
                chunks.AddRange(SplitLongSentence(sentence, maxLen));
                continue;
            }
            if (sb.Length > 0 && sb.Length + 1 + sentence.Length > maxLen) Flush(chunks, sb);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }
        Flush(chunks, sb);
        return chunks;

        static void Flush(List<string> chunks, StringBuilder sb)
        {
            if (sb.Length == 0) return;
            chunks.Add(sb.ToString());
            sb.Clear();
        }
    }

    // Предложение длиннее лимита (модель пишет без точек) режем по последнему пробелу, а не
    // посреди слова: на лимите 249 эта ветка из почти недостижимой стала обычной, и «Провер /
    // ка связи» слышно сразу. Слова длиннее лимита не бывает, но на всякий случай режем жёстко.
    private static IEnumerable<string> SplitLongSentence(string sentence, int maxLen)
    {
        var start = 0;
        while (sentence.Length - start > maxLen)
        {
            var cut = sentence.LastIndexOf(' ', start + maxLen - 1, maxLen);
            if (cut <= start) cut = start + maxLen; // слово длиннее лимита — режем жёстко
            var piece = sentence[start..cut].Trim();
            if (piece.Length > 0) yield return piece;
            start = cut;
            while (start < sentence.Length && sentence[start] == ' ') start++;
        }
        var tail = sentence[start..].Trim();
        if (tail.Length > 0) yield return tail;
    }

    // Предложения по .!?…и переводу строки; хвостовая пачка знаков («?!», «...») не рвётся.
    // Сокращения не разбираем: для нарезки под лимит синтеза лишний разрез безвреден.
    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…' or '\n')) continue;
            while (i + 1 < text.Length && text[i + 1] is '.' or '!' or '?' or '…') i++;
            var piece = text[start..(i + 1)].Trim();
            if (piece.Length > 0) yield return piece;
            start = i + 1;
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) yield return tail;
    }
}
