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

    // Сферы: проекты группируются в сферы с общей командой персон и хартией; зона персоны
    // «Сфера». Dark launch, выключено по умолчанию
    public const string Spheres = "spheres";

    // Плагины (ADR-031): установка пакетов владельца, skills/команды через --plugin-dir, панели.
    // Dark launch, выключено по умолчанию.
    public const string PluginsMvp = "plugins-mvp";

    // Плагины (ADR-031): build-it-yourself — агент собирает пакет через MCP-тулсет plugins.
    // Dark launch, выключено по умолчанию.
    public const string PluginsBuildYourself = "plugins-build-yourself";

    // Общие проекты (docs/research/shared-projects-plan-2026-10.md): владелец приглашает
    // участников в проект, у участников он появляется в списке. Рубильник инстанса —
    // SharedProjects:Enabled, читается только через SharedProjectsGate.
    // Dark launch, выключено по умолчанию.
    public const string SharedProjects = "shared-projects";

    // LLM-wiki (ADR-035/036): база знаний, которую ведёт Хранитель. Флаг прячет UI (вкладку
    // «Вики») и источник wiki от агентов; гейт записи в заведённую вику от него не зависит.
    // Dark launch, выключено по умолчанию.
    public const string LlmWiki = "llm-wiki";

    // Детский режим (ADR-041): флаг прячет только интерфейс включения — редактор профиля ребёнка
    // в «Пользователях». Ограничения ребёнка зависят только от профиля: выключенный флаг не
    // снимает ни одного запрета. Dark launch, выключено по умолчанию.
    public const string ChildMode = "child-mode";
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
        // Один из семи оставшихся флагов (ещё spheres, plugins-mvp, plugins-build-yourself, shared-projects, llm-wiki и child-mode): прочие фичи
        // включены безусловно, а этот — постоянный предохранитель от необратимого удаления
        // (по умолчанию выключен). Механика флагов (сервис, каталог, модалка,
        // /api/feature-flags) оставлена рабочей для будущих флагов.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.WorkspaceDestructive,
            Title: "Разрушающие операции агента",
            Description: "Claude может БЕЗВОЗВРАТНО удалять файлы проектов и чаты через инструменты рабочего пространства (files_delete, chats_delete) — только по явной просьбе. Персоне дополнительно нужна возможность «Удаление (опасно)».",
            Default: false,
            Stage: "dev"),

        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.Spheres,
            Title: "Сферы",
            Description: "Проекты объединяются в сферы: у сферы своя команда персон и общая хартия, персона сферы видит все проекты сферы.",
            Default: false,
            Stage: "dev"),

        // Плагины (ADR-031): пакет владельца — навыки/команды через --plugin-dir, панели,
        // прокси net:. Стадия experimental: тумблер в «Экспериментальных функциях», по
        // умолчанию выключен.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PluginsMvp,
            Title: "Плагины (MVP)",
            Description: "Пакеты владельца: навыки и slash-команды агента, панели в интерфейсе и ссылки на MCP-серверы и секреты. Пакет ставит и права выдаёт только человек.",
            Default: false,
            Stage: "experimental"),

        // Плагины (ADR-031): build-it-yourself — агент собирает пакет (черновики) через
        // MCP-тулсет plugins; принять и выдать права может только человек.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PluginsBuildYourself,
            Title: "Плагины: собери сам",
            Description: "Агент собирает пакет плагина (черновик) по вашему промпту; принять его, выдать права и ввести секреты — только вы, кнопкой.",
            Default: false,
            Stage: "experimental"),

        // Общие проекты: приглашение участников по точному имени учётной записи.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.SharedProjects,
            Title: "Общие проекты",
            Description: "Можно открыть свой проект другому человеку: пригласить его по имени учётной записи редактором или читателем, и проект появится у него в списке. Чтобы вас пригласили, флаг должен быть включён и у вас.",
            Default: false,
            Stage: "dev"),

        // LLM-wiki (ADR-035/036): страница с цитатами, которую ведёт Хранитель. Флаг прячет
        // вкладку «Вики» и источник wiki от агентов; в заведённую вику пишет только Хранитель
        // независимо от флага (fail-closed). Dark launch, выключено по умолчанию.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.LlmWiki,
            Title: "База знаний ИИ (Вики)",
            Description: "ИИ ведёт вашу базу знаний: раскладывает новое по страницам с цитатами, замещает устаревшее, отмечает противоречия и проверяет здоровье базы. Заметки и вики живут в одном разделе: навигация в боковых панелях, правка страниц вики человеком вместе с ИИ.",
            Default: false,
            Stage: "dev"),

        // Детский режим (ADR-041): админ включает режим пользователям в песочнице в «Пользователях»:
        // окна «Уроков», дневные минуты, бюджеты и закреплённая модель по API-ключу. Флаг — только
        // интерфейс включения: ограничения ребёнка зависят только от профиля (И1–И3), выключенный
        // флаг не снимает ни одного запрета.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.ChildMode,
            Title: "Детский режим",
            Description: "Включать детям режим «Уроков»: окна времени и дневные минуты, дневный и месячный бюджет, только закреплённая модель по API-ключу. Режим включает админ в «Пользователях»; ограничения действуют независимо от этого переключателя.",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
