using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Формат .film (ADR-022 §2): JSON с версией схемы. Чтение, проверка и ревизия; запись под ревизией — FilmStore.
// Ссылается на ФАЙЛЫ проекта, а не на нити. Правила: Cuts.Count == Items.Count − 1 (иначе film_invalid),
// неизвестная схема — только чтение (film_schema_unsupported), «устарел» и «● обновлена» не хранятся —
// они вычисляются из builds[].sourceHash (FilmStaleness).
internal static class FilmFormat
{
    public const int MaxItems = 50;
    public const double MaxCutSeconds = 10;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public enum Status { Ok, Invalid, Unsupported }

    // Document у Unsupported — лучшее приближение для чтения (поля как есть), править его нельзя; у Invalid —
    // null, если файл вообще не разобрался
    public sealed record Parsed(Status Status, FilmDocument? Document, string? Error);

    public static Parsed Parse(string json)
    {
        FilmDocument? doc;
        try
        {
            using (var probe = JsonDocument.Parse(json))
            {
                if (probe.RootElement.ValueKind != JsonValueKind.Object)
                    return new Parsed(Status.Invalid, null, "Файл фильма — не JSON-объект.");
            }
            doc = JsonSerializer.Deserialize<FilmDocument>(json, Json);
        }
        catch (JsonException ex)
        {
            return new Parsed(Status.Invalid, null, "Файл фильма не разобрался: " + ex.Message);
        }
        if (doc is null) return new Parsed(Status.Invalid, null, "Файл фильма пуст.");
        doc = Normalize(doc);
        if (doc.Schema != FilmDocument.CurrentSchema)
            return new Parsed(Status.Unsupported, doc,
                $"Схема .film {doc.Schema} не поддерживается этой версией: файл открыт только для чтения.");
        return Validate(doc) is { } error ? new Parsed(Status.Invalid, doc, error) : new Parsed(Status.Ok, doc, null);
    }

    // Пропущенные в файле списки читаются пустыми: поля добавляются аддитивно
    private static FilmDocument Normalize(FilmDocument d) => d with
    {
        Aspect = string.IsNullOrWhiteSpace(d.Aspect) ? "16:9" : d.Aspect,
        Items = d.Items ?? [],
        Cuts = d.Cuts ?? [],
        Builds = d.Builds ?? [],
    };

    public static string? Validate(FilmDocument doc)
    {
        if (doc.Items.Count > MaxItems) return $"В фильме не больше {MaxItems} сцен.";
        if (doc.Cuts.Count != Math.Max(0, doc.Items.Count - 1))
            return "Склеек должно быть на одну меньше, чем сцен.";
        // Фильм собирается в формате первой сцены (ADR-025 §11), поэтому пропорции — любое «W:H», а не пять известных
        if (!ClaudeHomeServer.Services.Media.AspectCrop.TryParse(doc.Aspect, out _)) return $"Неизвестное соотношение сторон «{doc.Aspect}».";
        for (var i = 0; i < doc.Items.Count; i++)
        {
            var item = doc.Items[i];
            if (string.IsNullOrWhiteSpace(item.File)) return $"У сцены {i + 1} не указан файл.";
            if (!FilmPaths.IsClipPath(item.File)) return $"Файл сцены {i + 1} должен лежать в video/.";
            if (TrimError(item.Trim) is { } trim) return $"Сцена {i + 1}: {trim}";
        }
        foreach (var cut in doc.Cuts)
        {
            if (CutError(cut.Type, cut.Sec) is { } error) return error;
        }
        if (doc.Music is { } music && MusicError(music) is { } musicError) return musicError;
        return null;
    }

    public static string? TrimError(IReadOnlyList<double>? trim)
    {
        if (trim is not { Count: 2 } || !double.IsFinite(trim[0]) || !double.IsFinite(trim[1]))
            return "обрезка — два числа: начало и конец.";
        if (trim[0] < 0 || trim[1] <= trim[0]) return "обрезка: конец должен быть позже начала, начало — не меньше нуля.";
        return null;
    }

    public static string? CutError(string type, double sec)
    {
        if (type is not (FilmCutTypes.Butt or FilmCutTypes.Dissolve or FilmCutTypes.Fade))
            return $"Неизвестная склейка «{type}».";
        if (!double.IsFinite(sec) || sec < 0 || sec > MaxCutSeconds) return $"Склейка: длина — от 0 до {MaxCutSeconds} с.";
        return null;
    }

    public static string? MusicError(FilmMusic music)
    {
        if (string.IsNullOrWhiteSpace(music.File) || !FilmPaths.IsMusicPath(music.File))
            return "Музыка должна лежать в music/.";
        if (music.Volume is < 0 or > 100) return "Громкость музыки — от 0 до 100.";
        if (!double.IsFinite(music.FadeOut) || music.FadeOut < 0) return "Затухание музыки не может быть отрицательным.";
        return null;
    }

    public const int MaxAssembleSide = 4096;

    // Малые пропорции, к которым прижимается размер первого клипа: 1344×768 — это «16:9», а не «7:4»
    private static readonly string[] KnownAspects = ["16:9", "9:16", "1:1", "4:3", "3:4", "21:9", "9:21", "3:2", "2:3"];

    // Размер клипа → пропорции фильма: известные в пределах 2 %, иначе несократимое «W:H»
    public static string AspectOfSize(int width, int height)
    {
        var ratio = (double)width / height;
        foreach (var known in KnownAspects)
            if (ClaudeHomeServer.Services.Media.AspectCrop.TryParse(known, out var r) && Math.Abs(ratio / r - 1) <= 0.02)
                return known;
        var a = width;
        var b = height;
        while (b != 0) (a, b) = (b, a % b);
        return $"{width / a}:{height / a}";
    }

    // Размер кадра сборки — размер первого клипа (ADR-025 §11): чётные стороны, длинная не больше 4096 с теми же пропорциями
    public static (int Width, int Height) AssembleSize(int width, int height)
    {
        var scale = Math.Max(width, height) > MaxAssembleSide ? (double)MaxAssembleSide / Math.Max(width, height) : 1;
        static int Even(double v) => Math.Max(2, (int)Math.Floor(v / 2) * 2);
        return (Even(width * scale), Even(height * scale));
    }

    // Таймаут растёт с площадью кадра относительно 1280×720, но не короче базового
    public static TimeSpan ScaledTimeout(TimeSpan baseTimeout, int width, int height)
    {
        var factor = Math.Max(1.0, (double)width * height / (1280 * 720));
        var minutes = Math.Clamp(baseTimeout.TotalMinutes * factor, 1, 240);
        return TimeSpan.FromMinutes(minutes);
    }

    // Соотношение сторон → размер кадра НОВОГО фильма (чётные числа); null — неизвестное. Сборка берёт размер первого клипа,
    // эта таблица нужна только для проверки пропорций при создании фильма
    public static (int Width, int Height)? AspectSize(string aspect) => aspect switch
    {
        "16:9" => (1280, 720),
        "9:16" => (720, 1280),
        "1:1" => (720, 720),
        "4:3" => (960, 720),
        "3:4" => (720, 960),
        _ => null,
    };

    public static string Serialize(FilmDocument doc) => JsonSerializer.Serialize(doc, Json);

    // Ревизия — хеш содержимого файла (етаг): любая правка в любом редакторе меняет её
    public static string RevisionOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    // Длительность фильма по документу: сумма обрезок за вычетом наплывов (затемнение длину не меняет)
    public static double DurationOf(FilmDocument doc)
    {
        double total = 0;
        for (var i = 0; i < doc.Items.Count; i++)
        {
            var trim = doc.Items[i].Trim;
            if (trim.Count == 2) total += Math.Max(0, trim[1] - trim[0]);
            if (i > 0 && i - 1 < doc.Cuts.Count && doc.Cuts[i - 1].Type == FilmCutTypes.Dissolve) total -= doc.Cuts[i - 1].Sec;
        }
        return Math.Max(0, total);
    }
}
