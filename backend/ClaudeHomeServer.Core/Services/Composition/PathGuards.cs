namespace ClaudeHomeServer.Services.Composition;

// Путь-гварды: резолв домашней папки + проверка containment.
// Performed в Core: TriggerSources (AutomationRootResolver) использует IsInside
// без прямой ссылки на UserHomeResolver (Main).
public static class PathGuards
{
    /// <summary>
    /// true — <paramref name="path"/> внутри <paramref name="root"/> (или тождественен ему).
    /// Регистр-независимость — по OS.
    /// </summary>
    public static bool IsInside(string path, string root)
    {
        var cp = Path.GetFullPath(path);
        var cr = Path.GetFullPath(root);
        if (cp == cr) return true;
        var prefix = cr.EndsWith(Path.DirectorySeparatorChar) ? cr : cr + Path.DirectorySeparatorChar;
        return cp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
