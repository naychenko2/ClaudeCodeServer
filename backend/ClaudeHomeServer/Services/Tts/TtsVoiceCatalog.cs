namespace ClaudeHomeServer.Services.Tts;

// Белый список голосов синтеза (SpeechKit v3) — по нему проверяется имя из конфига.
//
// Состав проверен живыми запросами 20.08.2026: каждое имя отвечает 200 на v3. Восемь
// голосов достались от v1 (alena, jane, omazh, marina, filipp, zahar, ermil, madirus),
// остальные живут только в v3. `alena` в публичном списке голосов документации не
// значится, но API её принимает — держим, не закладываясь на вечность.
//
// Человекочитаемые подписи и пол голоса сюда НЕ добавлять, пока их некому применять:
// они нужны только форме выбора голоса у персоны. Роли — уже нужны: их задаёт персона.
public static class TtsVoiceCatalog
{
    // Голос, на который падаем при незнакомом имени в конфиге: есть и в v1, и в v3
    public const string Default = "zahar";

    // madirus и madi_ru — одно и то же имя, v3 принимает оба написания
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "alena", "jane", "omazh", "marina", "filipp", "zahar", "ermil", "madirus", "madi_ru",
        "dasha", "julia", "lera", "masha", "alexander", "kirill", "anton",
    };

    // Амплуа голоса: SpeechKit отвечает 400 на роль, которую голос не тянет, поэтому
    // проверяем заранее. Состав замерен прямыми запросами 20.08.2026 — у filipp и madi_ru
    // ролей нет вовсе, у остальных свой набор. Роль — СТРОКА, а не enum: старый бинарь
    // споткнулся бы на десериализации нового enum'а в personas.json, и пришлось бы
    // поднимать версию схемы бэкапа (BackupSchema).
    private static readonly Dictionary<string, string[]> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alena"] = ["good"],
        ["jane"] = ["good", "evil"],
        ["omazh"] = ["evil"],
        ["marina"] = ["friendly", "whisper"],
        ["zahar"] = ["good"],
        ["ermil"] = ["good"],
        ["dasha"] = ["good", "friendly"],
        ["julia"] = ["strict"],
        ["lera"] = ["friendly"],
        ["masha"] = ["good", "strict", "friendly"],
        ["alexander"] = ["good"],
        ["kirill"] = ["good", "strict"],
        ["anton"] = ["good"],
        // filipp, madirus и madi_ru ролей не поддерживают — их здесь нет намеренно
    };

    public static bool IsKnown(string? voice) =>
        !string.IsNullOrWhiteSpace(voice) && Known.Contains(voice.Trim());

    // Роли голоса; пусто — голос говорит только нейтрально. Отсутствие в карте означает
    // именно «ролей нет», а не «голос неизвестен» (проверка имени — отдельно, IsKnown)
    public static IReadOnlyList<string> RolesFor(string? voice) =>
        !string.IsNullOrWhiteSpace(voice) && Roles.TryGetValue(voice.Trim(), out var roles)
            ? roles
            : [];

    public static bool SupportsRole(string? voice, string? role) =>
        !string.IsNullOrWhiteSpace(role)
        && RolesFor(voice).Contains(role.Trim(), StringComparer.OrdinalIgnoreCase);

    // Незнакомый голос не повод глушить озвучку: опечатка в конфиге стоила бы всей речи
    // (фронт запомнил бы отказ и ушёл на голос браузера до конца сессии)
    public static string Resolve(string? voice) => IsKnown(voice) ? voice!.Trim() : Default;
}
