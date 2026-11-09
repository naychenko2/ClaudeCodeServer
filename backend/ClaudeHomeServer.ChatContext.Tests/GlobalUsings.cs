// Глобальные using для тестов вертикали ChatContext. По образцу Mcp.Tests (3.6)/TestRuns.Tests (3.7):
// Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`TheoryAttribute`
// живут в сборке xunit.core, поэтому нужен явный глобальный using.
// Перенесённые ChatContext-тесты не имеют явного `using Xunit;`/`using FluentAssertions;`
// (в монолите их покрывал ClaudeHomeServer.Tests/GlobalUsings.cs) — дублируем тот же набор.
global using Xunit;
global using FluentAssertions;
