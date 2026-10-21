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
    // Шов — ОПЦИОННЫЙ (QA-фикс N, major 3): реализацию (PluginBindingPurgerAdapter)
    // регистрирует Main (Program.cs, неусловно), а не сама вертикаль Plugins. При
    // самодостаточной активации подсистемы (тест-контейнер без Main, ADR-014) шов
    // не в контейнере → DI отдаёт null → каскад no-op (Skill-привязки не чистит, но
    // установка/удаление пакета, записи и секретов работают). В бою Main всегда
    // регистрирует шов → null не бывает.
    private readonly IPersonaPluginBindingPurger? _purger;

    // `= null` обязателен: DI считает параметр опциональным только при явном значении
    // по умолчанию (nullable-референс-тип сам по себе DI не делает опциональным).
    public PluginInstallCascade(IPersonaPluginBindingPurger? purger = null)
    {
        _purger = purger;
    }

    public void OnUninstalled(string ownerId, string pluginId)
    {
        // Снять Skill-привязки plugin:{pluginId} у всех персон владельца.
        // Шва нет (подсистема самодостаточна) → no-op.
        _purger?.PurgePluginSkillBindings(ownerId, pluginId);
    }
}
