using ClaudeHomeServer.Services.ChatContext;

namespace ClaudeHomeServer.Services.Media;

// «Формат» (ADR-025): пропорции и размер результата генерации картинок и видео. Общий тип в Core,
// чтобы обе вертикали округляли и подбирали «ближайшее» одинаково, не ссылаясь друг на друга.
// В JSON — camelCase и строковые enum'ы (как везде в продукте); поля добавляются только аддитивно:
// MediaFormat лежит в префах проекта, а те — в бэкапе.

// Ступень качества: на реальные размеры модели её отображает каталог драйвера (ADR-025 §1)
public enum FormatTier { Standard, High, Max }

// Режим желания: model — «Как у модели» (формат не задан), aspect — пропорции и ступень,
// exact — точная ширина × высота, source — «Как у исходника» (правка картинки)
public static class FormatModes
{
    public const string Model = "model";
    public const string Aspect = "aspect";
    public const string Exact = "exact";
    public const string Source = "source";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Model, Aspect, Exact, Source };
}

// Пост-шаг ступени: что делается с результатом модели, чтобы получить размер ступени. Сейчас один —
// «upscale»: «Макс» у local MiniMax H3 = съёмка full + латентный апскейл до 1440p. Ступень без
// собственного размера модели и без пост-шага серая (решение 4 Андрея: общего апскейла нет)
public static class FormatPostSteps
{
    public const string Upscale = "upscale";
}

public sealed record FormatSize(int W, int H);

// Ступень в каталоге модели. Label — подпись размера для UI («720p», «1792×1024»); Sizes — пиксели по
// пропорциям (пусто, если драйвер отдаёт ступень параметром без размера); WireValue — значение
// параметра модели («768P», «1080p», «half»); PriceFactor — множитель цены, если живой прайс по
// ступеням не различается (тогда цена в ручке с пометкой approx); PostStep — см. FormatPostSteps
public sealed record FormatTierSpec(
    FormatTier Tier,
    string Label,
    IReadOnlyDictionary<string, FormatSize> Sizes,
    string? WireValue = null,
    double PriceFactor = 1,
    string? PostStep = null);

// Правила «Точно…»: кратность стороны, наименьшая сторона, потолок длинной стороны, потолок площади.
// null-поля — ограничения нет
public sealed record ExactSizeSpec(int Step, int MinSide, int? MaxSide = null, double? MaxMegapixels = null);

// Возможности модели по формату. null у модели в каталоге — «Как у модели» (размеров не сообщает).
// ModelLabel — как модель названа в заметках («GPT Image»); WireParam — имя параметра ступени у
// модели («resolution», «size»); NativeNote — «один размер ~1,6 Мп»: причина серых ступеней
public sealed record MediaFormatCaps(
    string ModelLabel,
    IReadOnlyList<string> Aspects,
    IReadOnlyList<FormatTierSpec> Tiers,
    ExactSizeSpec? Exact = null,
    string? WireParam = null,
    string? NativeNote = null);

// Желание человека или агента — единственное, что хранится. Действующий формат считает
// MediaFormatResolver на каждом чтении: подмена при смене модели поэтому временная по построению
public sealed record MediaFormat(
    string Mode,
    string? Aspect,
    FormatTier? Tier,
    int? Width,
    int? Height,
    ContextActor By,
    DateTime At);

// Действующий формат: то, что реально уходит в модель. Не хранится никогда. Substituted — желание
// заменено ближайшим (модель не умеет), Rounded — «Точно…» округлено под модель; Note — фраза для
// человека, дословно по записке («16:9 нет у GPT Image — взяли 3:2.»)
public sealed record ResolvedFormat(
    string Mode,
    string? Aspect,
    FormatTier? Tier,
    int? Width,
    int? Height,
    string? WireValue,
    string? PostStep,
    bool Substituted,
    bool Rounded,
    string? Note);

// Сумма под ступенью: Approx — цена оценочная (множитель или ближайшая ступень), в UI с «~»
public sealed record FormatPrice(double Amount, string Unit, bool Approx = false);

// Строка ступени для ручки format. DisabledReason != null — ступень серая, причина по записке
public sealed record FormatTierOption(
    FormatTier Tier,
    string Label,
    FormatSize? Size,
    string? WireValue,
    string? PostStep,
    FormatPrice? Price,
    string? DisabledReason);

// Ответ ручки GET …/format?provider&model&op (и такой же у видео): всё посчитано на сервере, фронт
// арифметики не ведёт. Wish — сохранённое желание (null — «Как у модели»), Caps — null, если модель
// размеров не сообщает; ExactUnavailableReason — почему «Точно…» нет у модели с Caps
public sealed record MediaFormatState(
    MediaFormatCaps? Caps,
    MediaFormat? Wish,
    ResolvedFormat Resolved,
    IReadOnlyList<FormatTierOption> Tiers,
    ExactSizeSpec? Exact,
    string? ExactUnavailableReason);
