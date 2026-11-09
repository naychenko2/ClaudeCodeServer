// Глобальные using для тестов вертикали Architecture. По образцу TestRuns.Tests (3.7) /
// ChatContext.Tests (3.8): Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`Theory`
// живут в сборке xunit.core, поэтому нужен явный глобальный using.
// Перенесённые Architecture-тесты не имеют явного `using Xunit;` (в монолите их покрывал
// ClaudeHomeServer.Tests/GlobalUsings.cs) — дублируем тот же набор.
global using Xunit;
global using FluentAssertions;
