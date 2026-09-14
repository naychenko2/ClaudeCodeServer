using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Dossiers;

// Разбор трейлеров CCS-Session/CCS-Task из сообщения коммита (ADR-004 §1). Неймспейс CCS-
// защищает от коллизии с чужой конвенцией «Task: JIRA-123» — значение по нераспознанному
// имени трейлера даже не доходит до разбора. Само по себе значение — НЕ доверенный ввод:
// формат и принадлежность проверяет вызывающий (DossierCaptureService), здесь только парсинг.
public static partial class CommitTrailers
{
    [GeneratedRegex(@"(?m)^CCS-Session:[ \t]*(\S+)[ \t]*$", RegexOptions.Compiled)]
    private static partial Regex SessionTrailerRegex();

    [GeneratedRegex(@"(?m)^CCS-Task:[ \t]*(\S+)[ \t]*$", RegexOptions.Compiled)]
    private static partial Regex TaskTrailerRegex();

    // Последнее совпадение — при конкатенации сообщений squash'ем трейлер результирующего
    // (сохраняемого) коммита обычно оказывается ниже трейлеров поглощённых коммитов.
    public static string? ExtractSessionId(string message) => LastMatch(SessionTrailerRegex(), message);

    public static string? ExtractTaskId(string message) => LastMatch(TaskTrailerRegex(), message);

    private static string? LastMatch(Regex regex, string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var matches = regex.Matches(message);
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
    }
}
