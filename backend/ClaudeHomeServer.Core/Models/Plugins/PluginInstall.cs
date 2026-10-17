using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Запись установки плагина владельца (ADR-031 §2.3), строка в <c>installed.json</c>.
/// Ссылается на пакет по <c>id + version + sha256</c> (пакет неизменяем), хранит
/// <b>выданные</b> права <see cref="Granted"/> отдельно от <b>запрошенных</b>
/// <see cref="Requested"/> (потолок из манифеста), <see cref="Enabled"/> и источник.
/// </summary>
public sealed class PluginInstall
{
    /// <summary>Id пакета <c>{publisher}/{slug}</c> — ключ записи.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Версия пакета, на которую указывает установка.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    /// <summary>SHA-256 пакета (hex): неизменяемость — обновление = новый hash.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    /// <summary>
    /// Запрошенные права (потолок) — копия <c>permissions.requested</c> манифеста на момент
    /// установки. Хранится, чтобы расширение <c>requested</c> при обновлении требовало повторного
    /// согласия (ADR-031 §4).
    /// </summary>
    [JsonPropertyName("requested")] public List<string> Requested { get; set; } = [];
    /// <summary>
    /// Выданные права — подмножество <see cref="Requested"/>: что человек реально одобрил при
    /// установке. Выдать право, которого нет в <see cref="Requested"/>, — 400 (ADR-031 §4).
    /// </summary>
    [JsonPropertyName("granted")] public List<string> Granted { get; set; } = [];
    /// <summary>Включён ли плагин (выключенный не даёт <c>--plugin-dir</c> и не монтирует панели).</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>
    /// Источник установки — метка-строка, а не enum (урок <see cref="McpCatalogRef"/>):
    /// <c>"draft:{slug}"</c>, <c>"upload:{uploadId}"</c>, позже <c>"catalog:{ref}"</c>.
    /// Неизвестные значения при разборе терпимы: откат кода на старую версию не должен
    /// уносить файл владельца в <c>.corrupt</c>.
    /// </summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    /// <summary>
    /// Секреты, введённые владельцем при выдаче прав (ADR-031 §4, шаг 1.4, M7): в записи
    /// установки — только имя (из манифеста <c>requires.secrets[].name</c>) и признак
    /// <see cref="PluginInstallSecret.Set"/> «значение введено». Само значение — в отдельном
    /// per-owner архиве <c>plugin-secrets.json</c> (<c>PluginSecretsStore</c>), чтобы не лежать
    /// в <c>installed.json</c> открытым.
    /// </summary>
    [JsonPropertyName("secrets")] public List<PluginInstallSecret> Secrets { get; set; } = [];
}

/// <summary>
/// Секрет плагина в записи установки (M7): имя (из манифеста) + признак «значение введено».
/// Значение живёт в <c>PluginSecretsStore</c>, не здесь.
/// </summary>
public sealed class PluginInstallSecret
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("set")] public bool Set { get; set; }
}
