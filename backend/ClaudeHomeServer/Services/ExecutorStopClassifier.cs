using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Терминальный отказ хода исполнителя — «дальше работать нечем», а не «ход не удался».
// Отдельно от TurnErrorClassifier намеренно: там классы ФОЛБЭКа (что имеет шанс пройти на
// другой паре «модель × подписка»), и авторизация туда не входит по определению — ротация
// её не лечит. Здесь ровно обратное: причина, при которой перезапускать бессмысленно и
// нужно сказать человеку. Сегодня категория одна (авторизация), поэтому Classify —
// одна точка расширения: появится «модель удалена» и т.п. — добавляется рядом.
public static class ExecutorStopClassifier
{
    // Причина на проводе (TaskItem.ExecutorStopReason): стиль как у api_error_status CLI — snake_case
    public const string AuthFailedReason = "auth_failed";

    /// <summary>
    /// Причина терминальной остановки исполнителя либо null — обычный ход (успех или
    /// рабочая ошибка). errorText — текст ошибки хода (ErrorMessage перед result):
    /// у 401 CLI регулярно отдаёт subtype=success с пустым api_error_status, и текст
    /// оказывается единственным сигналом.
    /// </summary>
    public static string? Classify(ResultMessage result, string? errorText) =>
        IsTerminalAuthFailure(result.ApiErrorStatus, errorText) ? AuthFailedReason : null;

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
