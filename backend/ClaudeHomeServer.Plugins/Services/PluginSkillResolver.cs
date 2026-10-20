using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Plugins;

// Реализация Core-шва IPluginSkillResolver (ADR-031 §2.5, QA-фикс L3):
// проверка «плагин установлен владельцем, включён, навык объявлен, флаг plugins-mvp вкл».
// PersonaBindingsService (Main) вызывает её при СОЗДАНИИ Skill-привязки с target
// «plugin:{id}/{skill}». Направление: Main → Core ← Plugins (ADR-014, прямой ссылки
// Main → Plugins нет — Main берёт шов опциональной зависимостью).
//
// Stateless поверх PluginStore + IFeatureFlagGate: singleton.
public class PluginSkillResolver : IPluginSkillResolver
{
    private readonly PluginStore _store;
    private readonly IFeatureFlagGate _flags;

    public PluginSkillResolver(PluginStore store, IFeatureFlagGate flags)
    {
        _store = store;
        _flags = flags;
    }

    public PluginSkillCheck? Check(string ownerId, string pluginId, string skill)
    {
        // Флаг plugins-mvp гейтит установку (ADR-031 M11): выключен — привязка к навыку
        // плагина преждевременна (не 500, а понятный 400).
        if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.PluginsMvp))
            return new PluginSkillCheck("Флаг plugins-mvp выключен");

        // Плагин установлен ВЛАДЕЛЬЦЕМ (не чужим): запись в installed.json владельца.
        var record = _store.LoadInstalled(ownerId)
            .FirstOrDefault(r => string.Equals(r.Id, pluginId, StringComparison.Ordinal));
        if (record is null)
            return new PluginSkillCheck("Плагин не найден");
        if (!record.Enabled)
            return new PluginSkillCheck("Плагин выключен");

        // Навык объявлен в пакете: манифест (contributes.skills/commands) или фактические
        // файлы skills/*/SKILL.md + commands/*.md (тот же источник, что READ-путь
        // SkillsService.GetOwnerPluginSkills — имя навыка из SKILL.md frontmatter).
        if (!PackageDeclaresSkill(_store, ownerId, record, skill))
            return new PluginSkillCheck("Навык не объявлен в плагине");
        return null;
    }

    // Навык объявлен: либо в манифесте (contributes.skills/commands — пути, имя = хвост
    // пути), либо в фактических файлах пакета (skills/*/SKILL.md — name из frontmatter,
    // commands/*.md — имя файла). Совпадение — регистронезавisible (как в READ-пути).
    private static bool PackageDeclaresSkill(PluginStore store, string ownerId, PluginInstall record, string skill)
    {
        var pkgDir = store.PackageDir(ownerId, record.Id, record.Version);
        if (!Directory.Exists(pkgDir)) return false;

        var manifest = ReadManifest(pkgDir);
        if (manifest?.Contributes is { } c)
        {
            foreach (var path in c.Skills ?? [])
                if (string.Equals(PathTail(path), skill, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var path in c.Commands ?? [])
                if (string.Equals(CommandName(path), skill, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var name in EnumerateSkillNames(pkgDir))
            if (string.Equals(name, skill, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Имена навыков/команд пакета: skills/*/SKILL.md (name из frontmatter, фолбэк — имя
    // папки) + commands/*.md (имя файла без .md). Тот же источник, что READ-путь.
    private static IEnumerable<string> EnumerateSkillNames(string pkgDir)
    {
        var skillsDir = Path.Combine(pkgDir, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (var dir in Directory.GetDirectories(skillsDir))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (File.Exists(skillFile))
                {
                    var name = FrontmatterName(File.ReadAllText(skillFile)) ?? Path.GetFileName(dir);
                    yield return name;
                }
            }
        }
        var commandsDir = Path.Combine(pkgDir, "commands");
        if (Directory.Exists(commandsDir))
        {
            foreach (var file in Directory.GetFiles(commandsDir, "*.md"))
                yield return Path.GetFileNameWithoutExtension(file);
        }
    }

    // `name` из frontmatter SKILL.md (---\nname: …\n---); null — нет frontmatter/name.
    private static string? FrontmatterName(string content)
    {
        if (!content.StartsWith("---")) return null;
        var end = content.IndexOf("\n---", 3);
        if (end < 0) return null;
        foreach (var line in content[3..end].Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                return trimmed["name:".Length..].Trim().Trim('"').Trim('\'');
        }
        return null;
    }

    // Хвост пути навыка: «skills/qa3panel» → «qa3panel».
    private static string PathTail(string path)
    {
        var norm = path.Replace('\\', '/').Trim('/');
        var i = norm.LastIndexOf('/');
        return i < 0 ? norm : norm[(i + 1)..];
    }

    // Имя команды из пути: «commands/foo.md» → «foo».
    private static string CommandName(string path) => Path.GetFileNameWithoutExtension(path);

    // Прочитать манифест пакета (ccs-plugin.json → .claude-plugin/plugin.json); null — нет.
    private static PluginManifest? ReadManifest(string pkgDir)
    {
        foreach (var name in new[] { "ccs-plugin.json", ".claude-plugin/plugin.json" })
        {
            var path = Path.Combine(pkgDir, name);
            if (!File.Exists(path)) continue;
            try
            {
                return JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return null; }
        }
        return null;
    }
}
