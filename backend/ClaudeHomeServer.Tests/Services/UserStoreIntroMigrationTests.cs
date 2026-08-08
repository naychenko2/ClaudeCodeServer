using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Миграция IntroCompletedAt в UserStore.Load (фича default-personas-onboarding).
// Load бежит на КАЖДОМ старте сервера — поэтому условие обязано быть точным и
// идемпотентным. Главный сторож: пользователь, отложивший знакомство (получил
// заготовку), переживает рестарт БЕЗ проставления даты.
public class UserStoreIntroMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public UserStoreIntroMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_intro_migration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore OpenStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        return new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
    }

    // Пишет users.json с заданными пользователями ДО создания стора — так миграция в Load
    // бежит по предзаполненному состоянию (имитация существующего файла на старте).
    private void WriteUsers(params User[] users)
    {
        var json = JsonSerializer.Serialize(new { Version = 1, users },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_tempDir, "users.json"), json);
    }

    [Fact]
    public void Пользователь_СДефолтом_БезДаты_БезЗаготовки_ПолучаетДату()
    {
        // Существующий пользователь, прошедший «старый» онбординг (дефолт назначен, признака
        // знакомства ещё нет — фича только включается). Миграция закрывает его от приглашения.
        WriteUsers(new User { Username = "u1", DefaultPersonaId = "p-legacy" });

        var store = OpenStore();
        var loaded = store.FindByUsername("u1");

        loaded.Should().NotBeNull();
        loaded!.IntroCompletedAt.Should().NotBeNull("пользователь с дефолтом считается прошедшим знакомство");
        loaded.AssistantPersonaId.Should().BeNull("заготовки у него не было");
        loaded.DefaultPersonaId.Should().Be("p-legacy");
    }

    [Fact]
    public void Сторож_ОтложенноеЗнакомство_СЗаготовкой_ДатуНеПолучает()
    {
        // Пользователь получил заготовку, но знакомство не прошёл (отложил). Рестарт сервера
        // НЕ должен гасить приглашение — иначе без третьего условия миграции оно гаснет у всех.
        WriteUsers(new User
        {
            Username = "u1",
            DefaultPersonaId = "p-assistant",
            AssistantPersonaId = "p-assistant",
        });

        var store = OpenStore();
        var loaded = store.FindByUsername("u1");

        loaded.Should().NotBeNull();
        loaded!.IntroCompletedAt.Should().BeNull("знакомство отложено — приглашение должно пережить рестарт");
    }

    [Fact]
    public void Пользователь_БезДефолта_ДатуНеПолучает()
    {
        WriteUsers(new User { Username = "u1" }); // DefaultPersonaId == null

        var store = OpenStore();
        var loaded = store.FindByUsername("u1");

        loaded.Should().NotBeNull();
        loaded!.IntroCompletedAt.Should().BeNull("без дефолта знакомство не пройдено");
    }

    [Fact]
    public void ПовторныйLoad_ДатуНеПереписывает()
    {
        WriteUsers(new User { Username = "u1", DefaultPersonaId = "p-legacy" });

        var firstDate = OpenStore().FindByUsername("u1")!.IntroCompletedAt;
        firstDate.Should().NotBeNull();

        // Тот же файл, новый старт: миграция уже отработала и сохранилась — дата не должна
        // сдвинуться (иначе каждый рестарт обновлял бы «момент знакомства»).
        var secondDate = OpenStore().FindByUsername("u1")!.IntroCompletedAt;

        secondDate.Should().Be(firstDate);
    }
}
