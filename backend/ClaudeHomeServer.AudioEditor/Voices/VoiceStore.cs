using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Voices;

// Голоса проекта на диске (ADR-021 §2): voices/<slug>/voice.json, образцы sample-01.wav… и пара
// voice.pth + voice.index у модели RVC. Сюда приходит уже корень серверного проекта: личную область и
// локальный проект отсекает VoiceLibrary до диска.
//
// Граница проекта: slug проходит только по белому списку [a-z0-9-] — «../x» здесь «нет такого голоса»,
// а не путь; каждый путь — только через ProjectLinkGuard.ResolveInside (ни один сегмент не ссылка).
// Файлы называем сами, имя файла из запроса на диск не попадает вовсе.
public static partial class VoiceStore
{
    public const string Folder = "voices";
    public const string ManifestFile = "voice.json";
    public const string RvcModelFile = "voice.pth";
    public const string RvcIndexFile = "voice.index";
    public const int MaxSamples = 5;
    public const int MaxSampleMb = 50;
    private const int MaxSlugChars = 48;

    // Мутации одного процесса не перемешивают манифест: записей мало, общий замок дешевле точечных
    private static readonly object Sync = new();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    public static bool IsValidSlug(string? slug) => slug is not null && SlugPattern().IsMatch(slug);

    public static IReadOnlyList<VoiceManifest> List(string root)
    {
        var folder = ProjectLinkGuard.ResolveInside(root, Folder);
        if (folder is null || !Directory.Exists(folder)) return [];
        var result = new List<VoiceManifest>();
        foreach (var dir in Directory.EnumerateDirectories(folder))
            if (Get(root, Path.GetFileName(dir)) is { } manifest) result.Add(manifest);
        return [.. result.OrderBy(m => m.CreatedAt).ThenBy(m => m.Slug, StringComparer.Ordinal)];
    }

    public static VoiceManifest? Get(string root, string slug)
    {
        if (FileIn(root, slug, ManifestFile) is not { } file || !File.Exists(file)) return null;
        var manifest = VoiceManifest.Parse(File.ReadAllText(file));
        if (manifest is null) return null;
        // Имя папки — источник правды: переименованная руками папка не должна вести в чужую
        manifest.Slug = slug;
        return manifest;
    }

    // Голос из образцов: 1–5 записей и необязательная расшифровка
    public static AudioEditCallResult<VoiceChange> CreateFromSamples(string root, string name, string? transcript,
        IReadOnlyList<VoiceSampleUpload> samples, DateTime now)
    {
        if (CheckName(name) is { } badName) return Invalid(badName);
        if (samples.Count is < 1 or > MaxSamples) return Invalid($"Нужно от 1 до {MaxSamples} записей");
        if (CheckSamples(samples) is { } bad) return Invalid(bad);

        lock (Sync)
        {
            if (NewFolder(root, name.Trim()) is not { } created) return Invalid("Не удалось завести папку голоса");
            var (slug, dir) = created;
            var manifest = new VoiceManifest
            {
                Name = name.Trim(),
                Slug = slug,
                Kind = VoiceKinds.Samples,
                Transcript = Normalize(transcript),
                CreatedAt = now,
            };
            try
            {
                var written = WriteSamples(root, slug, manifest, samples);
                WriteManifest(root, slug, manifest, createNew: true);
                written.Add(Rel(slug, ManifestFile));
                return AudioEditCallResult<VoiceChange>.Ok(new VoiceChange(manifest, written, []));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Полузаписанный голос не оставляем: папка заведена этим вызовом
                TryDeleteDir(dir);
                return Invalid("Голос не записан: путь вне проекта или ошибка записи");
            }
        }
    }

    // Голос из модели RVC: пара .pth + .index копируется вместе — или не копируется ничего
    public static AudioEditCallResult<VoiceChange> CreateFromRvc(string root, string name, string modelPath, string indexPath,
        DateTime now)
    {
        if (CheckName(name) is { } badName) return Invalid(badName);
        if (!File.Exists(modelPath) || !File.Exists(indexPath))
            return AudioEditCallResult<VoiceChange>.Fail(AudioEditErrorCodes.FileNotFound,
                "Файлы модели больше не найдены — рабочая папка очищена");

        lock (Sync)
        {
            if (NewFolder(root, name.Trim()) is not { } created) return Invalid("Не удалось завести папку голоса");
            var (slug, dir) = created;
            var manifest = new VoiceManifest { Name = name.Trim(), Slug = slug, Kind = VoiceKinds.Rvc, CreatedAt = now };
            try
            {
                Copy(modelPath, FileIn(root, slug, RvcModelFile));
                Copy(indexPath, FileIn(root, slug, RvcIndexFile));
                VoiceProviders.SetRvc(manifest.Providers, RvcModelFile, RvcIndexFile, now);
                WriteManifest(root, slug, manifest, createNew: true);
                return AudioEditCallResult<VoiceChange>.Ok(new VoiceChange(manifest,
                    [Rel(slug, RvcModelFile), Rel(slug, RvcIndexFile), Rel(slug, ManifestFile)], []));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteDir(dir);
                return Invalid("Модель не записана: путь вне проекта или ошибка записи");
            }
        }
    }

    // null — голоса нет. null в поле — не менять; переименование slug не трогает: папка остаётся прежней
    public static AudioEditCallResult<VoiceChange>? Update(string root, string slug, string? name, string? transcript)
    {
        lock (Sync)
        {
            if (Get(root, slug) is not { } manifest) return null;
            if (name is not null)
            {
                if (CheckName(name) is { } bad) return Invalid(bad);
                manifest.Name = name.Trim();
            }
            if (transcript is not null) manifest.Transcript = Normalize(transcript);
            WriteManifest(root, slug, manifest, createNew: false);
            return AudioEditCallResult<VoiceChange>.Ok(new VoiceChange(manifest, [Rel(slug, ManifestFile)], []));
        }
    }

    public static AudioEditCallResult<VoiceChange>? AddSamples(string root, string slug, IReadOnlyList<VoiceSampleUpload> samples)
    {
        lock (Sync)
        {
            if (Get(root, slug) is not { } manifest) return null;
            if (manifest.Kind != VoiceKinds.Samples) return Invalid("У модели RVC нет образцов");
            if (samples.Count == 0) return Invalid("Нет записей");
            if (manifest.Samples.Count + samples.Count > MaxSamples) return Invalid($"У голоса не больше {MaxSamples} записей");
            if (CheckSamples(samples) is { } bad) return Invalid(bad);
            var written = WriteSamples(root, slug, manifest, samples);
            WriteManifest(root, slug, manifest, createNew: false);
            written.Add(Rel(slug, ManifestFile));
            return AudioEditCallResult<VoiceChange>.Ok(new VoiceChange(manifest, written, []));
        }
    }

    // Убрать образец: только перечисленный в манифесте и не последний
    public static AudioEditCallResult<VoiceChange>? RemoveSample(string root, string slug, string file)
    {
        lock (Sync)
        {
            if (Get(root, slug) is not { } manifest) return null;
            if (manifest.Samples.All(s => s.File != file))
                return AudioEditCallResult<VoiceChange>.Fail(AudioEditErrorCodes.FileNotFound, "Такой записи у голоса нет");
            if (manifest.Samples.Count == 1) return Invalid("Последнюю запись убрать нельзя — удалите голос целиком");
            manifest.Samples.RemoveAll(s => s.File == file);
            WriteManifest(root, slug, manifest, createNew: false);
            if (FileIn(root, slug, file) is { } full && File.Exists(full)) File.Delete(full);
            return AudioEditCallResult<VoiceChange>.Ok(new VoiceChange(manifest, [Rel(slug, ManifestFile)], [Rel(slug, file)]));
        }
    }

    // false — голоса нет. Удаляется ровно папка голоса внутри voices/
    public static bool Delete(string root, string slug)
    {
        lock (Sync)
        {
            if (Get(root, slug) is null || DirOf(root, slug) is not { } dir) return false;
            Directory.Delete(dir, recursive: true);
            return true;
        }
    }

    // Правка кеша поставщиков (шаг пересоздания клона и отметка использования). null — голоса нет
    public static VoiceManifest? UpdateProviders(string root, string slug, Action<JsonObject> change)
    {
        lock (Sync)
        {
            if (Get(root, slug) is not { } manifest) return null;
            change(manifest.Providers);
            WriteManifest(root, slug, manifest, createNew: false);
            return manifest;
        }
    }

    // Абсолютный путь файла голоса для отдачи: только образец из манифеста или файл пары RVC
    public static string? OpenFile(string root, string slug, string file)
    {
        if (Get(root, slug) is not { } manifest) return null;
        var listed = manifest.Samples.Any(s => s.File == file)
            || (VoiceProviders.RvcPair(manifest.Providers) is { } pair && (pair.Model == file || pair.Index == file));
        if (!listed || FileIn(root, slug, file) is not { } full || !File.Exists(full)) return null;
        return full;
    }

    // Папка голоса внутри проекта; null — slug вне белого списка, путь вне корня или через ссылку
    public static string? DirOf(string root, string slug) =>
        IsValidSlug(slug) ? ProjectLinkGuard.ResolveInside(root, $"{Folder}/{slug}") : null;

    // Сигнатура звука → расширение; null — не wav, mp3, flac и не ogg
    public static string? DetectExtension(byte[] b)
    {
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'A' && b[10] == 'V' && b[11] == 'E') return ".wav";
        if (b.Length >= 4 && b[0] == 'f' && b[1] == 'L' && b[2] == 'a' && b[3] == 'C') return ".flac";
        if (b.Length >= 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S') return ".ogg";
        if (b.Length >= 3 && b[0] == 'I' && b[1] == 'D' && b[2] == '3') return ".mp3";
        if (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0) return ".mp3";
        return null;
    }

    private static string? FileIn(string root, string slug, string file)
    {
        if (!IsValidSlug(slug) || string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || file is "." or "..")
            return null;
        return ProjectLinkGuard.ResolveInside(root, $"{Folder}/{slug}/{file}");
    }

    // Существующую папку не перетираем никогда: занятый slug → anya-2, anya-3
    private static (string Slug, string Dir)? NewFolder(string root, string name)
    {
        var folder = ProjectLinkGuard.ResolveInside(root, Folder);
        if (folder is null) return null;
        Directory.CreateDirectory(folder);
        var baseSlug = SlugFromName(name);
        var slug = baseSlug;
        for (var n = 2; n < 1000; n++)
        {
            if (DirOf(root, slug) is { } dir && !Directory.Exists(dir) && !File.Exists(dir)
                && new FileInfo(dir).LinkTarget is null)
            {
                Directory.CreateDirectory(dir);
                return (slug, dir);
            }
            slug = $"{TrimForSuffix(baseSlug, n)}-{n}";
        }
        return null;
    }

    private static string SlugFromName(string name)
    {
        var slug = Slugifier.Slugify(name, maxChars: MaxSlugChars);
        return IsValidSlug(slug) ? slug : "voice";
    }

    private static List<string> WriteSamples(string root, string slug, VoiceManifest manifest,
        IReadOnlyList<VoiceSampleUpload> samples)
    {
        var written = new List<string>();
        var next = NextIndex(manifest);
        foreach (var sample in samples)
        {
            var ext = DetectExtension(sample.Bytes)!;
            string file;
            string? full;
            do
            {
                file = $"sample-{next++:00}{ext}";
                full = FileIn(root, slug, file) ?? throw new UnauthorizedAccessException("Путь записи вне папки голоса");
            }
            while (File.Exists(full));
            using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write)) stream.Write(sample.Bytes);
            manifest.Samples.Add(new VoiceSample(file));
            written.Add(Rel(slug, file));
        }
        return written;
    }

    private static int NextIndex(VoiceManifest manifest)
    {
        var max = 0;
        foreach (var sample in manifest.Samples)
        {
            var stem = Path.GetFileNameWithoutExtension(sample.File);
            if (stem.StartsWith("sample-", StringComparison.Ordinal) && int.TryParse(stem[7..], out var n) && n > max) max = n;
        }
        return max + 1;
    }

    // Манифест перезаписывается через временный файл: оборванная запись не оставит голос без voice.json
    private static void WriteManifest(string root, string slug, VoiceManifest manifest, bool createNew)
    {
        var target = FileIn(root, slug, ManifestFile) ?? throw new UnauthorizedAccessException("Путь манифеста вне проекта");
        var bytes = System.Text.Encoding.UTF8.GetBytes(manifest.Serialize());
        if (createNew)
        {
            using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes);
            return;
        }
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
    }

    private static void Copy(string source, string? target)
    {
        if (target is null) throw new UnauthorizedAccessException("Путь модели вне папки голоса");
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        src.CopyTo(dst);
    }

    private static string? CheckName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Укажите имя голоса"
        : name.Trim().Length > 100 ? "Имя голоса длиннее 100 символов"
        : null;

    private static string? CheckSamples(IReadOnlyList<VoiceSampleUpload> samples)
    {
        foreach (var sample in samples)
        {
            if (sample.Bytes.Length > MaxSampleMb * 1024L * 1024L) return $"Запись больше {MaxSampleMb} МБ";
            if (DetectExtension(sample.Bytes) is null) return "Запись должна быть в формате WAV, MP3, FLAC или OGG";
        }
        return null;
    }

    private static string TrimForSuffix(string slug, int n)
    {
        var room = MaxSlugChars - n.ToString().Length - 1;
        return slug.Length > room ? slug[..room].TrimEnd('-') : slug;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Rel(string slug, string file) => $"{Folder}/{slug}/{file}";

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static AudioEditCallResult<VoiceChange> Invalid(string error) =>
        AudioEditCallResult<VoiceChange>.Fail(AudioEditErrorCodes.InvalidRequest, error);
}
