namespace ClaudeHomeServer.Models;

// Правило разрешения на уровне проекта.
// Pattern: "Tool" (любой вызов инструмента) или "Tool(specifier)" с glob по основному
// аргументу (напр. Bash(npm run *), Edit(src/**), WebFetch(domain:*)).
// Action: "allow" — авто-разрешить, "deny" — авто-запретить (deny приоритетнее).
public class PermissionRule
{
    public string Pattern { get; set; } = "";
    public string Action { get; set; } = "allow";
}
