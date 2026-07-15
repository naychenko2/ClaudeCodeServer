using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Русские описания скиллов установленных плагинов (upstream — на английском, имена команд
// не трогаем: их резолвит CLI). Перевод — фоном через SkillTranslationService (haiku);
// кеш персистентный в data/skill-translations.json: ключ — имя скилла, SHA-256 английского
// текста защищает от устаревания при обновлении плагина. До готовности перевода отдаётся
// оригинал — листинг навыков никогда не ждёт LLM.
public class PluginSkillLocalizer
{
    private readonly SkillTranslationService _translation;
    private readonly ILogger<PluginSkillLocalizer> _log;
    private readonly string _storePath;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly object _saveLock = new();
    private int _translating; // single-flight: один фоновый перевод за раз

    private sealed record CacheEntry(string Hash, string Ru);

    // Батч перевода: не грузим весь каталог плагина в один промпт
    private const int BatchSize = 20;

    public PluginSkillLocalizer(SkillTranslationService translation, IConfiguration config,
        ILogger<PluginSkillLocalizer> log)
    {
        _translation = translation;
        _log = log;
        // Стор — в каталоге DataPath (см. правило stores: фолбэк BaseDirectory/data эфемерен в контейнере)
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataDir, "skill-translations.json");
        Load();
    }

    // Подменяет описания на русские из кеша; недостающие ставит в фоновый перевод.
    public IReadOnlyList<SkillInfo> Localize(IReadOnlyList<SkillInfo> skills)
    {
        if (skills.Count == 0) return skills;

        var missing = new List<(string Key, string Text)>();
        var result = new List<SkillInfo>(skills.Count);
        foreach (var s in skills)
        {
            if (string.IsNullOrWhiteSpace(s.Description))
            {
                result.Add(s);
                continue;
            }
            var hash = Sha256(s.Description);
            if (_cache.TryGetValue(s.Name, out var c) && c.Hash == hash)
                result.Add(new SkillInfo
                {
                    Name = s.Name, Description = c.Ru,
                    ArgumentHint = s.ArgumentHint, FilePath = s.FilePath,
                });
            else
            {
                result.Add(s);
                missing.Add((s.Name, s.Description));
            }
        }

        if (missing.Count > 0)
            TranslateInBackground(missing);
        return result;
    }

    private void TranslateInBackground(List<(string Key, string Text)> missing)
    {
        if (Interlocked.CompareExchange(ref _translating, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var batch in missing.Chunk(BatchSize))
                {
                    var translated = await _translation.TranslateDescriptionsAsync(batch);
                    foreach (var (key, text) in batch)
                        if (translated.TryGetValue(key, out var ru) && !string.IsNullOrWhiteSpace(ru))
                            _cache[key] = new CacheEntry(Sha256(text), ru);
                }
                Save();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Фоновый перевод описаний плагиновых скиллов не удался — останутся оригиналы");
            }
            finally
            {
                Interlocked.Exchange(ref _translating, 0);
            }
        });
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(_storePath));
            if (map is null) return;
            foreach (var (k, v) in map)
                _cache[k] = v;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Кеш переводов навыков не прочитался — начну с пустого");
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                File.WriteAllText(_storePath, JsonSerializer.Serialize(
                    _cache.ToDictionary(kv => kv.Key, kv => kv.Value),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Кеш переводов навыков не сохранился");
            }
        }
    }
}
