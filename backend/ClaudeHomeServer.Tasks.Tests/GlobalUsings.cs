// Глобальные using для тестов вертикали Tasks. По образцу Spend.Tests/Notes.Tests:
// Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`TheoryAttribute`
// живут в сборке xunit.core, поэтому нужен явный глобальный using.
global using Xunit;
global using FluentAssertions;
