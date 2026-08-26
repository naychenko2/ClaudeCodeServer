using System.Text.Json;
using ClaudeHomeServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Сборка SpecialtySettingsStore для юнит-тестов. Стор требует UserStore (роли нужны
/// миграции v4→v5: вливаем личные слои админов), а тестам почти всегда достаточно
/// пустого — этот хелпер прячет обвязку, чтобы конструктор не переписывали в каждом файле.
/// </summary>
public static class TestSpecialtyStore
{
    /// <summary>UserStore на том же DataPath: без users.json он заводит дефолтного админа.</summary>
    public static UserStore Users(IConfiguration config) =>
        new(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);

    /// <summary>
    /// UserStore с заданными пользователями: id и роли важны миграции v4→v5 (вливаем слои
    /// админов в порядке users.json), а UserStore.Add id не принимает — пишем файл сами.
    /// </summary>
    public static UserStore UsersFile(IConfiguration config,
        params (string Id, string Role)[] users)
    {
        var dataPath = config["DataPath"]!;
        var path = Path.Combine(Path.GetDirectoryName(dataPath)!, "users.json");
        // Поля — PascalCase: UserStore читает файл сериализатором без
        // PropertyNameCaseInsensitive, camelCase молча дал бы пользователей с чужими id
        var payload = new
        {
            Version = 1,
            users = users.Select(u => new { u.Id, Username = u.Id, u.Role }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return Users(config);
    }

    /// <summary>Стор настроек специальностей; users не задан — берётся пустой.</summary>
    public static SpecialtySettingsStore Create(IConfiguration config, UserStore? users = null) =>
        new(config, users ?? Users(config), NullLogger<SpecialtySettingsStore>.Instance);
}
