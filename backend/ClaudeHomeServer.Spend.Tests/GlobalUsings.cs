// Глобальные using для тестов Spend: xUnit + FluentAssertions + NullLogger.
// По образцу Knowledge.Tests/Notes.Tests — там тоже есть GlobalUsings.cs
// с тем же набором. Без этого каждый файл тестов дублировал бы 5-6 строк.
global using Xunit;
global using FluentAssertions;
global using Microsoft.Extensions.Logging.Abstractions;
