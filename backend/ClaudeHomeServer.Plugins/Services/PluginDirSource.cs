using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Plugins;

// Реализация шва IPluginDirSource (ADR-031 §2.5): набор каталогов пакетов для
// --plugin-dir запуска CLI. ClaudeSession (Llm) читает шов опциональной зависимостью
// (null → пустой список), прямой ссылки Llm → Plugins нет (ADR-014).
//
// Набор считается ТОЛЬКО от свойств сессии (владелец, персона, проект) — никогда
// от хода: установка/удаление/смена версии вступают в силу со следующего старта
// процесса CLI (инвариант 4). Фильтры:
//   1) запись включена (Enabled) — выключенный пакет не даёт --plugin-dir;
//   2) проект: в MVP у локального проекта плагинов нет (ProjectCapabilities,
//      инвариант 8) — каталог пакетов и секреты на устройство не едут;
//   3) персона: плагин персоне открывает Skill-биндинг с target plugin:{id}
//      (ADR-031 §2.5); персона задана, но биндинга нет — пакет не открыт.
public class PluginDirSource : IPluginDirSource
{
    private readonly PluginStore _store;
    private readonly IPersonaSkillBindingLookup _personaBindings;

    public PluginDirSource(PluginStore store, IPersonaSkillBindingLookup personaBindings)
    {
        _store = store;
        _personaBindings = personaBindings;
    }

    public IReadOnlyList<string> GetPluginDirs(string ownerId, string? personaId, Project project)
    {
        // Единственная проверка локальности — через ProjectCapabilities (инвариант 8):
        // у локального проекта в MVP плагины выключены, каталог на устройство не едет.
        if (ProjectCapabilities.IsDeviceBound(project)) return [];

        var records = _store.LoadInstalled(ownerId);
        if (records.Count == 0) return [];

        // Персона: personaId null — фильтра нет (все включённые пакеты); персона
        // задана — только пакеты, открытые Skill-биндингом plugin:{id}; персона не
        // найдена — биндингов нет, значит пакетов нет (биндинг — единственный гейт).
        bool hasPersonaFilter = personaId is not null;
        var personaTargets = hasPersonaFilter
            ? _personaBindings.Get(ownerId, personaId)?.SkillBindingTargets
            : null;

        var dirs = new List<string>();
        foreach (var record in records)
        {
            if (!record.Enabled) continue;
            if (hasPersonaFilter
                && (personaTargets is null || !personaTargets.Contains($"plugin:{record.Id}")))
                continue;
            dirs.Add(_store.PackageDir(ownerId, record.Id, record.Version));
        }
        return dirs;
    }
}
