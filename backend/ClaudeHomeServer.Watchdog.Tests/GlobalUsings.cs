// Глобальные using для тестов вертикали Watchdog. По образцу Deploy.Tests (шаг 3.11) /
// Team.Tests (шаг 3.5): Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`Theory`
// живут в сборке xunit.core, поэтому нужен явный глобальный using. FluentAssertions — по образцу
// тех же эталонов (перенесённые тесты используют его явно, глобальный using — страховка).
global using Xunit;
global using FluentAssertions;
