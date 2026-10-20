using ClaudeHomeServer.Services.Plugins;

namespace ClaudeHomeServer.Services;

// Форвардер Core-шва IPersonaPluginBindingPurger → PersonaBindingsService (QA-фикс L3):
// каскад Plugins (PluginInstallCascade) снимает Skill-привязки plugin:{id} персон владельца
// через этот шов, не ссылаясь на Main напрямую (ADR-014). Namespace `ClaudeHomeServer.Services`
// (Main-корень, exempt от сторожа границ) — а не `…Services.Plugins`: в Plugins-namespace
// сторож атрибутирует тип к вертикали Plugins, и ссылка на PersonaBindingsService (Main)
// стала бы нарушением (Vertical_НеСсылаетсяНаДругиеВертикали).
public sealed class PluginBindingPurgerAdapter(PersonaBindingsService bindings) : IPersonaPluginBindingPurger
{
    public int PurgePluginSkillBindings(string ownerId, string pluginId)
        => bindings.PurgePluginSkillBindings(ownerId, pluginId);
}
