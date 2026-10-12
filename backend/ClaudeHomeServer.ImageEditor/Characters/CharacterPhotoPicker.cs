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
    // Порог очков, с которого подобранное фото встаёт на первое место вместо главного.
    // Пока «никогда»: значение даст замер шага 21.
    public const int LocalConfidentScore = int.MaxValue;

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
        var first = best.score >= confidentScore ? best : main;
        var second = first.index == best.index ? main : best;
        result.Add(first.photo);
        if (second.index != first.index) result.Add(second.photo);
        result.AddRange(ranked.Where(s => s.index != first.index && s.index != second.index).Select(s => s.photo));
        return new CharacterPhotoPick(result.Take(count).ToList(), CharacterPhotoPick.Auto);
    }

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
