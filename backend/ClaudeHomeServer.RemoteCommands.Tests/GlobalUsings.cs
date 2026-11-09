// Глобальные using для тестов вертикали RemoteCommands. По образцу Deploy.Tests (шаг 3.11) /
// Team.Tests: Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`TheoryAttribute`
// живут в сборке xunit.core, поэтому нужен явный глобальный using.
global using Xunit;
global using FluentAssertions;
