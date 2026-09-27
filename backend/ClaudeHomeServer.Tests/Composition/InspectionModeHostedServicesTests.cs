using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож фильтра inspectionMode в Program.cs:1211+ — снимает ВСЕ hosted-сервисы
// из наших сборок (`ClaudeHomeServer` и `ClaudeHomeServer.*`, минус `*.Tests`).
// До этой правки фильтр брал только сборку ClaudeHomeServer, и вынесенные вертикали
// (Llm, Images, Tasks, Notes…) продолжали ехать в инспекции молча. Без этого
// теста следующая вынесенная вертикаль принесла бы ту же дыру молча.
//
// Тест поднимает WebApplicationFactory с InspectionMode=true и проверяет, что ни
// один наш hosted не остался зарегистрированным. Это живая проверка инварианта,
// а не grep по коду — обход через прямой AddHostedService снаружи Program.cs
// тест поймает.
//
// InspectionMode пробрасывается через переменную окружения: WebApplication.CreateBuilder
// подключает env-провайдер ДО любых TestWebApplicationFactory.ConfigureAppConfiguration,
// поэтому чтение в Program.cs:1211 видит значение. (ExtraConfig приходит позже —
// фильтр в Program.cs должен иметь своё позднее чтение, см. комментарий там.)
//
// Тест живёт в ОТДЕЛЬНОЙ коллекции с DisableParallelization: env-переменная
// «InspectionMode» влияет на весь процесс, и параллельные тесты других коллекций
// не должны её перехватывать.
[Collection("InspectionSerial")]
public class InspectionModeHostedServicesTests
{
    private const string EnvVar = "InspectionMode";

    [Fact]
    public void Inspection_СнимаетВсеХостыИзClaudeHomeServerСборок()
    {
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, "true");
        try
        {
            using var factory = new TestWebApplicationFactory();

            var inspectionModeRaw = factory.Services.GetService<IConfiguration>()
                ?.GetValue<bool>("InspectionMode");
            Assert.True(inspectionModeRaw == true,
                $"InspectionMode должен быть true, фактически {inspectionModeRaw}");

            var ownHosted = factory.Services.GetServices<IHostedService>()
                .Where(h =>
                {
                    var n = h.GetType().Assembly.GetName().Name;
                    return n is not null
                        && !n.EndsWith(".Tests", StringComparison.Ordinal)
                        && (n == "ClaudeHomeServer" || n.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal));
                })
                .Select(h => h.GetType().FullName ?? h.GetType().Name)
                .ToList();

            Assert.True(ownHosted.Count == 0,
                "в inspection-режиме остались наши hosted-сервисы: "
                + string.Join(", ", ownHosted));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }
}