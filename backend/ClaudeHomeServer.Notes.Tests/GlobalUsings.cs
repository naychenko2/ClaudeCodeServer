// Глобальные using для тестов Notes: xUnit + FluentAssertions + NullLogger из
// Microsoft.Extensions.Logging.Abstractions (нужен для NullLogger<T>.Instance).
// По образцу CodeGraph.Tests.csproj — там тоже есть GlobalUsings.cs с тем же
// набором. Без этого каждый файл тестов дублировал бы 5-6 строк using-ов.
global using Xunit;
global using FluentAssertions;
global using Microsoft.Extensions.Logging.Abstractions;
