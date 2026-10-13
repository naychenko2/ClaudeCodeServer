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

    // Сферы: проекты группируются в сферы с общей командой персон и хартией; зона персоны
    // «Сфера». Dark launch, выключено по умолчанию
    public const string Spheres = "spheres";

    // Умный подбор фото персонажа под сюжет (ADR-027): метки ракурса и эмоции у фото,
    // автоподбор. Гейтится на сервере. Dark launch, выключено по умолчанию
    public const string CharacterSmartPhotos = "character-smart-photos";

    // Этап 2 «Подставить персонажа на лицо» (ADR-027, §4): тап по лицу на картинке, выбор
    // персонажа, замена лица. Гейтится на сервере. Dark launch, выключено по умолчанию
    public const string CharacterFaceSwap = "character-face-swap";
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
        // Один из трёх оставшихся флагов (ещё spheres и change-dossiers-recall): прочие фичи
        // включены безусловно, а этот — постоянный предохранитель от необратимого удаления
        // (по умолчанию выключен). Механика флагов (сервис, каталог, модалка,
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

        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.Spheres,
            Title: "Сферы",
            Description: "Проекты объединяются в сферы: у сферы своя команда персон и общая хартия, персона сферы видит все проекты сферы.",
            Default: false,
            Stage: "dev"),

        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.CharacterSmartPhotos,
            Title: "Умный подбор фото персонажа",
            Description: "AI Home сам выбирает фото персонажа под сюжет: для «в профиль у окна» возьмёт фото в профиль, для «смеётся» — где он смеётся. Фото размечаются автоматически, метку можно поправить.",
            Default: false,
            Stage: "dev"),

        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.CharacterFaceSwap,
            Title: "Подставить персонажа на лицо",
            Description: "На картинке тапаете по лицу и выбираете персонажа — лицо заменяется, остальное остаётся.",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
