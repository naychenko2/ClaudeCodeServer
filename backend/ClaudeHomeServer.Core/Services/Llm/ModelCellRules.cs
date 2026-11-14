namespace ClaudeHomeServer.Services.Llm;

// Лексика ссылок на модель и проверка ячеек матриц (ADR-007): чистые функции строк,
// без состояния и хранилища — поэтому живут в спине, а не в вертикали. Одной и той же
// проверкой валидируются ячейки матрицы специальности (Llm), этапа шаблона процесса
// (Tasks, разрез ai-fabrika-workflow-cut-2026-11 §1) и матрицы персоны (Main). Доработка
// B1a по R-B1a (п.1): Tasks→Llm как шов вертикалей недопустима (CLAUDE.md, «вертикали»),
// общая часть опущена в Core по образцу LocalActionCatalog/IModelResolver.
public static class ModelCellRules
{
    // Слоты тиров (v2): "tier:strong|medium|weak" — ссылка на именованную модель инстанса
    // (AppSettings.ModelTier*). Легаси-значения "claude"/"default" при чтении маршрута
    // трактуются как tier:medium (LocalActionRouter.Parse) — их никто из владеющих лексикой
    // не переписывает.
    public const string TierPrefix = "tier:";

    public static string TierRoute(ModelTier tier) =>
        TierPrefix + tier.ToString().ToLowerInvariant();

    // Разбор "tier:strong|medium|weak" в слот; иная строка — не тир-маршрут (null).
    public static ModelTier? ParseTierRoute(string route) =>
        route.ToLowerInvariant() switch
        {
            "tier:strong" => ModelTier.Strong,
            "tier:medium" => ModelTier.Medium,
            "tier:weak" => ModelTier.Weak,
            _ => null,
        };

    // Ячейка матрицы — id модели или "preset:{id}"; "tier:*" запрещён (ячейка уже адресована
    // уровнем). null — ячейка валидна. name — для текста ошибки (например «специальность «tester»»).
    // Единственная реализация проверки для всех матриц (специальность, этап, персона) —
    // вызывающие зовут её напрямую или тонким вызовом (SpecialtySettingsStore), не копией.
    public static string? ValidateModelCell(string? cell, string name)
    {
        if (string.IsNullOrWhiteSpace(cell)) return null;
        if (ParseTierRoute(cell) is not null)
            return $"{name}: в ячейке уровня не может быть tier:* — уровень уже выбран строкой";
        return null;
    }
}
