namespace ClaudeHomeServer.Services.Plugins;

// Реальная реализация шва IPluginInstallCascade (ADR-031 §4, QA-фикс L3): снимает
// «внешние» ссылки на установку плагина — Skill-привязки plugin:{id} персон владельца.
// Заменяет MVP-но-оп (NoOpPluginInstallCascade): с L3 привязки навыков плагинов реально
// создаются, и удаление плагина обязано их убрать, иначе привязка висит «в пустоту»
// (READ-путь её тихо пропускает, но список привязок персоны несёт мёртвую запись).
//
// Skill-привязки живут в вертикали персон (PersonaManager, Main): по правилу вертикалей
// (ADR-014) Plugins не ссылается на Main напрямую — их снимает Core-шов
// IPersonaPluginBindingPurger (реализация — форвардер в Main). Направление:
// Plugins → Core ← Main. Вызывается один раз на uninstall, после удаления записи.
public sealed class PluginInstallCascade : IPluginInstallCascade
{
    private readonly IPersonaPluginBindingPurger _purger;

    public PluginInstallCascade(IPersonaPluginBindingPurger purger)
    {
        _purger = purger;
    }

    public void OnUninstalled(string ownerId, string pluginId)
    {
        // Снять Skill-привязки plugin:{pluginId} у всех персон владельца.
        _purger.PurgePluginSkillBindings(ownerId, pluginId);
    }
}
