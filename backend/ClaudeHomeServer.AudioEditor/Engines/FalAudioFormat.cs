using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using static ClaudeHomeServer.Services.AudioEditor.Catalog.AudioCatalog;

namespace ClaudeHomeServer.Services.AudioEditor.Engines;

// Тело запроса fal по раскладке каталога: Params (имена fal) → общие поля запроса → Defaults (если поля
// ещё нет) → Fixed (поверх всего). Нехватка входа — ArgumentException с текстом для человека: драйвер
// превращает её в отказ значением до отправки, а не в 422 от fal
internal static class FalRequestBuilder
{
    public static JsonObject Build(FalFields f, AudioRequest req, bool withParams = true)
    {
        var body = withParams ? req.Params?.DeepClone().AsObject() ?? new JsonObject() : new JsonObject();

        // Текст и промпт подменяют друг друга, если модель знает только одно из полей (у эффектов
        // описание звука — text, у Kokoro текст озвучки — prompt)
        Set(body, f.Text, req.Text ?? (f.Prompt is null ? req.Prompt : null));
        Set(body, f.Prompt, req.Prompt ?? (f.Text is null ? req.Text : null));
        Set(body, f.Lyrics, req.Lyrics);

        if (f.Language is { } languageField && req.Language is { Length: > 0 } language
            && !string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            body[languageField] = FalLanguages.Format(language.Trim(), f.LanguageForm)
                                  ?? throw new ArgumentException($"Модель fal не знает язык «{language}»");

        if (f.Duration is { } durationField && req.DurationSec is { } duration)
            body[durationField] = f.DurationInMs ? duration * 1000 : duration;
        if (f.Start is { } startField && req.StartSec is { } start) body[startField] = start;
        // Конец −1 у local значит «до конца трека»; у fal такого значения нет — поле не шлём
        if (f.End is { } endField && req.EndSec is { } end && end >= 0) body[endField] = end;

        if (f.Source is { } sourceField)
            body[sourceField] = DataUri(req.Source ?? throw new ArgumentException("Модели нужен исходный звук"));
        if (f.Reference is { } referenceField)
        {
            if (req.Reference is { } reference) body[referenceField] = DataUri(reference);
            else if (req.Op == AudioOp.CloneVoice) throw new ArgumentException("Для клона нужен образец голоса");
        }
        if (f.Seed is { } seedField && req.Seed is { } seed) body[seedField] = seed;

        if (f.Defaults is not null)
            foreach (var (key, value) in f.Defaults)
                if (!body.ContainsKey(key)) body[key] = value?.DeepClone();
        if (f.Fixed is not null)
            foreach (var (key, value) in f.Fixed)
                body[key] = value?.DeepClone();
        return body;
    }

    private static void Set(JsonObject body, string? field, string? value)
    {
        if (field is not null && value is not null) body[field] = value;
    }

    private static string DataUri(AudioBytes audio) =>
        $"data:{(string.IsNullOrWhiteSpace(audio.ContentType) ? "audio/wav" : audio.ContentType)};base64,{Convert.ToBase64String(audio.Bytes)}";
}

// Язык в форме поля fal. Таблица, а не CultureInfo: имена должны совпасть с перечислениями схем fal
// буква в букву и не зависеть от режима глобализации процесса
internal static class FalLanguages
{
    private static readonly Dictionary<string, (string Name, string Iso3)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["af"] = ("Afrikaans", "afr"), ["ar"] = ("Arabic", "ara"), ["bg"] = ("Bulgarian", "bul"), ["ca"] = ("Catalan", "cat"),
        ["cs"] = ("Czech", "ces"), ["da"] = ("Danish", "dan"), ["de"] = ("German", "deu"), ["el"] = ("Greek", "ell"),
        ["en"] = ("English", "eng"), ["es"] = ("Spanish", "spa"), ["fi"] = ("Finnish", "fin"), ["fr"] = ("French", "fra"),
        ["he"] = ("Hebrew", "heb"), ["hi"] = ("Hindi", "hin"), ["hr"] = ("Croatian", "hrv"), ["hu"] = ("Hungarian", "hun"),
        ["id"] = ("Indonesian", "ind"), ["it"] = ("Italian", "ita"), ["ja"] = ("Japanese", "jpn"), ["ko"] = ("Korean", "kor"),
        ["ms"] = ("Malay", "msa"), ["nl"] = ("Dutch", "nld"), ["nn"] = ("Nynorsk", "nno"), ["no"] = ("Norwegian", "nor"),
        ["pl"] = ("Polish", "pol"), ["pt"] = ("Portuguese", "por"), ["ro"] = ("Romanian", "ron"), ["ru"] = ("Russian", "rus"),
        ["sk"] = ("Slovak", "slk"), ["sl"] = ("Slovenian", "slv"), ["sv"] = ("Swedish", "swe"), ["sw"] = ("Swahili", "swa"),
        ["th"] = ("Thai", "tha"), ["tr"] = ("Turkish", "tur"), ["uk"] = ("Ukrainian", "ukr"), ["vi"] = ("Vietnamese", "vie"),
        ["yue"] = ("Cantonese", "yue"), ["zh"] = ("Chinese", "zho"),
    };

    public static string? Format(string iso, FalLanguageForm form)
    {
        if (form == FalLanguageForm.Iso) return iso.ToLowerInvariant();
        if (!Table.TryGetValue(iso, out var lang)) return null;
        return form switch
        {
            FalLanguageForm.Iso3 => lang.Iso3,
            FalLanguageForm.EnglishName => lang.Name,
            FalLanguageForm.LowerName => lang.Name.ToLowerInvariant(),
            // Перечисление language_boost MiniMax: кантонский — «Chinese,Yue»
            FalLanguageForm.MiniMax => iso.Equals("yue", StringComparison.OrdinalIgnoreCase) ? "Chinese,Yue" : lang.Name,
            _ => null,
        };
    }
}

// Файлы ответа fal: скачивание всех файлов результата и сборка ролей версии
internal static class FalOutputs
{
    private static readonly string[] AudioFields = ["audio", "audio_file"];

    // Ссылка на файл в поле ответа: объект File или первый элемент массива File
    public static string? FileUrl(JsonElement output, string field) =>
        output.ValueKind == JsonValueKind.Object && output.TryGetProperty(field, out var value) ? Url(value) : null;

    public static async Task<IReadOnlyList<AudioFile>> CollectAsync(HttpClient client, FalOutputKind kind, JsonElement output,
        CancellationToken ct)
    {
        if (output.ValueKind != JsonValueKind.Object) return [];
        switch (kind)
        {
            case FalOutputKind.Stems:
            {
                var files = new List<AudioFile>();
                foreach (var prop in output.EnumerateObject())
                {
                    if (FileOf(prop.Value) is not { } file) continue;
                    var role = AudioFileRoles.Stem(prop.Name);
                    if (!AudioFileRoles.IsValid(role)) continue;
                    files.Add(await DownloadAsync(client, file, role, ct));
                }
                return files;
            }
            case FalOutputKind.Transcript:
                return Transcript(output);
            default:
            {
                // Сначала известные имена поля звука, затем любое поле-файл
                var file = AudioFields.Select(f => output.TryGetProperty(f, out var v) ? FileOf(v) : null).FirstOrDefault(f => f is not null)
                           ?? output.EnumerateObject().Select(p => FileOf(p.Value)).FirstOrDefault(f => f is not null);
                return file is null ? [] : [await DownloadAsync(client, file.Value, AudioFileRoles.Main, ct)];
            }
        }
    }

    private static JsonElement? FileOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object when FalAudioEngine.Str(value, "url") is { Length: > 0 } => value,
        JsonValueKind.Array => value.EnumerateArray().Select(FileOf).FirstOrDefault(f => f is not null),
        _ => null,
    };

    private static string? Url(JsonElement value) => FileOf(value) is { } file ? FalAudioEngine.Str(file, "url") : null;

    private static async Task<AudioFile> DownloadAsync(HttpClient client, JsonElement file, string role, CancellationToken ct)
    {
        var url = FalAudioEngine.Str(file, "url")!;
        using var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new HttpRequestException("пустой файл результата");
        var contentType = FalAudioEngine.Str(file, "content_type") is { Length: > 0 } declared && !declared.StartsWith("image/")
            ? declared
            : resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var extension = ExtensionOf(FalAudioEngine.Str(file, "file_name")) ?? ExtensionOf(new Uri(url).AbsolutePath)
                        ?? ExtensionByType(contentType);
        return new AudioFile(role, bytes, contentType, extension);
    }

    private static string? ExtensionOf(string? name)
    {
        var ext = string.IsNullOrEmpty(name) ? "" : Path.GetExtension(name);
        return ext.Length is > 1 and <= 6 && ext.Skip(1).All(char.IsAsciiLetterOrDigit) ? ext.ToLowerInvariant() : null;
    }

    private static string ExtensionByType(string contentType) => contentType.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/ogg" or "audio/opus" => ".ogg",
        "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a",
        _ => ".bin",
    };

    // Расшифровка: .txt из text и .srt из отрезков (chunks у Wizper) или слов (words у Scribe)
    private static IReadOnlyList<AudioFile> Transcript(JsonElement output)
    {
        var files = new List<AudioFile>();
        if (FalAudioEngine.Str(output, "text") is { Length: > 0 } text)
            files.Add(new AudioFile(AudioFileRoles.Text, Encoding.UTF8.GetBytes(text.Trim()), "text/plain", ".txt"));
        var cues = Chunks(output).ToList();
        if (cues.Count == 0) cues = [.. Words(output)];
        if (cues.Count > 0)
            files.Add(new AudioFile(AudioFileRoles.Subtitles, Encoding.UTF8.GetBytes(Srt(cues)), "application/x-subrip", ".srt"));
        return files;
    }

    internal sealed record Cue(double Start, double End, string Text);

    private static IEnumerable<Cue> Chunks(JsonElement output)
    {
        if (!output.TryGetProperty("chunks", out var chunks) || chunks.ValueKind != JsonValueKind.Array) yield break;
        var list = chunks.EnumerateArray().ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (FalAudioEngine.Str(list[i], "text") is not { } text || string.IsNullOrWhiteSpace(text)) continue;
            if (!list[i].TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Array) continue;
            var pair = ts.EnumerateArray().ToList();
            if (pair.Count < 1 || Num(pair[0]) is not { } start) continue;
            // Конец последнего отрезка Whisper бывает null — отрезку две секунды
            var end = pair.Count > 1 && Num(pair[1]) is { } e ? e : start + 2;
            yield return new Cue(start, Math.Max(end, start), text.Trim());
        }
    }

    // Слова Scribe — в строки до 42 символов, по концу предложения или паузе больше секунды
    private static IEnumerable<Cue> Words(JsonElement output)
    {
        if (!output.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array) yield break;
        var line = new StringBuilder();
        double start = 0, end = 0;
        foreach (var w in words.EnumerateArray())
        {
            if (FalAudioEngine.Str(w, "type") is { } type && type != "word") continue;
            if (FalAudioEngine.Str(w, "text") is not { } text || string.IsNullOrWhiteSpace(text)) continue;
            if (!w.TryGetProperty("start", out var s) || Num(s) is not { } ws) continue;
            var we = w.TryGetProperty("end", out var e) && Num(e) is { } wv ? wv : ws;
            if (line.Length > 0 && (ws - end > 1 || line.Length + text.Length + 1 > 42))
            {
                yield return new Cue(start, end, line.ToString());
                line.Clear();
            }
            if (line.Length == 0) start = ws;
            else line.Append(' ');
            line.Append(text.Trim());
            end = we;
            if (text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('?') || text.TrimEnd().EndsWith('!'))
            {
                yield return new Cue(start, end, line.ToString());
                line.Clear();
            }
        }
        if (line.Length > 0) yield return new Cue(start, end, line.ToString());
    }

    // Число или null: TryGetDouble на null бросает, а у Whisper конец последнего отрезка бывает null
    private static double? Num(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : null;

    internal static string Srt(IReadOnlyList<Cue> cues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            sb.Append(i + 1).Append('\n')
              .Append(Time(cues[i].Start)).Append(" --> ").Append(Time(cues[i].End)).Append('\n')
              .Append(cues[i].Text).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string Time(double seconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }
}
