using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Skills;

// Каталог скиллов для снимка промпта (CLI-слой): профильные (configRoot CLAUDE_CONFIG_DIR
// хода) + проектные (projectRoot/.claude/skills). Готовые CliSkillDto — Core-DTO,
// вертикаль Llm не должна знать про SkillInfo из SkillsService.
//
// Реализация собирает оба источника и возвращает единый список (Source ∈ "profile"|"project");
// null/пусто — снимок промпта без секции Skills (как у текущего кода при skills=null).
//
// Метод опционален: Llm зовёт его только если шов не null. Возвращать null из реализации
// тоже можно — это эквивалентно пустому списку (секции Skills в снимке не будет).
public interface ISkillSnapshotSource
{
    IReadOnlyList<CliSkillDto>? GetCliSkills(string projectRootPath, string? configRootPath);
}
