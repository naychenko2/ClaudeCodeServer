namespace ClaudeHomeServer.Models;

/// <summary>
/// Target Skill-привязки плагина (ADR-031 §2.5): <c>plugin:{publisher}/{slug}/{skill}</c>.
/// <see cref="PluginId"/> = <c>{publisher}/{slug}</c> (один разделитель), <see cref="Skill"/> — имя
/// навыка (без префикса slug, может содержать <c>/</c>).
///
/// ЕДИНАЯ точка разбора формата: <see cref="ClaudeHomeServer.Services.Plugins.PluginDirSource"/>
/// (сравнение по префиксу id), <c>SkillsService</c> (resolve навыка) и фронт
/// (<c>PluginTargetPicker</c>) пишут и читают один и тот же формат. Рассинхрон
/// «<c>plugin:{id}</c>» (PluginDirSource) vs «<c>plugin:{id}/{skill}</c>» (пикер/SkillsService)
/// — дефект C1: из-за него <c>GetPluginDirs</c> не находил привязку и <c>--plugin-dir</c>
/// не доставлялся.
/// </summary>
public sealed record PluginSkillTarget
{
    /// <summary>Префикс target: «plugin:».</summary>
    public const string Prefix = "plugin:";

    /// <summary>Канонический id плагина: <c>{publisher}/{slug}</c>.</summary>
    public string PluginId { get; }

    /// <summary>Имя навыка (без префикса slug).</summary>
    public string Skill { get; }

    public PluginSkillTarget(string pluginId, string skill)
    {
        PluginId = pluginId;
        Skill = skill;
    }

    /// <summary>Собрать target из id и имени навыка: <c>plugin:{id}/{skill}</c>.</summary>
    public static string Build(string pluginId, string skill) => $"{Prefix}{pluginId}/{skill}";

    /// <summary>
    /// Разобрать target: <c>plugin:{publisher}/{slug}/{skill}</c> → <see cref="PluginId"/> =
    /// <c>{publisher}/{slug}</c>, <see cref="Skill"/> = остаток (может содержать <c>/</c>).
    /// <c>true</c> — формат корректен (есть префикс, минимум три сегмента, skill непустой).
    /// </summary>
    public static bool TryParse(string? target, out PluginSkillTarget parsed)
    {
        parsed = null!;
        if (string.IsNullOrEmpty(target) || !target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = target[Prefix.Length..].Split('/');
        if (parts.Length < 3) return false; // минимум publisher/slug/skill
        var skill = string.Join("/", parts, 2, parts.Length - 2);
        if (string.IsNullOrEmpty(skill)) return false;
        parsed = new PluginSkillTarget($"{parts[0]}/{parts[1]}", skill);
        return true;
    }
}
