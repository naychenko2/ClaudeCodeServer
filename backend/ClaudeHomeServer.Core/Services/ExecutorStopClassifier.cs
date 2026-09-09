using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Остановка хода исполнителя — «работа не идёт», а не «ход не удался». Отдельно от
// TurnErrorClassifier намеренно: там классы ФОЛБЭКа (что имеет шанс пройти на другой паре
// «модель × подписка»), и авторизация туда не входит по определению — ротация её не лечит.
// Здесь ровно обратное: причины, при которых ход считать выполненным нельзя.
//
// Категории делятся по РЕАКЦИИ, и это различие обязано быть явным у каждого потребителя:
//  • терминальные (IsTerminal) — перезапускать бессмысленно, нужно решение человека
//    (сегодня одна: авторизация);
//  • восстановимые — работа не закончена, но продолжается сама: обрыв сабагента на середине
//    лечится добиванием (SessionManager.NudgeTruncatedSubagentAsync), а не звонком человеку.
// Новая категория добавляется сюда же и обязательно попадает в IsTerminal.
public static class ExecutorStopClassifier
{
    // Причина на проводе (TaskItem.ExecutorStopReason): стиль как у api_error_status CLI — snake_case
    public const string AuthFailedReason = "auth_failed";

    /// <summary>
    /// Сабагент хода замолчал посреди работы (последнее его сообщение — tool_use, отчёта нет).
    /// НЕ терминальная причина: ход не провалился и исполнитель жив — итог просто не готов,
    /// и выдавать обрывок за результат нельзя. Диагностика обрыва — SubagentRunLog.
    /// </summary>
    public const string SubagentTruncatedReason = "subagent_truncated";

    /// <summary>
    /// Обрывы сабагентов повторяются, а добивания исчерпаны (потолок — две попытки).
    /// Терминальная причина: продолжение не помогает, дальше решает человек.
    /// </summary>
    public const string SubagentStuckReason = "subagent_stuck";

    /// <summary>
    /// Сабагенты хода: None — обрывов не было; Truncated — оборвался, добивание в пути;
    /// Stuck — обрывается раз за разом, попытки добивания исчерпаны.
    /// </summary>
    public enum SubagentTurnState { None, Truncated, Stuck }

    /// <summary>
    /// Причина остановки исполнителя либо null — обычный ход (успех или рабочая ошибка).
    /// errorText — текст ошибки хода (ErrorMessage перед result): у 401 CLI регулярно отдаёт
    /// subtype=success с пустым api_error_status, и текст оказывается единственным сигналом.
    /// subagent — судьба сабагентов хода (паспорта прогонов, SubagentRunLog).
    /// Отказ авторизации сильнее: не авторизовавшись, добивать нечего.
    /// </summary>
    public static string? Classify(ResultMessage result, string? errorText,
        SubagentTurnState subagent = SubagentTurnState.None) =>
        IsTerminalAuthFailure(result.ApiErrorStatus, errorText) ? AuthFailedReason
        : subagent switch
        {
            SubagentTurnState.Truncated => SubagentTruncatedReason,
            SubagentTurnState.Stuck => SubagentStuckReason,
            _ => null,
        };

    /// <summary>
    /// Работа встала насовсем — звать человека (true), или продолжится сама (false).
    /// Неизвестная причина (пометку поставил более новый код) считается терминальной:
    /// лишний зов человека безопаснее молчаливо брошенной задачи. А вот null — это
    /// «остановки не было» (обычный ход), и терминальным он быть не может: без явной
    /// проверки на null любой вызов IsTerminal(task.ExecutorStopReason) на непомеченной
    /// задаче решал бы «работа встала, зовите человека».
    /// </summary>
    public static bool IsTerminal(string? reason) =>
        reason is not null && reason != SubagentTruncatedReason;

    // Отказ авторизации у провайдера модели: 401 в статусе ЛИБО канонические формулировки
    // в тексте ошибки. Только по статусу проверять нельзя — на этом перекосе уже стояли
    // трижды (usage limit и ProviderError при пустом api_error_status).
    internal static bool IsTerminalAuthFailure(string? status, string? text)
    {
        var s = status?.Trim();
        if (s is "401" or "authentication_error" or "invalid_api_key") return true;
        if (LooksAuthFailed(s)) return true;
        return LooksAuthFailed(text);
    }

    // Самодостаточные маркеры: в тексте ошибки хода встречаются только у авторизационного
    // отказа провайдера. Прод-образец: «Failed to authenticate. API Error: 401 invalid
    // access token or token expired».
    private static readonly string[] AuthPhrases =
    [
        "invalid access token",
        "invalid api key",
        "invalid_api_key",
        "invalid x-api-key",
        "invalid bearer token",
        "authentication_error",
        "authentication failed",
        "failed to authenticate",
        "could not authenticate",
    ];

    // Код 401 в связке со словом-квалификатором — голое «401» ловить нельзя (текст может
    // ЦИТИРОВАТЬ код). Тот же приём, что у RateLimitCodeMarkers в TurnErrorClassifier.
    private static readonly string[] AuthCodeMarkers =
    [
        "error: 401",
        "error 401",
        "status 401",
        "http 401",
        "(401)",
        "401 unauthorized",
    ];

    // Слабые маркеры: сами по себе живут и в невинном тексте (ход, разбирающий инцидент
    // с протухшим ключом, не должен считаться умершим), поэтому засчитываются только рядом
    // с признаком авторизационного ответа провайдера.
    private static readonly string[] WeakAuthPhrases =
    [
        "token expired",
        "token has expired",
        "expired token",
        "invalid token",
        "unauthorized",
    ];

    private static readonly string[] AuthContextMarkers =
    [
        "api error",
        "api_error",
        "authenticat",
        "access token",
        "api key",
        "api_key",
        "credential",
        "oauth",
        "401",
    ];

    private static bool LooksAuthFailed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var phrase in AuthPhrases)
            if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var marker in AuthCodeMarkers)
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        var weak = false;
        foreach (var phrase in WeakAuthPhrases)
            if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase)) { weak = true; break; }
        if (!weak) return false;
        foreach (var marker in AuthContextMarkers)
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
