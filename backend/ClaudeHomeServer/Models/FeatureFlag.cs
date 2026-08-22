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

    // Персоны по умолчанию и знакомство: каждый новый чат человека — с персоной; заготовка-
    // ассистент провижнится при первом входе, знакомство дорабатывает её по приглашению.
    public const string DefaultPersonasOnboarding = "default-personas-onboarding";

    // Свой фон у каждого проекта: тайл-дудл и цвет генерируются по смыслу проекта
    // (ADR-008). Выключен — везде рисуется прежний статический фон.
    public const string ProjectBackgrounds = "project-backgrounds";

    // Доклад о завершении делегированной задачи одним сообщением: карточка с действиями
    // («Открыть задачу», «Чат исполнителя») плюс реакция постановщика только по делу.
    // Выключен — прежние два сообщения об одном факте.
    public const string TaskReportCard = "task-report-card";

    // Этап 2 «Истории решений» (ADR-004 §5): пассивная секция паспортов в recall промпта
    // персоны + MCP-инструменты dossier_lookup/dossier_get. Второй шаг двухфлаговой выкатки:
    // включается, когда записи перестали быть мусорными (замер качества на проде).
    public const string ChangeDossiersRecall = "change-dossiers-recall";

    // Стиль озвучки digest: полный ответ на экране + короткая выжимка вслух
    // (Session.VoiceStyle, VoicePrompts.DigestSectionText).
    public const string VoiceDigest = "voice-digest";
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

        // Персоны по умолчанию и знакомство: инвариант «новый чат человека — только
        // с персоной» держится провижном заготовки-ассистента при первом входе, а само
        // знакомство необязательно и открывается из приглашения. Всё за этим флагом.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.DefaultPersonasOnboarding,
            Title: "Персоны по умолчанию и знакомство",
            Description: "Каждый новый чат начинается с персоной. При первом входе ассистент создаётся автоматически; знакомство — по приглашению, а не обязательный экран.",
            Default: false,
            Stage: "dev"),

        // Фон рабочего пространства, нарисованный моделью по смыслу проекта (ADR-008).
        // Гейтит оба POST-эндпоинта фона на бэке и раздел «Оформление» на фронте.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.ProjectBackgrounds,
            Title: "Свой фон у каждого проекта",
            Description: "Рисунок и цвет фона подбираются по смыслу проекта: финансы, поездки, учёба и код больше не выглядят одинаково, а фон совпадает по цвету с иконкой проекта. Не понравился — «Сгенерировать заново» или «Вернуть стандартный» в настройках проекта.",
            Default: false,
            Stage: "dev"),

        // Доклад о завершении делегированной задачи (docs/features/task-completion-report.md):
        // карточка — единственный носитель факта, реакция постановщика — только решение
        // «что дальше». Обе части ходят вместе, поэтому флаг один на карточку и промпт.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.TaskReportCard,
            Title: "Карточка доклада о задаче",
            Description: "Отчёт о выполненной задаче приходит одним сообщением — с кнопкой «Открыть задачу»: результат и файлы видно прямо из чата, не переключаясь на доску. Постановщик отвечает, только если есть что решать.",
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

        // Второй стиль озвучки (VoiceStyles.Digest): ответ на экране полный, вслух читается
        // выжимка из блока <voice> в его конце. Выключен — озвучка работает как раньше,
        // только внутри режима разговора (короткий ответ целиком).
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.VoiceDigest,
            Title: "Озвучивать краткую выжимку ответа",
            Description: "За компьютером ответ приходит полным — с кодом, таблицами и схемами, — а вслух читается только короткая суть в несколько предложений. Стиль озвучки выбирается удержанием кнопки с наушниками и запоминается на этом устройстве.",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
