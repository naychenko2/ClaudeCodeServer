namespace ClaudeHomeServer.Services.Plugins;

// MVP-реализация шва IPluginInstallCascade (ADR-031 §4, шаг 1.4): «внешние» ссылки
// на установку плагина (Skill-биндинги персон plugin:{id}, размещения панелей в слотах)
// пока не создаются ни одной вертикалью, поэтому каскад их не снимает — no-op.
// Реальный адаптер (через PersonaManager/слоты) приходит с UI-шагом 2.x; до него
// uninstall корректно убирает всё, что живёт в хранилище владельца (пакет, запись,
// черновики, выданные права, секреты).
public sealed class NoOpPluginInstallCascade : IPluginInstallCascade
{
    public void OnUninstalled(string ownerId, string pluginId)
    {
        // no-op: внешних ссылок в MVP ещё нет.
    }
}
