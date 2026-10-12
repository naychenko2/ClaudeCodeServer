using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;

namespace ClaudeHomeServer.Services.ImageEditor;

// Персонажи проекта на диске (ADR-017, раздел 10): characters/<slug>/character.json и
// 3–10 фото лица. Владение проектом проверяет контроллер, сюда приходит уже корень.
//
// Граница проекта держится в трёх местах сразу:
// - slug из маршрута проходит только по белому списку [a-z0-9-] — «../x» здесь просто
//   «нет такого персонажа», а не путь;
// - каждый путь собирается через SafePath.Join;
// - папка characters/, папка персонажа и файл фото не могут быть символической ссылкой:
//   SafePath сравнивает строки и ссылку наружу не видит.
// Фото называем сами (face-01.jpg), имя файла из запроса на диск не попадает вовсе.
public static partial class CharacterStore
{
    public const string Folder = "characters";
    public const string ManifestFile = "character.json";
    public const int MinPhotos = 3;
    public const int MaxPhotos = 10;
    public const int MaxPhotoMb = 8;
    // Сколько фото уходит в запрос генерации: primary и два ракурса, остальное место —
    // исходнику и другим образцам
    public const int PhotosPerRequest = 3;
    private const int MaxSlugChars = 48;

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\z")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^[\p{L}\p{N}_]+\z")]
    private static partial Regex TriggerPattern();

    public static bool IsValidSlug(string? slug) => slug is not null && SlugPattern().IsMatch(slug);

    public static bool IsValidTrigger(string? trigger) => trigger is not null && TriggerPattern().IsMatch(trigger);

    public static string SlugFromName(string name)
    {
        var slug = Slugifier.Slugify(name, maxChars: MaxSlugChars);
        return IsValidSlug(slug) ? slug : "character";
    }

    public static IReadOnlyList<CharacterManifest> List(string root)
    {
        var folder = FolderPath(root);
        if (folder is null || !Directory.Exists(folder)) return [];
        var result = new List<CharacterManifest>();
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            var slug = Path.GetFileName(dir);
            if (Get(root, slug) is { } manifest) result.Add(manifest);
        }
        return [.. result.OrderBy(m => m.CreatedAt).ThenBy(m => m.Slug, StringComparer.Ordinal)];
    }

    public static CharacterManifest? Get(string root, string slug)
    {
        var dir = CharacterDir(root, slug);
        if (dir is null) return null;
        var file = Path.Combine(dir, ManifestFile);
        if (!File.Exists(file) || IsLink(file)) return null;
        var manifest = CharacterManifest.Parse(File.ReadAllText(file));
        if (manifest is null) return null;
        // Имя папки — источник правды: переименованная руками папка не должна вести в чужую
        manifest.Slug = slug;
        return manifest;
    }

    public static ImageEditCallResult<CharacterChange> Create(string root, CharacterDraft draft, DateTime now)
    {
        var name = draft.Name?.Trim() ?? "";
        if (name.Length == 0) return Invalid("Укажите имя персонажа");
        if (draft.Photos.Count is < MinPhotos or > MaxPhotos)
            return Invalid($"Нужно от {MinPhotos} до {MaxPhotos} фото");
        if (CheckPhotos(draft.Photos) is { } bad) return Invalid(bad);

        var folder = FolderPath(root);
        if (folder is null) return Invalid("Папка персонажей недоступна");
        Directory.CreateDirectory(folder);

        // Существующую папку не перетираем никогда: занятый slug → anya-2, anya-3
        var baseSlug = SlugFromName(name);
        string slug = baseSlug, dir;
        for (var n = 2; ; n++)
        {
            dir = CharacterDir(root, slug) ?? "";
            if (dir.Length > 0 && !Directory.Exists(dir) && !File.Exists(dir)) break;
            if (n > 999) return Invalid("Не удалось подобрать свободное имя папки");
            slug = $"{TrimForSuffix(baseSlug, n)}-{n}";
        }
        Directory.CreateDirectory(dir);

        var manifest = new CharacterManifest
        {
            Name = name,
            Slug = slug,
            Description = Normalize(draft.Description),
            CreatedAt = now,
            Providers = new() { [CharacterManifest.HiggsfieldProvider] = CharacterManifest.EmptyHiggsfield() },
        };
        var written = WritePhotos(root, slug, dir, manifest, draft.Photos);
        EnsurePrimary(manifest);
        WriteNew(Path.Combine(dir, ManifestFile), System.Text.Encoding.UTF8.GetBytes(manifest.Serialize()));
        written.Add(Rel(slug, ManifestFile));
        return ImageEditCallResult<CharacterChange>.Ok(new CharacterChange(manifest, written, []));
    }

    public static ImageEditCallResult<CharacterChange>? Update(string root, string ownerId, string slug, CharacterPatch patch)
    {
        var manifest = Get(root, slug);
        var dir = CharacterDir(root, slug);
        if (manifest is null || dir is null) return null;

        if (patch.Lora is { Remove: false } set && CheckLora(ownerId, set) is { } loraError) return loraError;
        if (patch.Name is { } newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return Invalid("Укажите имя персонажа");
            manifest.Name = newName.Trim();
        }
        if (patch.Description is not null) manifest.Description = Normalize(patch.Description);

        var remove = patch.RemovePhotos.ToHashSet(StringComparer.Ordinal);
        if (remove.Any(f => manifest.Photos.All(p => p.File != f)))
            return Invalid("Такого фото у персонажа нет");
        // Число фото проверяем, только когда патч их меняет: у персонажа с 11 фото на диске
        // иначе нельзя поправить даже LoRA
        var remaining = manifest.Photos.Count - remove.Count + patch.AddPhotos.Count;
        if ((remove.Count > 0 || patch.AddPhotos.Count > 0) && remaining is < MinPhotos or > MaxPhotos)
            return Invalid($"Нужно от {MinPhotos} до {MaxPhotos} фото");
        if (CheckPhotos(patch.AddPhotos) is { } bad) return Invalid(bad);
        if (CheckPhotoLabels(manifest, remove, patch.PhotoLabels) is { } labelError) return Invalid(labelError);
        if (patch.PrimaryPhoto is { } primary
            && (remove.Contains(primary) || manifest.Photos.All(p => p.File != primary)))
            return Invalid("Главное фото не найдено");

        var deleted = new List<string>();
        foreach (var file in remove)
        {
            var full = PhotoPath(dir, file);
            if (full is not null && File.Exists(full) && !IsLink(full)) File.Delete(full);
            deleted.Add(Rel(slug, file));
        }
        manifest.Photos.RemoveAll(p => remove.Contains(p.File));

        var written = WritePhotos(root, slug, dir, manifest, patch.AddPhotos);
        if (patch.PrimaryPhoto is { } chosen)
            manifest.Photos = [.. manifest.Photos.Select(p => p with { Primary = p.File == chosen })];
        EnsurePrimary(manifest);
        if (patch.PhotoLabels is { Count: > 0 } labels) ApplyHumanLabels(manifest, labels);
        if (patch.Lora is { } lora) ApplyLora(manifest, lora);

        // Манифест перезаписывается целиком через временный файл: оборванная запись не
        // должна оставить персонажа без character.json
        var target = Path.Combine(dir, ManifestFile);
        var temp = target + ".tmp";
        File.WriteAllText(temp, manifest.Serialize());
        File.Move(temp, target, overwrite: true);
        written.Add(Rel(slug, ManifestFile));
        return ImageEditCallResult<CharacterChange>.Ok(new CharacterChange(manifest, written, deleted));
    }

    // Слияние меток авторазметки (ADR-027, §1). Вызывать под блокировкой персонажа
    // (CharacterLoraRuns.LockCharacterAsync), как Update: метки считались долго, манифест за это
    // время мог поменяться, поэтому он читается заново. Фото с LabelSource=human не трогаем целиком,
    // метка удалённого фото отбрасывается, прочие поля манифеста остаются как есть.
    // null — персонажа нет; иначе имена файлов, чьи метки записаны.
    // placeSource — источник МЕСТА разметки: Auto (модель ответила) или NoRoute (разметить было
    // нечем, повторим позже). anglePlace — куда писать: AngleSource (angle/pitch) или EmotionSource
    // (emotion). По месту, а не общим LabelSource: запись одного места не затирает noroute другого
    // (дефекты а/б находки Александра).
    public static IReadOnlyList<string>? MergeAutoLabels(
        string root, string slug, IReadOnlyDictionary<string, CharacterPhotoLabels> labels,
        string placeSource = PhotoLabelValues.Auto, bool anglePlace = true)
    {
        var manifest = Get(root, slug);
        var dir = CharacterDir(root, slug);
        if (manifest is null || dir is null) return null;

        var applied = new List<string>();
        for (var i = 0; i < manifest.Photos.Count; i++)
        {
            var photo = manifest.Photos[i];
            if (photo.LabelSource == PhotoLabelValues.Human || !labels.TryGetValue(photo.File, out var found)) continue;
            // Запись без единой метки ничего не говорит о фото: источник не ставим
            if (found.Angle is null && found.Pitch is null && found.Emotion is null) continue;
            manifest.Photos[i] = anglePlace
                ? photo with
                {
                    Angle = found.Angle ?? photo.Angle,
                    Pitch = found.Pitch ?? photo.Pitch,
                    AngleSource = placeSource,
                }
                : photo with
                {
                    Emotion = found.Emotion ?? photo.Emotion,
                    EmotionSource = placeSource,
                };
            applied.Add(photo.File);
        }
        if (applied.Count == 0) return applied;

        var target = Path.Combine(dir, ManifestFile);
        var temp = target + ".tmp";
        File.WriteAllText(temp, manifest.Serialize());
        File.Move(temp, target, overwrite: true);
        return applied;
    }

    private static string? CheckPhotoLabels(
        CharacterManifest manifest, HashSet<string> remove, IReadOnlyDictionary<string, CharacterPhotoLabels>? labels)
    {
        if (labels is null) return null;
        foreach (var (file, l) in labels)
        {
            if (remove.Contains(file) || manifest.Photos.All(p => p.File != file)) return "Такого фото у персонажа нет";
            if (l.Angle is not null && !PhotoLabelValues.Angles.Contains(l.Angle)) return "Неизвестный ракурс";
            if (l.Pitch is not null && !PhotoLabelValues.Pitches.Contains(l.Pitch)) return "Неизвестный наклон головы";
            if (l.Emotion is not null && !PhotoLabelValues.Emotions.Contains(l.Emotion)) return "Неизвестная эмоция";
        }
        return null;
    }

    // Ручная правка: поля меняются только переданные, источник фото становится human (авторазметка
    // такое фото не трогает). Запись из одних null — сброс: метки, источник фото и по-местные
    // источники (AngleSource/EmotionSource) стираются, фото снова доступно авторазметке.
    private static void ApplyHumanLabels(CharacterManifest manifest, IReadOnlyDictionary<string, CharacterPhotoLabels> labels)
    {
        for (var i = 0; i < manifest.Photos.Count; i++)
        {
            var photo = manifest.Photos[i];
            if (!labels.TryGetValue(photo.File, out var l)) continue;
            if (l.Angle is null && l.Pitch is null && l.Emotion is null)
            {
                manifest.Photos[i] = photo with
                { Angle = null, Pitch = null, Emotion = null, LabelSource = null, AngleSource = null, EmotionSource = null };
                continue;
            }
            manifest.Photos[i] = photo with
            {
                Angle = l.Angle ?? photo.Angle,
                Pitch = l.Pitch ?? photo.Pitch,
                Emotion = l.Emotion ?? photo.Emotion,
                LabelSource = PhotoLabelValues.Human,
                AngleSource = null,
                EmotionSource = null,
            };
        }
    }

    // Проверка «задать LoRA»: null — всё в порядке, иначе отказ. Зовёт и Update, и контроллер
    // до создания персонажа. Для Remove проверять нечего. Видимость имени владельцу проверяем и здесь:
    // нижний слой не должен полагаться на то, что верхний уже отказал (защита в глубину).
    public static ImageEditCallResult<CharacterChange>? CheckLora(string ownerId, CharacterLoraPatch set)
    {
        if (set.Remove) return null;
        if (set.Known is null)
            return Unavailable("ComfyUI недоступен, поэтому проверить LoRA не получилось. Ничего не сохранено, попробуйте позже.");
        var name = set.Name?.Trim() ?? "";
        if (!LoraNamespace.VisibleTo(ownerId, name))
            return ImageEditCallResult<CharacterChange>.Fail(ImageEditErrorCodes.LoraNotVisible, "Эта LoRA вам недоступна.");
        if (!set.Known.Contains(name, StringComparer.Ordinal))
            return Invalid("Такой LoRA нет в ComfyUI. Выберите файл из списка.");
        var trigger = set.Trigger?.Trim() ?? "";
        if (!IsValidTrigger(trigger))
            return Invalid("Укажите слово-триггер: одно слово из букв, цифр и «_», без пробелов.");
        if (set.Strength is { } strength && !(strength >= 0 && strength <= 2))
            return Invalid("Сила должна быть числом от 0 до 2.");
        return null;
    }

    // Пишет только lora/trigger/strength блока providers.local; прочие ключи блока и
    // другие провайдеры остаются как есть. Пустой после «убрать» блок удаляется.
    private static void ApplyLora(CharacterManifest manifest, CharacterLoraPatch patch)
    {
        var local = manifest.Providers[CharacterManifest.LocalProvider] as JsonObject;
        if (patch.Remove)
        {
            if (local is null) return;
            local.Remove("lora");
            local.Remove("trigger");
            local.Remove("strength");
            if (local.Count == 0) manifest.Providers.Remove(CharacterManifest.LocalProvider);
            return;
        }
        if (local is null)
        {
            local = new JsonObject();
            manifest.Providers[CharacterManifest.LocalProvider] = local;
        }
        local["lora"] = patch.Name!.Trim();
        local["trigger"] = patch.Trigger!.Trim();
        local["strength"] = patch.Strength ?? CharacterManifest.DefaultLoraStrength;
    }

    // false — персонажа нет. Удаляется ровно папка персонажа внутри characters/.
    public static bool Delete(string root, string slug)
    {
        if (Get(root, slug) is null) return false;
        var dir = CharacterDir(root, slug);
        if (dir is null) return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    // Фото отдаётся, только если оно перечислено в манифесте персонажа
    public static EditedImage? OpenPhoto(string root, string slug, string file)
    {
        var manifest = Get(root, slug);
        var dir = CharacterDir(root, slug);
        if (manifest is null || dir is null || manifest.Photos.All(p => p.File != file)) return null;
        var full = PhotoPath(dir, file);
        if (full is null || !File.Exists(full) || IsLink(full)) return null;
        var bytes = File.ReadAllBytes(full);
        return new EditedImage(bytes, ContentTypeOf(ImageFormatSniffer.DetectExtension(bytes)));
    }

    // Персонаж для запроса: primary первым, затем остальные в порядке манифеста;
    // чужая владельцу LoRA из манифеста отбрасывается целиком (триггер и сила тоже).
    // explicitPhotos (ADR-027 §8) — явный выбор человека: образцами уходят ровно эти файлы манифеста
    // в этом порядке; любое нарушение (чужое имя, дубль, лишние, ссылка, не картинка) — null целиком.
    // Эталон facePhotos от явного списка не зависит
    public static CharacterForRequest? ForRequest(string root, string ownerId, string slug,
        int maxPhotos = PhotosPerRequest, IReadOnlyList<string>? explicitPhotos = null)
    {
        var manifest = Get(root, slug);
        var dir = CharacterDir(root, slug);
        if (manifest is null || dir is null) return null;

        var ordered = manifest.Photos.OrderByDescending(p => p.Primary).ToList();
        var photos = new List<ReferenceImage>();
        var facePhotos = new List<byte[]>();
        foreach (var photo in ordered)
        {
            if ((explicitPhotos is not null || photos.Count >= maxPhotos) && facePhotos.Count >= MaxPhotos) break;
            var full = PhotoPath(dir, photo.File);
            if (full is null || !File.Exists(full) || IsLink(full)) continue;
            var bytes = File.ReadAllBytes(full);
            var ext = ImageFormatSniffer.DetectExtension(bytes);
            if (ext is null) continue;
            if (explicitPhotos is null && photos.Count < maxPhotos)
                photos.Add(new ReferenceImage(bytes, ContentTypeOf(ext), ReferenceRole.Character, manifest.Name));
            if (facePhotos.Count < MaxPhotos) facePhotos.Add(bytes);
        }
        if (explicitPhotos is not null)
        {
            if (explicitPhotos.Count == 0 || explicitPhotos.Count > maxPhotos
                || explicitPhotos.Distinct().Count() != explicitPhotos.Count) return null;
            foreach (var file in explicitPhotos)
            {
                if (manifest.Photos.All(p => p.File != file)) return null;
                var full = PhotoPath(dir, file);
                if (full is null || !File.Exists(full) || IsLink(full)) return null;
                var bytes = File.ReadAllBytes(full);
                if (ImageFormatSniffer.DetectExtension(bytes) is not { } ext) return null;
                photos.Add(new ReferenceImage(bytes, ContentTypeOf(ext), ReferenceRole.Character, manifest.Name));
            }
        }
        if (photos.Count == 0) return null;
        var lora = manifest.ReadLora();
        if (lora is not null && !LoraNamespace.VisibleTo(ownerId, lora.Name)) lora = null;
        return new CharacterForRequest(
            new CharacterRef(manifest.Slug, manifest.Name, manifest.Description, lora, facePhotos), photos);
    }

    // Абсолютный путь папки персонажа; null — slug не проходит белый список, путь вышел за
    // корень или по дороге символическая ссылка
    public static string? CharacterDir(string root, string slug)
    {
        if (!IsValidSlug(slug)) return null;
        var folder = FolderPath(root);
        if (folder is null) return null;
        var dir = Join(root, $"{Folder}/{slug}");
        if (dir is null || IsLink(dir)) return null;
        return dir;
    }

    private static string? FolderPath(string root)
    {
        var folder = Join(root, Folder);
        return folder is null || IsLink(folder) ? null : folder;
    }

    private static string? PhotoPath(string dir, string file)
    {
        if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || file == ManifestFile) return null;
        return Join(dir, file);
    }

    private static string? Join(string root, string relative)
    {
        try { return SafePath.Join(root, relative); }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && info.LinkTarget is not null;
    }

    private static List<string> WritePhotos(
        string root, string slug, string dir, CharacterManifest manifest, IReadOnlyList<CharacterPhotoUpload> uploads)
    {
        var written = new List<string>();
        var next = NextIndex(manifest);
        foreach (var upload in uploads)
        {
            var ext = ImageFormatSniffer.DetectExtension(upload.Bytes)!;
            string file;
            do file = $"face-{next++:00}{ext}";
            while (File.Exists(Path.Combine(dir, file)));
            WriteNew(PhotoPath(dir, file) ?? throw new UnauthorizedAccessException("Путь фото вне папки персонажа"),
                upload.Bytes);
            manifest.Photos.Add(new CharacterPhoto(file, false, Normalize(upload.Angle)));
            written.Add(Rel(slug, file));
        }
        return written;
    }

    private static int NextIndex(CharacterManifest manifest)
    {
        var max = 0;
        foreach (var photo in manifest.Photos)
        {
            var name = Path.GetFileNameWithoutExtension(photo.File);
            if (name.StartsWith("face-", StringComparison.Ordinal) && int.TryParse(name[5..], out var n) && n > max)
                max = n;
        }
        return max + 1;
    }

    private static void EnsurePrimary(CharacterManifest manifest)
    {
        if (manifest.Photos.Count == 0 || manifest.Photos.Any(p => p.Primary)) return;
        manifest.Photos[0] = manifest.Photos[0] with { Primary = true };
    }

    private static string? CheckPhotos(IReadOnlyList<CharacterPhotoUpload> photos)
    {
        foreach (var photo in photos)
        {
            if (photo.Bytes.Length > MaxPhotoMb * 1024L * 1024L) return $"Фото больше {MaxPhotoMb} МБ";
            if (ImageFormatSniffer.DetectExtension(photo.Bytes) is not (".jpg" or ".png" or ".webp"))
                return "Фото должно быть в формате JPEG, PNG или WebP";
        }
        return null;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
    }

    private static string TrimForSuffix(string slug, int n)
    {
        var room = MaxSlugChars - n.ToString().Length - 1;
        return slug.Length > room ? slug[..room].TrimEnd('-') : slug;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Rel(string slug, string file) => $"{Folder}/{slug}/{file}";

    private static string ContentTypeOf(string? ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "application/octet-stream",
    };

    private static ImageEditCallResult<CharacterChange> Unavailable(string error) =>
        ImageEditCallResult<CharacterChange>.Fail(ImageEditErrorCodes.ProviderUnavailable, error);

    private static ImageEditCallResult<CharacterChange> Invalid(string error) =>
        ImageEditCallResult<CharacterChange>.Fail(ImageEditErrorCodes.InvalidRequest, error);
}
