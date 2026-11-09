using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor.Faces;

// Чистые правила запроса подстановки персонажей на лица (ADR-028, этап 3).
// Одна функция без зависимостей: котировка, сборщик запуска и MCP зовут её одинаково.
// Отказ — код invalid_request с русским текстом; отображаемое имя персонажа
// подставит вызывающий (здесь — слаг).
public static class FaceTargetRules
{
    // Лимит лиц за запрос (В2): не больше двух проходов
    public const int MaxFacePasses = 2;

    // Результат проверки: Ok — можно продолжать; иначе ErrorCode + Error для ответа
    public sealed record Result(bool Ok, string? ErrorCode, string? Error)
    {
        public static Result Pass { get; } = new(true, null, null);
        public static Result Fail(string code, string error) => new(false, code, error);
    }

    // Правила:
    //   1 ≤ Faces.Count ≤ MaxFacePasses, иначе «Не больше 2 лиц за раз»;
    //   номер лица ≥ 1 («Номер лица должен быть не меньше 1») и не повторяется («Лицо N указано дважды»);
    //   персонаж (слаг) не пустой и не повторяется (В3), иначе
    //   «Каждому лицу — свой персонаж: «{slug}» уже стоит на лице {n}».
    public static Result Validate(FaceTarget target)
    {
        var faces = target.Faces;

        // Пустой список и превышение лимита — один и тот же текст
        if (faces.Count is 0 or > MaxFacePasses)
            return Result.Fail(ImageEditErrorCodes.InvalidRequest, "Не больше 2 лиц за раз");

        // Номера лиц: ≥ 1 и без повторов
        var seenFaces = new HashSet<int>(faces.Count);
        foreach (var fa in faces)
        {
            if (fa.Face < 1)
                return Result.Fail(ImageEditErrorCodes.InvalidRequest, "Номер лица должен быть не меньше 1");
            if (!seenFaces.Add(fa.Face))
                return Result.Fail(ImageEditErrorCodes.InvalidRequest, $"Лицо {fa.Face} указано дважды");
        }

        // Персонажи: слаг не пустой и не повторяется (В3)
        var seenSlugs = new HashSet<string>(faces.Count);
        foreach (var fa in faces)
        {
            if (string.IsNullOrWhiteSpace(fa.CharacterSlug))
                return Result.Fail(ImageEditErrorCodes.InvalidRequest, "Укажите персонажа");
            if (!seenSlugs.Add(fa.CharacterSlug))
            {
                var first = faces.First(f => f.CharacterSlug == fa.CharacterSlug);
                return Result.Fail(ImageEditErrorCodes.InvalidRequest,
                    $"Каждому лицу — свой персонаж: «{fa.CharacterSlug}» уже стоит на лице {first.Face}");
            }
        }

        return Result.Pass;
    }
}
