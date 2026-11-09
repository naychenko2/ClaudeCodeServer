// Глобальные using для тестов вертикали TestRuns. По образцу Mcp.Tests (3.6)/Llm.Tests (3.1–3.3):
// Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`TheoryAttribute`
// живут в сборке xunit.core, поэтому нужен явный глобальный using.
// Перенесённые TestRuns-тесты не имеют явного `using Xunit;`/`using FluentAssertions;`
// (в монолите их покрывал ClaudeHomeServer.Tests/GlobalUsings.cs) — дублируем тот же набор.
global using Xunit;
global using FluentAssertions;
