namespace ClaudeHomeServer.Services.Skills;

// Тела запросов панели навыков: их принимают и SkillsController сервера, и localhost-API
// агента устройства (ADR-016, задача 4.3) — форма общая, расхождение держит компилятор.
public record SkillContentRequest(string Content);
public record CreateSkillRequest(string Name, string Content);
