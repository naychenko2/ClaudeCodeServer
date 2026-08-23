namespace ClaudeHomeServer.Models;

/// <summary>
/// Определение фич-флага — декларируется в коде (source of truth).
/// </summary>
/// <param name="Key">Стабильный машинный ключ (kebab-case), по нему хранится override юзера.</param>
/// <param name="Title">Человекочитаемое название для тумблера.</param>
/// <param name="Description">Что включает фича.</param>
/// <param name="Default">Значение по умолчанию, когда у юзера нет override.</param>
/// <param name="Stage">Зрелость: "dev" | "beta" | "stable" — только для метки в UI.</param>
public record FeatureFlagDefinition(
    string Key,
    string Title,
    string Description,
    bool Default,
    string Stage);

/// <summary>
/// Константы ключей флагов — использовать вместо строковых литералов,
/// чтобы опечатка не отключала фичу молча.
/// </summary>
public static class FeatureFlagKeys
{
    // Секция destructive workspace-server: безвозвратное удаление файлов и чатов (files_delete/chats_delete).
    // Предохранитель от необратимого удаления агентом.
    public const string WorkspaceDestructive = "workspace-destructive";

    // Этап 2 «Истории решений» (ADR-004 §5): пассивная секция паспортов в recall промпта
    // персоны + MCP-инструменты dossier_lookup/dossier_get. Второй шаг двухфлаговой выкатки:
    // включается, когда записи перестали быть мусорными (замер качества на проде).
    public const string ChangeDossiersRecall = "change-dossiers-recall";

    // Десктопный агент (ADR-008 о десктопном агенте): чат типа «Десктопный», реестр
    // устройств, сеанс рук и грань desktop_* в ходе. Выключен — ни тип чата, ни включение
    // грани в проекте недоступны, сеанс не стартует, инструменты в ход не доставляются.
    public const string DesktopAgent = "desktop-agent";

    // План «Секции промптов» (dark launch): каталог секций промпта специальности (история,
    // граф кода, процессы, правила роли) + перестановка блока досье рядом с новой секцией
    // «история» + UI каталога в настройках специальностей. Один флаг накрывает весь срез —
    // выключен, промпт и настройки собираются как до фичи (см. ClaudeSession/PersonaMemoryService).
    public const string SpecialtyPromptSections = "specialty-prompt-sections";
}

/// <summary>
/// Единственное место, где объявляются фич-флаги. Чтобы добавить новый флаг —
/// допиши строку в <see cref="All"/> (и продублируй ключ в lib/featureFlags.ts на фронте).
/// </summary>
public static class FeatureFlagCatalog
{
    public static readonly IReadOnlyList<FeatureFlagDefinition> All =
    [
        // Секция destructive workspace-server: files_delete/chats_delete. Без флага секция
        // не выдаётся никому; персоне дополнительно нужен tool-ключ destructive (Tools/привязка).
        // Один из двух оставшихся флагов (второй — change-dossiers-recall): все прочие
        // фичи включены безусловно, а этот — постоянный предохранитель от необратимого
        // удаления (по умолчанию выключен). Механика флагов (сервис, каталог, модалка,
        // /api/feature-flags) оставлена рабочей для будущих флагов.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.WorkspaceDestructive,
            Title: "Разрушающие операции агента",
            Description: "Claude может БЕЗВОЗВРАТНО удалять файлы проектов и чаты через инструменты рабочего пространства (files_delete, chats_delete) — только по явной просьбе. Персоне дополнительно нужна возможность «Удаление (опасно)».",
            Default: false,
            Stage: "dev"),

        // Этап 2 «Истории решений» (ADR-004 §5): паспорта изменений подмешиваются персонам в
        // промпт по коду, который те правят, и доступны через MCP dossier_lookup/dossier_get.
        // Тексты — дословно из заметки «Тексты — Паспорта изменений (UI, empty-state, «Что нового»)».
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.ChangeDossiersRecall,
            Title: "История решений: подсказки персонам и выгрузка в репозиторий",
            Description: "Персоны видят историю решений по коду, который правят, и не предлагают повторно то, что уже отвергли. А саму историю можно выгрузить в репозиторий отдельной веткой — отправка только по вашей кнопке.",
            Default: false,
            Stage: "dev"),

        // Десктопный агент (ADR-008-desktop-agent). Гейтит всю грань целиком: тип чата
        // «Десктопный», включение грани в проекте, реестр устройств и доставку desktop_*
        // в ход. Текст — без обещаний безопасности, которой нет (решение ADR: единственный
        // предохранитель — человек у машины, слова «второй фактор» запрещены).
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.DesktopAgent,
            Title: "Десктопный агент",
            Description: "Агент из песочницы может смотреть на экран вашего компьютера и действовать в окнах — через приложение AI Home Desktop на этой машине. Работает только в чате типа «Десктопный» и только пока вы сами начали сеанс с устройства. Действия идут вне песочницы, с вашими правами.",
            Default: false,
            Stage: "dev"),

        // План «Секции промптов»: каталог секций промпта специальности + перестановка блока
        // досье рядом с секцией «история» (салентность) + UI каталога в настройках специальностей.
        // Текст — из «Текстов для релиза» плана.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.SpecialtyPromptSections,
            Title: "Инструкции и типовые умения для ролей",
            Description: "Настраиваемые секции промпта и типовые умения по специальности персоны: история решений, граф кода, процессы роли. Включите, чтобы увидеть блок в настройках специальностей.",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
