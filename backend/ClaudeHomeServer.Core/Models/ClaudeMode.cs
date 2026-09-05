namespace ClaudeHomeServer.Models;

// Режимы прав — соответствуют значениям флага --permission-mode у claude CLI
public enum ClaudeMode { Default, AcceptEdits, Plan, Auto, DontAsk, Bypass }

public static class ClaudeModeExtensions
{
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
