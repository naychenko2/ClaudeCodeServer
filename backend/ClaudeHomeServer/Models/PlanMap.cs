namespace ClaudeHomeServer.Models;

/// <summary>
/// Карта плана — структурный слепок markdown-плана для разворота схемой
/// (план «Контекстные замечания к плану + визуальный разворот», docs/plans/visual-plan.md,
/// часть B). Строится местом plan-map по кнопке «Собрать схему», контракт — зеркало
/// types/index.ts на фронте. Раздел текста и блок схемы — одна сущность:
/// <see cref="PlanMapBlock.Anchor"/> несёт точный текст заголовка раздела, поэтому
/// замечание на блоке в схеме и замечание на заголовке в тексте — одно и то же.
/// </summary>
public class PlanMap
{
    /// <summary>Жанр плана — один из <see cref="PlanMapValues.Genres"/>.</summary>
    public string Genre { get; set; } = "";

    /// <summary>Суть плана одной фразой — шапка разворота.</summary>
    public string OneLine { get; set; } = "";

    /// <summary>Пары «значение — подпись» о масштабе плана (шаги, файлы, волны).</summary>
    public List<PlanMapNumber> Numbers { get; set; } = [];

    /// <summary>Значимые разделы плана. Якорь каждого обязан быть заголовком плана.</summary>
    public List<PlanMapBlock> Blocks { get; set; } = [];
}

/// <summary>Пара «значение — подпись» для шапки разворота.</summary>
public class PlanMapNumber
{
    /// <summary>Значение: «3», «5 КБ», «2 недели».</summary>
    public string Value { get; set; } = "";

    /// <summary>Подпись: «шага», «файлов затрагивается».</summary>
    public string Label { get; set; } = "";
}

/// <summary>Раздел плана как блок схемы.</summary>
public class PlanMapBlock
{
    /// <summary>Короткий стабильный идентификатор, на него ссылаются DependsOn.</summary>
    public string Id { get; set; } = "";

    /// <summary>Заголовок блока одной строкой (не путать с якорем).</summary>
    public string Title { get; set; } = "";

    /// <summary>Тип блока — один из <see cref="PlanMapValues.BlockTypes"/>.</summary>
    public string Type { get; set; } = "";

    /// <summary>Флаги внимания — только из <see cref="PlanMapValues.BlockFlags"/>; валидация
    /// оставляет их не больше чем у <see cref="PlanMapService.MaxFlaggedBlocks"/> блоков.</summary>
    public List<string> Flags { get; set; } = [];

    /// <summary>ТОЧНЫЙ текст заголовка раздела плана (без решёток): по нему фронт прыгает
    /// в раздел текста и клеит замечания. Блок с якорем, которого нет среди заголовков
    /// плана, отбрасывается целиком.</summary>
    public string Anchor { get; set; } = "";

    /// <summary>Id блоков, которые должны закрыться раньше (ветвление схемы).</summary>
    public List<string> DependsOn { get; set; } = [];
}

/// <summary>
/// Белые списки значений карты: модель отвечает свободным текстом, значения вне списков
/// нормализуются сервисом (неизвестный жанр → feature, тип → step, флаг отбрасывается).
/// </summary>
public static class PlanMapValues
{
    public static readonly HashSet<string> Genres =
        ["feature", "fix", "choice", "audit", "framework", "operation"];

    public static readonly HashSet<string> BlockTypes =
        ["step", "decision", "fork", "risk", "criterion", "boundary"];

    public static readonly HashSet<string> BlockFlags =
        ["blocking", "needs-decision", "expands-scope", "has-cost", "review-fix"];
}
