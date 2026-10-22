using ClaudeHomeServer.Services.Plugins;

namespace ClaudeHomeServer.Services.Composition;

// Форвардер Core-шва IPersonaPluginBindingPurger → PersonaBindingsService (QA-фикс L3):
// каскад Plugins (PluginInstallCascade) снимает Skill-привязки plugin:{id} персон владельца
// через этот шов, не ссылаясь на Main напрямую (ADR-014). Лежит в Services/Composition рядом
// с прочими адаптерами швов (PersonaSkillBindingLookup и др.), а не в папке с именем вертикали:
// в namespace Services.Plugins сторож атрибутировал бы тип к вертикали Plugins, и ссылка на
// PersonaBindingsService (Main) стала бы нарушением.
public sealed class PluginBindingPurgerAdapter(PersonaBindingsService bindings) : IPersonaPluginBindingPurger
{
    public int PurgePluginSkillBindings(string ownerId, string pluginId)
        => bindings.PurgePluginSkillBindings(ownerId, pluginId);
}
