// Глобальные using для тестов вертикали Memory. По образцу Turn.Tests/Tasks.Tests:
// Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`TheoryAttribute`
// живут в сборке xunit.core, поэтому нужен явный глобальный using.
global using Xunit;
global using FluentAssertions;
