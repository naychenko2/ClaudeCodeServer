using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Шов «каталоги плагинов для запуска CLI» (ADR-031 §2.4, §2.5).
//
// ClaudeSession (Llm) читает его опциональной зависимостью (?? []): список каталогов
// включённых пакетов владельца, которые передаются через --plugin-dir на старте процесса.
// Вертикаль Plugins реализует: читает installed.json владельца, фильтрует по enabled и
// привязке персоны, возвращает пути. Направление: Llm -> Core <- Plugins, прямой ссылки
// Llm -> Plugins нет (ADR-014).
//
// Набор считается ТОЛЬКО от свойств сессии (владелец, персона, проект), никогда от хода:
// установка/удаление/смена версии вступают в силу со следующего старта процесса CLI
// (ADR-031 §2.5, инвариант 4).
public interface IPluginDirSource
{
    // Каталоги включённых пакетов владельца для запуска CLI. Пустой список — пакетов нет
    // или подсистема выключена. personaId null — без привязки персоны (все включённые).
    IReadOnlyList<string> GetPluginDirs(string ownerId, string? personaId, Project project);
}
