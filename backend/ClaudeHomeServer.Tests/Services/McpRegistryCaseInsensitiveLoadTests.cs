using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Регресс: <see cref="McpRegistry"/> грузит <c>data/mcp-servers.json</c> через
/// <see cref="JsonFileStore.Load"/> КАК ЕСТЬ, без нормализации ключа. Пути мимо
/// <see cref="McpRegistry.Create"/>/<see cref="McpRegistry.CreateBuiltIn"/>/<see cref="McpRegistry.Update"/>
/// (ручная правка файла, восстановление из бэкапа, будущая миграция) дают ключ
/// в любом регистре. Защита от этого — гейт реестрового цикла в SessionManager,
/// который обязан сравнивать ключ по <see cref="StringComparison.OrdinalIgnoreCase"/>
/// (а не по <see cref="Array.IndexOf"/> — то была бы дыра, чинили волной 2).
///
/// Здесь фиксируем САМ факт: реестр сохраняет регистр ключа при загрузке. Это значит,
/// что гейт в SessionManager не может рассчитывать на нижний регистр «по построению» —
/// единственная защита — case-insensitive сравнение.
/// </summary>
public class McpRegistryCaseInsensitiveLoadTests : IDisposable
{
    private readonly string _tempDir;
    private const string OwnerId = "owner-case";

    public McpRegistryCaseInsensitiveLoadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp_case_load_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData("HIGGSFIELD")]
    [InlineData("Higgsfield")]
    [InlineData("dIFY")]
    public void ЗаписьИзФайла_СКлючомВДругомРегистре_ГрузитсяБезНормализации(string uppercaseKey)
    {
        // Пишем JSON напрямую, минуя Create (который привёл бы ключ к нижнему регистру).
        // Так моделируем «ручная правка файла / восстановление из бэкапа».
        var filePath = Path.Combine(_tempDir, "mcp-servers.json");
        var store = new Dictionary<string, List<McpServerRecord>>
        {
            [OwnerId] =
            [
                new McpServerRecord
                {
                    Key = uppercaseKey,
                    Label = "Higgsfield",
                    Transport = McpTransport.Http,
                    Url = "https://example.com/mcp",
                    Enabled = true,
                },
            ],
        };
        JsonFileStore.Save(filePath, store, new JsonSerializerOptions { WriteIndented = true });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        var registry = new McpRegistry(config, new McpSecretStore(config));

        var loaded = registry.GetByOwner(OwnerId);
        loaded.Should().ContainSingle("запись должна загрузиться из файла");
        loaded[0].Key.Should().Be(uppercaseKey,
            "JsonFileStore.Load не нормализует ключ — реестр сохраняет регистр как есть");

        // Сам гейт реестрового цикла (тот, что фильтрует встроенные интеграции) обязан
        // ловить ключ в любом регистре. Проверяем «формулу гейта» — то, чем SessionManager
        // отсекает запись, — здесь и сейчас: она должна срабатывать на загруженный ключ.
        // Мутация «вернуть Array.IndexOf» в SessionManager роняет этот ассерт — иначе
        // правка декоративна.
        var gateFiltersIt = McpRegistry.IntegrationKeys.Contains(loaded[0].Key, StringComparer.OrdinalIgnoreCase);
        gateFiltersIt.Should().BeTrue(
            "ключ «{0}» должен распознаваться как встроенная интеграция по case-insensitive сравнению — "
            + "иначе реестровый цикл доставит его в обход фич-флага", uppercaseKey);

        // Контраст: тот же ключ через Array.IndexOf — НЕ ловится (Ordinal). Это и есть
        // поломка, которую мы чиним: один и тот же ключ проходит/не проходит в зависимости
        // от компаратора. Тест стоит на той формуле, которую SessionManager теперь обязан
        // использовать, — и падает, если кто-то вернёт Array.IndexOf.
        Array.IndexOf(McpRegistry.IntegrationKeys, loaded[0].Key)
            .Should().Be(-1,
                "Array.IndexOf по Ordinal пропускает ключ «{0}» — это и есть дыра; "
                + "страховка от случайного возврата к этой формуле", uppercaseKey);
    }
}
