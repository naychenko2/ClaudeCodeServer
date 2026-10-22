namespace ClaudeHomeServer.Models;

// Режимы прав — соответствуют значениям флага --permission-mode у claude CLI
public enum ClaudeMode { Default, AcceptEdits, Plan, Auto, DontAsk, Bypass }

public static class ClaudeModeExtensions
{
    // Режим нового чата — единственная точка умолчания. Явный режим уважается; без него чат с персоной
    // идёт в AcceptEdits, а не в Auto: «Авто» шлёт КАЖДЫЙ вызов инструмента на серверный классификатор
    // CLI (10–120 с на вызов), хотя встроенные MCP наш обработчик разрешений и так пропускает.
    // Чат без персоны остаётся на прежнем умолчании (Auto).
    public static ClaudeMode ForNewChat(string? requested, bool hasPersona) =>
        Enum.TryParse<ClaudeMode>(requested, true, out var parsed) ? parsed
            : hasPersona ? ClaudeMode.AcceptEdits : ClaudeMode.Auto;

    // Значение флага --permission-mode для claude CLI
    public static string ToCliFlag(this ClaudeMode mode) => mode switch
    {
        ClaudeMode.AcceptEdits => "acceptEdits",
        ClaudeMode.Plan => "plan",
        ClaudeMode.Auto => "auto",
        ClaudeMode.DontAsk => "dontAsk",
        ClaudeMode.Bypass => "bypassPermissions",
        _ => "default",
    };

    // Wire-токен для фронта (совпадает с именами режимов в frontend/src/lib/modes.ts)
    public static string ToWireToken(this ClaudeMode mode) => mode switch
    {
        ClaudeMode.AcceptEdits => "acceptEdits",
        ClaudeMode.Plan => "plan",
        ClaudeMode.Auto => "auto",
        ClaudeMode.DontAsk => "dontAsk",
        ClaudeMode.Bypass => "bypass",
        _ => "default",
    };
}
