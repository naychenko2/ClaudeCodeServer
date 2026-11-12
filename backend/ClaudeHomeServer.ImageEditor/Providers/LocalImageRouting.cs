using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.ImageEditor;

// Признаки запроса, по которым правила авто-выбора локальной модели решают, куда идти
[Flags]
public enum RouteTrait
{
    None = 0,
    QuotedText = 1,      // в запросе надпись в кавычках
    SignKeyword = 2,     // вывеска, афиша, слайд, плакат
    People = 4,          // люди, портрет
    Photo = 8,           // фото, фотореализм
    FastEdit = 16,       // фон, стиль, аксессуар
    PreciseEdit = 32,    // одежда, точная правка
    Mask = 64,           // маска (кисть) — или дорисовка за края
    FaceSwap = 128,      // подстановка персонажа на лицо
    Character = 256,     // персонаж по фото
    Annotations = 512,   // стрелки, рамки, подписи на холсте
    Inputs = 1024,       // образцы на входе
    BigSource = 2048,    // исходник заметно больше 1 Мп (или размер неизвестен)
    PhotorealMode = 4096,
    PreciseMode = 8192,
    FastMode = 16384,
}

// Одно правило таблицы: операции, обязательные и запрещённые признаки → модели по порядку
// предпочтения (первая, что есть в каталоге и умеет операцию) и причина для человека
public sealed record RouteRule(
    string Id,
    IReadOnlyList<ImageEditOp> Ops,
    RouteTrait Any,          // сработает, если есть ХОТЬ ОДИН из них (None — не проверяется)
    RouteTrait Forbid,       // ни одного из них
    IReadOnlyList<string> Models,
    string Reason);

// Таблица маршрутизации «Авто» у локальных моделей — ЕДИНСТВЕННОЕ место, где решается «что куда».
// Стартовая — итог Ш1 (замер 520 картинок, tools/image-bench). После оценки Андреем слепых листов
// меняется только она (и тесты LocalImageRoutingTests). Первое сработавшее правило выигрывает.
// Fibo 2.0 Dev и MiniMax H3 в таблицу не входят: Dev медленный и слабый, H3 печатает куски промпта
// в кадр — только ручной выбор. Qwen «качественно» на генерации выигрыша не дал при цене ×2–2,7.
public static class LocalImageRouting
{
    // Исходник правки больше этого — Fibo Turbo не берём: он ужимает результат до ~1 Мп
    public const double FiboSourceMegapixels = 1.3;
    // Больше образцов Fibo Turbo в замере не видел
    public const int FiboMaxReferences = 2;

    private static readonly ImageEditOp[] Gen = [ImageEditOp.Generate];
    private static readonly ImageEditOp[] Edit = [ImageEditOp.Edit];
    private static readonly ImageEditOp[] MaskOps = [ImageEditOp.Edit, ImageEditOp.Inpaint, ImageEditOp.Outpaint];

    public static readonly IReadOnlyList<RouteRule> Table =
    [
        // Маска, дорисовка, подстановка на лицо — Qwen «качественно» (как было до Ш2)
        new("mask", [ImageEditOp.Inpaint, ImageEditOp.Outpaint], RouteTrait.None, RouteTrait.None,
            [LocalImageEditor.QwenImage], "маска или дорисовка"),
        new("mask-edit", MaskOps, RouteTrait.Mask | RouteTrait.FaceSwap, RouteTrait.None,
            [LocalImageEditor.QwenImage], "маска, дорисовка или подстановка на лицо"),
        // Персонаж: черновик Ш1 велел Klein, но котировка не знает, есть ли у персонажа LoRA, а Klein её
        // не применяет (LocalImageEditor.RunAsync: Lora = null) — LoRA-персонаж молча потерял бы сходство.
        // Поэтому «Авто» оставляет Qwen Turbo (как было до Ш2), а Klein — ручным выбором. Когда котировка
        // научится видеть LoRA, строку заменить: [Klein] без LoRA, Qwen Turbo — с LoRA
        new("character", [ImageEditOp.Generate, ImageEditOp.Edit], RouteTrait.Character, RouteTrait.None,
            [LocalImageEditor.QwenImageTurbo], "персонаж"),
        // Стрелки и подписи на холсте ходят образцами — модели без канала образцов не годятся
        new("annotations", Edit, RouteTrait.Annotations, RouteTrait.None,
            [LocalImageEditor.QwenImageTurbo], "пометки на холсте"),
        // Надписи и кириллица
        new("text-gen", Gen, RouteTrait.QuotedText, RouteTrait.Inputs,
            [LocalImageEditor.FluxKlein, LocalImageEditor.MossImage], "в запросе надпись"),
        new("sign-gen", Gen, RouteTrait.SignKeyword, RouteTrait.Inputs,
            [LocalImageEditor.FluxKlein, LocalImageEditor.MossImage], "вывеска, афиша или слайд"),
        new("text-edit", Edit, RouteTrait.QuotedText, RouteTrait.None,
            [LocalImageEditor.FluxKlein, LocalImageEditor.QwenImageTurbo], "в запросе надпись"),
        new("sign-edit", Edit, RouteTrait.SignKeyword, RouteTrait.None,
            [LocalImageEditor.FluxKlein, LocalImageEditor.QwenImageTurbo], "вывеска, афиша или слайд"),
        // Правка: одежда и точная правка — Klein; быстрая — Fibo Turbo, если исходник не больше ~1 Мп
        new("precise-edit", Edit, RouteTrait.PreciseMode | RouteTrait.PreciseEdit, RouteTrait.None,
            [LocalImageEditor.FluxKlein, LocalImageEditor.QwenImageTurbo], "точная правка или одежда"),
        new("fast-edit", Edit, RouteTrait.FastMode | RouteTrait.FastEdit, RouteTrait.BigSource | RouteTrait.Inputs,
            [LocalImageEditor.Fibo2Turbo], "быстрая правка"),
        new("edit-default", Edit, RouteTrait.None, RouteTrait.None,
            [LocalImageEditor.QwenImageTurbo], "правка по умолчанию"),
        // Генерация: люди и фото — Krea 2 Turbo (образцов он не берёт, с образцами — как по умолчанию)
        new("people-gen", Gen, RouteTrait.People | RouteTrait.Photo | RouteTrait.PhotorealMode, RouteTrait.Inputs,
            [LocalImageEditor.Krea2Turbo], "люди или фотореализм"),
        new("gen-default", Gen, RouteTrait.None, RouteTrait.None,
            [LocalImageEditor.QwenImageTurbo], "по умолчанию"),
    ];

    // Размер исходника неизвестен — считаем «большим»: Fibo Turbo ужимает результат до ~1 Мп
    public static RouteTrait TraitsOf(ImageEditOp op, EditMode mode, EditTraits t)
    {
        var r = RouteTrait.None;
        var h = PromptHints.Of(t.Prompt);
        if (h.QuotedText) r |= RouteTrait.QuotedText;
        if (h.Sign) r |= RouteTrait.SignKeyword;
        if (h.People) r |= RouteTrait.People;
        if (h.Photo) r |= RouteTrait.Photo;
        if (h.Fast) r |= RouteTrait.FastEdit;
        if (h.Precise) r |= RouteTrait.PreciseEdit;
        if (t.HasMask || op is ImageEditOp.Inpaint or ImageEditOp.Outpaint) r |= RouteTrait.Mask;
        if (t.FaceSwap) r |= RouteTrait.FaceSwap;
        if (t.HasCharacter) r |= RouteTrait.Character;
        if (t.HasAnnotations) r |= RouteTrait.Annotations;
        if (t.References > 0) r |= RouteTrait.Inputs;
        if (t.References > FiboMaxReferences || t.SourceMegapixels is null or > FiboSourceMegapixels)
            r |= RouteTrait.BigSource;
        r |= mode switch
        {
            EditMode.Photoreal => RouteTrait.PhotorealMode,
            EditMode.Precise => RouteTrait.PreciseMode,
            EditMode.Fast => RouteTrait.FastMode,
            _ => RouteTrait.None,
        };
        return r;
    }

    // Правила, после которых правила молчат: признаков в запросе не нашлось
    private static readonly string[] DefaultRules = ["gen-default", "edit-default"];

    // Категории классификатора неоднозначных запросов (Ш3, ImageRouteClassifier): подпись для причины
    // выбора и признаки таблицы. Модель решает по-прежнему таблица — классификатор только досказывает
    // признаки, которых правила по словам не нашли. Пустые признаки — модель по умолчанию, но причина
    // называет категорию. «товар» = предметная съёмка, то есть фотореализм. После Ш10 (оценка листов
    // по категориям) менять признаки здесь же
    public static readonly IReadOnlyDictionary<string, (string Label, RouteTrait Traits)> GenCategories =
        new Dictionary<string, (string, RouteTrait)>
        {
            ["люди"] = ("люди", RouteTrait.People),
            ["товар"] = ("товар", RouteTrait.Photo),
            ["арт"] = ("иллюстрация", RouteTrait.None),
            ["надписи"] = ("надписи", RouteTrait.SignKeyword),
            ["состав"] = ("состав сцены", RouteTrait.None),
            ["пейзаж"] = ("пейзаж", RouteTrait.None),
        };

    public static readonly IReadOnlyDictionary<string, (string Label, RouteTrait Traits)> EditCategories =
        new Dictionary<string, (string, RouteTrait)>
        {
            ["фон"] = ("фон", RouteTrait.FastEdit),
            ["одежда"] = ("одежда", RouteTrait.PreciseEdit),
            ["добавить"] = ("добавить предмет", RouteTrait.FastEdit),
            ["убрать"] = ("убрать предмет", RouteTrait.None),
            ["стиль"] = ("стиль", RouteTrait.FastEdit),
            ["текст"] = ("надпись", RouteTrait.SignKeyword),
        };

    // Стоит ли спрашивать классификатор: текст есть, ключевых слов в нём нет, и правила дошли до умолчания.
    // Слово нашлось, но правило не сработало (фон при большом исходнике) — категория выбора не изменит
    public static bool NeedsClassifier(IReadOnlyList<ImageEditModelInfo> catalog, ImageEditOp op, EditMode mode, EditTraits traits) =>
        !string.IsNullOrWhiteSpace(traits.Prompt) && PromptHints.Of(traits.Prompt) == default
        && Match(catalog, op, TraitsOf(op, mode, traits)) is { } m && DefaultRules.Contains(m.Rule.Id);

    // Первое сработавшее правило, у которого есть подходящая модель каталога. category — ответ
    // классификатора (ключ GenCategories/EditCategories): его признаки добавляются к найденным по словам,
    // а причина выбора называет категорию
    public static ImageRoute? Pick(IReadOnlyList<ImageEditModelInfo> catalog, ImageEditOp op, EditMode mode, EditTraits traits,
        string? category = null)
    {
        var have = TraitsOf(op, mode, traits);
        var cats = op == ImageEditOp.Generate ? GenCategories : EditCategories;
        if (category is not null && cats.TryGetValue(category, out var c))
        {
            var m = Match(catalog, op, have | c.Traits);
            return m is null ? null : new ImageRoute(m.Value.Model, $"{c.Label} (по смыслу запроса)");
        }
        var r = Match(catalog, op, have);
        return r is null ? null : new ImageRoute(r.Value.Model, r.Value.Rule.Reason);
    }

    private static (RouteRule Rule, ImageEditModelInfo Model)? Match(IReadOnlyList<ImageEditModelInfo> catalog, ImageEditOp op, RouteTrait have)
    {
        foreach (var rule in Table)
        {
            if (!rule.Ops.Contains(op)) continue;
            if (rule.Any != RouteTrait.None && (have & rule.Any) == 0) continue;
            if ((have & rule.Forbid) != 0) continue;
            foreach (var id in rule.Models)
            {
                var model = catalog.FirstOrDefault(m => m.Id == id && m.Caps.Ops.Contains(op));
                if (model is not null) return (rule, model);
            }
        }
        return null;
    }

    // «Авто → Klein: в запросе надпись»
    public static string Describe(ImageEditModelInfo model, string reason) => $"Авто → {ShortName(model)}: {reason}";

    private static string ShortName(ImageEditModelInfo m) => m.Id switch
    {
        LocalImageEditor.QwenImageTurbo => "Qwen Turbo",
        LocalImageEditor.QwenImage => "Qwen «качественно»",
        LocalImageEditor.FluxKlein => "Klein",
        LocalImageEditor.Fibo2Turbo => "Fibo Turbo",
        LocalImageEditor.Krea2Turbo => "Krea 2 Turbo",
        LocalImageEditor.MossImage => "MOSS",
        _ => m.Label,
    };
}

// Признаки по тексту запроса: надпись в кавычках и категории по ключевым словам
public readonly record struct PromptHints(bool QuotedText, bool Sign, bool People, bool Photo, bool Fast, bool Precise)
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan T = TimeSpan.FromMilliseconds(50);

    // «…», "…", “…” с хотя бы одной буквой или цифрой внутри
    private static readonly Regex Quoted = new("«[^»]*[\\p{L}\\p{N}][^»]*»|\"[^\"]*[\\p{L}\\p{N}][^\"]*\"|“[^”]*[\\p{L}\\p{N}][^”]*”", O, T);
    private static readonly Regex SignRx = new("(?<!\\p{L})(вывеск|афиш|слайд|плакат|баннер|постер|надпис|signboard|poster|slide|banner|billboard|caption)", O, T);
    private static readonly Regex PeopleRx = new("(?<!\\p{L})(человек|людей|люди|портрет|девушк|парен|мужчин|женщин|ребён|ребен|дет[ие]|лиц[оа]|селфи|person|people|portrait|woman|man\\b|girl|boy|face)", O, T);
    private static readonly Regex PhotoRx = new("(?<!\\p{L})(фото|реалистич|фотореал|photo|realistic)", O, T);
    private static readonly Regex FastRx = new("(?<!\\p{L})(фон|стил|аксессуар|очки|очков|шляп|background|style|accessor)", O, T);
    private static readonly Regex PreciseRx = new("(?<!\\p{L})(одежд|футболк|куртк|платье|плать|рубашк|штан|костюм|пальто|clothes|clothing|shirt|dress|jacket|точн)", O, T);

    public static PromptHints Of(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return default;
        return new PromptHints(Quoted.IsMatch(prompt), SignRx.IsMatch(prompt), PeopleRx.IsMatch(prompt),
            PhotoRx.IsMatch(prompt), FastRx.IsMatch(prompt), PreciseRx.IsMatch(prompt));
    }
}
