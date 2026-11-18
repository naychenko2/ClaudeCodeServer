namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Метка «ход ребёнка» в env запуска (И2, ADR-041). Ставит её ClaudeSession; раннеры, которым
/// знать владельца нечем (песочница), читают метку и не докладывают в ход токен подписки.
/// </summary>
public static class ChildTurnMarker
{
    public const string EnvKey = "CCS_CHILD_TURN";
}
