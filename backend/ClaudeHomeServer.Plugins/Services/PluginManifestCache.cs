using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Кэш манифестов пакета в памяти процесса (rework 12, minor 4: PluginsController.ManifestFor
// читал ccs-plugin.json с диска на каждый GET /api/plugins — N+1 чтений). Ключ (id, version,
// sha): манифест неизменяем, sha256 пакета однозначно определяет содержимое, поэтому запись
// кэша не «протухает» по содержимому — инвалидация при установке/обновлении/удалении лишь
// освобождает память старых версий. Потолок размера (MaxEntries) — защита от роста при
// бесконечном потоке разных пакетов; превышение → сброс (простой, без LRU-состояния).
//
// Статический (а не DI-синглтон): «в памяти процесса», минимально, без новой регистрации.
// Тред-безопасность: ConcurrentDictionary; Invalidate — атомарное удаление по id.
public static class PluginManifestCache
{
    // Потолок записей: 1000 разных (id, version, sha) — с запасом для MVP; превышение сбрасывает.
    private const int MaxEntries = 1000;

    // (id, version, sha) → манифест (null — не найден, тоже кэшируем: не перечитываем пустоту).
    private static readonly ConcurrentDictionary<(string, string, string), PluginManifest?> _cache = new();

    // Прочитать манифест из кэша или загрузить (loader) и положить в кэш.
    public static PluginManifest? GetOrLoad(string id, string version, string sha, Func<PluginManifest?> loader)
    {
        var key = (id, version, sha);
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var loaded = loader();
        if (_cache.Count >= MaxEntries)
            _cache.Clear(); // простой сброс: не LRU, а «потолок → чистая страница»
        _cache[key] = loaded;
        return loaded;
    }

    // Инвалидация по id: установка/обновление/удаление пакета с этим id освобождает все его
    // версии из кэша (старые (version, sha) больше не читаются текущей записью).
    public static void Invalidate(string id)
    {
        foreach (var key in _cache.Keys)
            if (key.Item1 == id)
                _cache.TryRemove(key, out _);
    }
}
