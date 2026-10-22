namespace ClaudeHomeServer.Services.ImageEditor;

// Сюжет запроса в словарях меток фото (ADR-027, §1); null — сюжет молчит об этом признаке
public sealed record CharacterScene(string? Angle, string? Pitch, string? Emotion)
{
    public bool IsEmpty => IsKnown(Angle) is false && IsKnown(Pitch) is false && IsKnown(Emotion) is false;

    public static bool IsKnown(string? value) => !string.IsNullOrEmpty(value) && value != PhotoLabelValues.Unknown;
}

// Pick: auto — подбор сработал; fallback — нынешний порядок «главное, затем манифест»
public sealed record CharacterPhotoPick(IReadOnlyList<CharacterPhoto> Photos, string Pick)
{
    public const string Auto = "auto";
    public const string Fallback = "fallback";
    public const string Manual = "manual";
}

// Ранжирование фото персонажа под сюжет (ADR-027, §7). Чистая функция: файлов и сети нет.
public static class CharacterPhotoPicker
{
    // Порог очков РАКУРСА, с которого подобранное фото встаёт на первое место вместо главного.
    // 2 = точное совпадение ракурса (ADR-027 §7, замер шага 21: эмоция тоже даёт 2,
    // но она не замерена, поэтому решение — по ракурсу, а не по сумме).
    public const int LocalConfidentScore = 2;

    private static readonly string[] AngleChain = ["front", "three-quarter", "profile"];

    public static CharacterPhotoPick Pick(
        IReadOnlyList<CharacterPhoto> photos, CharacterScene? scene, int count,
        int confidentScore = LocalConfidentScore)
    {
        // Нынешний порядок ForRequest: главные первыми, остальные по манифесту (сортировка устойчива)
        var current = photos.OrderByDescending(p => p.Primary).ToList();
        var fallback = new CharacterPhotoPick(current.Take(count).ToList(), CharacterPhotoPick.Fallback);
        if (scene is null || scene.IsEmpty || photos.Count == 0 || count <= 0) return fallback;

        var scored = current
            .Select((photo, index) => (photo, index, score: Score(photo, scene)))
            .ToList();
        if (scored.All(s => s.score == 0)) return fallback;

        // current уже отсортирован по главному, так что при равных очках индекс решает в его пользу
        var ranked = scored.OrderByDescending(s => s.score).ThenBy(s => s.index).ToList();
        var main = scored[0];
        var best = ranked[0];

        var result = new List<CharacterPhoto>();
        // Решение по ракурсу, а не по сумме: эмоция тоже даёт 2, но она не замерена (ADR-027 §7)
        var first = AngleScore(best.photo.Angle, scene.Angle) >= confidentScore ? best : main;
        var second = first.index == best.index ? main : best;
        result.Add(first.photo);
        if (second.index != first.index) result.Add(second.photo);
        result.AddRange(ranked.Where(s => s.index != first.index && s.index != second.index).Select(s => s.photo));
        return new CharacterPhotoPick(result.Take(count).ToList(), CharacterPhotoPick.Auto);
    }

    // Модель берёт несколько фото как ОДНОГО человека (Klein): ей нужен набор ракурсов, а не
    // лучшее совпадение с сюжетом. У Qwen-Image (1 фото) и облака (null) — прежний подбор
    public static bool WantsDiverse(int? maxCharacterPhotos) => maxCharacterPhotos is > 1;

    // Очередь ракурсов набора после главного: анфас несёт больше всего черт, поэтому его вдвое
    // больше. A/B 2026-10-22 («Андрей», Klein): лучший набор из пяти — анфас, анфас снизу, три
    // четверти, профиль, ещё анфас (0,765 против 0,717 у трёх фото)
    private static readonly string[] DiverseOrder = ["front", "front", "three-quarter", "profile"];

    // Набор разных ракурсов: главное первым, дальше ракурсы по очереди DiverseOrder (нет фото
    // нужного ракурса — следующий в очереди). Внутри ракурса — наклон, которого среди уже взятых фото
    // этого ракурса меньше всего, при равенстве ровный, затем порядок манифеста. Фото без метки
    // ракурса — в конце, по манифесту; без меток вовсе — порядок манифеста
    public static CharacterPhotoPick Diverse(IReadOnlyList<CharacterPhoto> photos, int count)
    {
        var current = photos.OrderByDescending(p => p.Primary).ToList();
        if (count <= 0 || current.Count == 0 || current.All(p => AngleOf(p) is null))
            return new CharacterPhotoPick(current.Take(count).ToList(), CharacterPhotoPick.Fallback);

        var result = new List<CharacterPhoto> { current[0] };
        var left = current.Skip(1).ToList();
        var slot = AngleOf(current[0]) == DiverseOrder[0] ? 1 : 0;
        while (result.Count < count && left.Count > 0)
        {
            CharacterPhoto? next = null;
            for (var tried = 0; tried < DiverseOrder.Length && next is null; tried++, slot++)
            {
                var angle = DiverseOrder[slot % DiverseOrder.Length];
                var pitches = result.Where(p => AngleOf(p) == angle).Select(PitchOf).ToList();
                next = left
                    .Where(p => AngleOf(p) == angle)
                    .OrderBy(p => pitches.Count(x => x == PitchOf(p)))
                    .ThenBy(p => PitchOf(p) == "level" ? 0 : 1)
                    .FirstOrDefault();
            }
            next ??= left[0];
            result.Add(next);
            left.Remove(next);
        }
        return new CharacterPhotoPick(result, CharacterPhotoPick.Auto);
    }

    private static string? AngleOf(CharacterPhoto photo) =>
        CharacterScene.IsKnown(photo.Angle) && AngleChain.Contains(photo.Angle) ? photo.Angle : null;

    private static string PitchOf(CharacterPhoto photo) =>
        CharacterScene.IsKnown(photo.Pitch) ? photo.Pitch! : PhotoLabelValues.Unknown;

    internal static int Score(CharacterPhoto photo, CharacterScene scene) =>
        AngleScore(photo.Angle, scene.Angle) + PitchScore(photo.Pitch, scene.Pitch) + EmotionScore(photo.Emotion, scene.Emotion);

    private static int AngleScore(string? photo, string? scene)
    {
        if (!CharacterScene.IsKnown(photo) || !CharacterScene.IsKnown(scene)) return 0;
        var a = Array.IndexOf(AngleChain, photo);
        var b = Array.IndexOf(AngleChain, scene);
        if (a < 0 || b < 0) return 0;
        return Math.Abs(a - b) switch { 0 => 2, 1 => 1, _ => 0 };
    }

    private static int PitchScore(string? photo, string? scene) =>
        CharacterScene.IsKnown(photo) && photo == scene ? 1 : 0;

    private static int EmotionScore(string? photo, string? scene)
    {
        if (!CharacterScene.IsKnown(photo) || !CharacterScene.IsKnown(scene)) return 0;
        if (photo == scene) return 2;
        return IsRelated(photo!, scene!) ? 1 : 0;
    }

    private static bool IsRelated(string a, string b) =>
        (a, b) is ("smile", "laugh") or ("laugh", "smile");
}
