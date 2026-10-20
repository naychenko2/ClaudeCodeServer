// Глобальные using для тестов-сторожей (Guards.Tests). По образцу Telemetry.Tests
// (шаг 3.14) / Deploy.Tests (шаг 3.11) / Watchdog.Tests (шаг 3.13): SDK-проект не
// подтягивает Xunit автоматически — `Fact`/`Theory` живут в сборке xunit.core, поэтому
// нужен явный глобальный using. FluentAssertions — по образцу тех же эталонов
// (перенесённые сторожи используют его явно, глобальный using — страховка).
global using Xunit;
global using FluentAssertions;
