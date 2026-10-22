using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Кэш манифестов пакета в памяти процесса (rework 12, minor 4: PluginsController.ManifestFor
// читал ccs-plugin.json с диска на каждый GET /api/plugins — N+1 чтений). Ключ (ownerId, id,
// version, sha): манифест неизменяем, sha256 пакета однозначно определяет содержимое, поэтому
// запись кэша не «протухает» по содержимому — инвалидация при установке/обновлении/удалении лишь
// освобождает память старых версий.
//
// ownerId в ключе (QA-фикс K, minor 4): ключ (id, version, sha) без владельца безопасен, пока
// sha однозначно определяет содержимое; при пустом sha (PluginInstall.Sha256 по умолчанию "")
// два владельца с одним id+version получали бы чужой манифест. Поэтому: (1) ownerId в ключе,
// (2) пустой sha не кэшируем (loader вызывается напрямую, запись в кэш не идёт).
//
// Потолок размера (MaxEntries) — защита от роста при бесконечном потоке разных пакетов;
// превышение → сброс (простой, без LRU-состояния).
//
// Статический (а не DI-синглтон): «в памяти процесса», минимально, без новой регистрации.
// Тред-безопасность: ConcurrentDictionary; Invalidate — атомарное удаление по (owner, id).
public static class PluginManifestCache
{
    // Потолок записей: 1000 разных (owner, id, version, sha) — с запасом для MVP; превышение сбрасывает.
    private const int MaxEntries = 1000;

    // (ownerId, id, version, sha) → манифест (null — не найден, тоже кэшируем: не перечитываем пустоту).
    private static readonly ConcurrentDictionary<(string, string, string, string), PluginManifest?> _cache = new();

    // Прочитать манифест из кэша или загрузить (loader) и положить в кэш.
    // Пустой sha (PluginInstall.Sha256 по умолчанию "") — не кэшируем: loader вызывается
    // напрямую, запись в кэш не идёт (иначе два владельца с одним id+version и пустым sha
    // пересекались бы).
    public static PluginManifest? GetOrLoad(string ownerId, string id, string version, string sha, Func<PluginManifest?> loader)
    {
        if (string.IsNullOrEmpty(sha))
            return loader();
        var key = (ownerId, id, version, sha);
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var loaded = loader();
        if (_cache.Count >= MaxEntries)
            _cache.Clear(); // простой сброс: не LRU, а «потолок → чистая страница»
        _cache[key] = loaded;
        return loaded;
    }

    // Инвалидация по (owner, id): установка/обновление/удаление пакета владельца освобождает все
    // его версии из кэша (старые (version, sha) больше не читаются текущей записью).
    public static void Invalidate(string ownerId, string id)
    {
        foreach (var key in _cache.Keys)
            if (key.Item1 == ownerId && key.Item2 == id)
                _cache.TryRemove(key, out _);
    }
}
