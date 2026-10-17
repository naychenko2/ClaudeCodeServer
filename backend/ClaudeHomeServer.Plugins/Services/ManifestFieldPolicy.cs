namespace ClaudeHomeServer.Services.Plugins;

// Белый список топ-уровневых полей манифеста пакета (rework C3, ADR-031 §2.2).
// Манифест (plugin.json / ccs-plugin.json) парсится сырым: любой ключ, НЕ в этом списке,
// — запретное поле (deny-manifest-field). Причина: поля формата CLI-плагина (hooks,
// mcpServers, agents, lspServers, outputStyles, settings, enableAllProjectMcpServers, ...)
// исполняют код или обходят allow-list (спайк docs/research/plugin-dir-spike-2026-10.md),
// а наш manifest-model (PluginManifest) их молча игнорирует как forward-compat.
//
// Список = ровно поля, которые хост сам читает/использует; «id» не входит намеренно:
// глобальный id пакета — из записи установки (PluginInstall.Id), а не из топ-уровня
// нативного plugin.json (у того id = name).
public static class ManifestFieldPolicy
{
    // Разрешённые топ-уровневые ключи манифеста (rework C3). `id` — добавлен сверх списка
    // C3: глобальный идентификатор пакета ({publisher}/{slug}) в ccs-plugin.json; безопасный
    // (не исполняет код), в отличие от hooks/mcpServers/agents.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "id",
        "name",
        "version",
        "description",
        "author",
        "license",
        "homepage",
        "repository",
        "keywords",
        "contributes",
        "requires",
        "permissions",
        "bridge",
        "engines",
    };

    /// <summary>Поле манифеста разрешено (в белом списке)?</summary>
    public static bool IsAllowed(string field) => Allowed.Contains(field);
}
