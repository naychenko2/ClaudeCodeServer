// Глобальные using для тестов вертикали LocalStats. По образцу TestRuns.Tests (3.7)/Llm.Tests (3.1):
// Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`Theory` живут в сборке xunit.core,
// поэтому нужен явный глобальный using.
// Перенесённые LocalStats-тесты не имеют явного `using Xunit;` (LocalModelLiveSinkTests/
// LocalModelTurnMeterTests) — в монолите их покрывал ClaudeHomeServer.Tests/GlobalUsings.cs;
// дублируем тот же набор (FluentAssertions — явный using в каждом файле, глобальный безвреден).
global using Xunit;
global using FluentAssertions;
