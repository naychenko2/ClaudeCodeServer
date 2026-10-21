namespace ClaudeHomeServer.Services.Plugins;

// Шов «снять Skill-привязки плагина у персон владельца» (ADR-031 §4, QA-фикс L3).
//
// Каскад удаления установки (PluginInstallCascade, вертикаль Plugins) снимает «внешние»
// ссылки на плагин: Skill-привязки plugin:{id} живут в вертикали персон (PersonaManager,
// Main). По правилу вертикалей (ADR-014) Plugins не ссылается на Main напрямую — их
// снимает этот шов, реализацию регистрирует Main (форвардер в PersonaBindingsService).
//
// Направление: Plugins → Core ← Main. Вызывается один раз на uninstall, после того как
// запись установки уже удалена из стора (идемпотентный повтор — no-op).
public interface IPersonaPluginBindingPurger
{
    // Снять Skill-привязки с target «plugin:{pluginId}/…» у всех персон владельца
    // <paramref name="ownerId"/>. Возвращает число тронутых персон (0 — некого чистить).
    int PurgePluginSkillBindings(string ownerId, string pluginId);
}
