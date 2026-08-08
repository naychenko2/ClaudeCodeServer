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

    // Персоны по умолчанию и обязательные онбординги: каждый новый чат человека — с персоной,
    // личная дефолт-персона создаётся в чат-онбординге первого входа, руководитель проекта —
    // в онбординге проекта.
    public const string DefaultPersonasOnboarding = "default-personas-onboarding";
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
        // Один из двух оставшихся флагов (второй — default-personas-onboarding): все прочие
        // фичи включены безусловно, а этот — предохранитель от необратимого удаления
        // (по умолчанию выключен). Механика флагов (сервис, каталог, модалка, /api/feature-flags)
        // оставлена рабочей для будущих флагов.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.WorkspaceDestructive,
            Title: "Разрушающие операции агента",
            Description: "Claude может БЕЗВОЗВРАТНО удалять файлы проектов и чаты через инструменты рабочего пространства (files_delete, chats_delete) — только по явной просьбе. Персоне дополнительно нужна возможность «Удаление (опасно)».",
            Default: false,
            Stage: "dev"),

        // Персоны по умолчанию и обязательные онбординги: инвариант «новый чат человека —
        // только с персоной» + чат-онбординг первого входа (личная дефолт-персона) +
        // чат-онбординг проекта (персона-руководитель). Всё одним релизом за этим флагом.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.DefaultPersonasOnboarding,
            Title: "Персоны по умолчанию и онбординг",
            Description: "Каждый новый чат начинается с персоной. При первом входе обязательный чат-онбординг создаёт вашу личную дефолт-персону, при создании проекта — персону-руководителя проекта; их аватары становятся лицом приложения.",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
