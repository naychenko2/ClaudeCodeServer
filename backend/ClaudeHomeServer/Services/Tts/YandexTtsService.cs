using System.Text;

namespace ClaudeHomeServer.Services.Tts;

// Синтез речи через Yandex SpeechKit REST v1 (tts:synthesize) — озвучка ответов
// в голосовом режиме чата. Без ключа/folderId — IsConfigured=false, контроллер отдаёт 503
// и фронт уходит на голос браузера (speechSynthesis).
//
// Референс по SpeechKit — nga-speech-to-text/SpeechKitClient.cs (там STT): у TTS другой хост,
// form-urlencoded тело и бинарный ответ (audio/mpeg). Авторизация та же: Api-Key сервисного
// аккаунта, которому для синтеза нужна роль ai.speechkit-tts.user (STT-роли недостаточно).
public class YandexTtsService(IHttpClientFactory http, IConfiguration config, ILogger<YandexTtsService> logger)
{
    public const string HttpClientName = "yandex-tts";
    private const string Endpoint = "https://tts.api.cloud.yandex.net/speech/v1/tts:synthesize";
    // Лимит REST v1 — 5000 символов на запрос: длиннее режем по предложениям и склеиваем mp3
    internal const int MaxCharsPerRequest = 5000;

    private readonly string? _apiKey = config["Yandex:SpeechKit:ApiKey"];
    private readonly string? _folderId = config["Yandex:SpeechKit:FolderId"];
    // Дефолт — ДОКУМЕНТИРОВАННЫЙ голос из списка Яндекса (marina, есть и в v1, и в v3).
    // Прежним дефолтом была alena: живой API её принимает (проверено 20.08.2026, 200 в обеих
    // версиях), но в списке голосов документации её нет — а инстанс, поднятый без
    // Yandex:SpeechKit:Voice, не должен зависеть от недокументированного имени: выключат —
    // синтез отвалится с 400 у всех, кто голос не задал.
    private readonly string _voice = config["Yandex:SpeechKit:Voice"] ?? "marina";
    private readonly double _speed = config.GetValue("Yandex:SpeechKit:Speed", 1.0);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_folderId);

    // mp3-байты синтезированной речи; null — синтез не удался (не настроен, Яндекс ответил
    // ошибкой или недоступен). Причина уже в логе — вызывающему хватает «не вышло» для 502.
    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(text)) return null;

        var client = http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(30);

        using var result = new MemoryStream();
        foreach (var chunk in SplitForSynthesis(text, MaxCharsPerRequest))
        {
            var bytes = await SynthesizeChunkAsync(client, chunk, ct);
            if (bytes is null) return null; // причина в логе; полкуска озвучки хуже честного фолбэка
            result.Write(bytes);
        }
        return result.Length > 0 ? result.ToArray() : null;
    }

    private async Task<byte[]?> SynthesizeChunkAsync(HttpClient client, string chunk, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", $"Api-Key {_apiKey}");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["text"] = chunk,
                ["lang"] = "ru-RU",
                ["voice"] = _voice,
                ["speed"] = _speed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["format"] = "mp3",
                ["folderId"] = _folderId!,
            });

            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsByteArrayAsync(ct);

            // 403 при живом ключе — почти всегда не тот ключ, а не та РОЛЬ: без отдельного
            // сообщения причина полдня ищется не там (ключ-то работает в STT)
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                logger.LogWarning("SpeechKit отверг синтез (403): ключ жив, но у сервисного аккаунта " +
                                  "нет роли ai.speechkit-tts.user — выдай её в Yandex Cloud.");
            else
                logger.LogWarning("SpeechKit ответил {Status} на синтез: {Body}",
                    (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            // Недоступность/таймаут Яндекса — штатный случай: строку в лог пишет QuietHttpLogger,
            // здесь только «не вышло»
            return null;
        }
    }

    // Нарезка текста под лимит одного запроса: по границам предложений, предложение длиннее
    // лимита режется жёстко. Чистая internal static функция — иначе её нечем тестировать.
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
                for (var i = 0; i < sentence.Length; i += maxLen)
                    chunks.Add(sentence.Substring(i, Math.Min(maxLen, sentence.Length - i)));
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
