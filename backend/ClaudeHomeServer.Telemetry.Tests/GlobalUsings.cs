// Глобальные using для тестов вертикали Telemetry. По образцу Deploy.Tests (шаг 3.11) /
// Watchdog.Tests (шаг 3.13): Web SDK проекта не подтягивает Xunit автоматически — `Fact`/`Theory`
// живут в сборке xunit.core, поэтому нужен явный глобальный using. FluentAssertions — по образцу
// тех же эталонов (перенесённые тесты используют его явно, глобальный using — страховка).
global using Xunit;
global using FluentAssertions;
