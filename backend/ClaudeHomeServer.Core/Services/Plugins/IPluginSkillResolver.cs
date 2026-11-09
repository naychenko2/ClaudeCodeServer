using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Шов «навык плагина владельца существует» (ADR-031 §2.5, QA-фикс L3).
//
// PersonaBindingsService (Main) проверяет Skill-привязку с target «plugin:{id}/{skill}»
// при СОЗДАНИИ привязки: плагин с таким id установлен ВЛАДЕЛЬЦЕМ ПЕРСОНЫ (не чужим),
// включён, и в его пакете объявлен такой навык. Реализация — в вертикали Plugins
// (PluginSkillResolver): читает installed.json владельца и пакет. Направление:
// Main → Core ← Plugins, прямой зависимости Main → Plugins нет (ADR-014).
//
// Подсистема Plugins выключена (Subsystems:plugins:Enabled=false) → шов не
// зарегистрирован: Main берёт его опциональной зависимостью (null) и сам отвечает
// «Подсистема плагинов выключена» (не 500).
public interface IPluginSkillResolver
{
    // Проверить навык плагина. null — навык существует (плагин установлен владельцем,
    // включён, навык объявлен, флаг plugins-mvp вкл); иначе — причина отказа для
    // понятного 400 («Плагин не найден» / «Плагин выключен» / «Навык не объявлен…» /
    // «Флаг plugins-mvp выключен»).
    PluginSkillCheck? Check(string ownerId, string pluginId, string skill);
}

/// <summary>
/// Причина отказа проверки навыка плагина (ADR-031 §2.5, QA-фикс L3).
/// <see cref="Reason"/> — человекочитаемый текст для 400.
/// </summary>
public sealed record PluginSkillCheck(string Reason);
