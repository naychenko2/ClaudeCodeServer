using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Пул общих тестовых хостов (фича «Ускорение тестов», Э5): один хост на конфигурацию на процесс
/// тестов вместо хоста на класс. Не <c>ICollectionFixture</c>: коллекция сериализует свои классы, а
/// пул — нет, классы пула идут параллельно, как и шли. Подключение явное — <see cref="PooledHost"/>
/// через <c>IClassFixture</c>.
///
/// В пул нельзя: классы с <c>ExtraServices</c>/<c>ExtraConfig</c>/<c>WithWebHostBuilder</c> (конфиг
/// общего хоста не меняется — сторож PooledHostGuardTests и проверка при каждой выдаче),
/// <c>*DisabledTests</c>, честные e2e и классы, проверяющие «пустое состояние» всего хоста.
/// Изоляция — свой владелец (<see cref="PooledHost.NewOwnerAsync"/>) и свой каталог на класс;
/// общих <c>testuser</c>/<c>seconduser</c> в хосте пула нет вовсе: случайный
/// <c>CreateAuthenticatedClient()</c> падает на входе, а не делит данные с соседями.
/// </summary>
public sealed class SharedHostPool : IDisposable
{
    public const string DefaultKey = "default";

    // Пул процесса: гасится на выходе процесса тестов (xUnit v2 без assembly fixture)
    public static SharedHostPool Shared { get; } = CreateShared();

    private readonly ConcurrentDictionary<string, Lazy<TestWebApplicationFactory>> _hosts = new(StringComparer.Ordinal);
    private int _disposed;

    private static SharedHostPool CreateShared()
    {
        var pool = new SharedHostPool();
        // Остановка хоста на выходе — под потолком: зависшая уборка не должна держать testhost
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Task.Run(pool.Dispose).Wait(TimeSpan.FromSeconds(10));
        return pool;
    }

    /// <summary>Хост конфигурации key; поднимается при первом обращении, дальше — тот же.</summary>
    public TestWebApplicationFactory Get(string key = DefaultKey)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var host = _hosts.GetOrAdd(key, _ => new Lazy<TestWebApplicationFactory>(Build,
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        // Конфиг общего хоста не меняется: правка после сборки молча не подействовала бы,
        // а до сборки — утекла бы во все классы пула
        if (host.ExtraServices is not null || host.ExtraConfig.Count > 0)
            throw new InvalidOperationException(
                $"Хост пула «{key}» получил ExtraServices/ExtraConfig — класс с правкой конфига хоста в пул нельзя, "
                + "ему нужен свой хост (IClassFixture<свой подкласс TestWebApplicationFactory>)");
        return host;
    }

    // Хост собирается ЗДЕСЬ, под Lazy: сборка WebApplicationFactory (EnsureServer) не
    // потокобезопасна, и классы пула, впервые тронувшие Services одновременно, подняли бы два
    // хоста — пользователь заводился в одном, а вход шёл в другой
    private static TestWebApplicationFactory Build()
    {
        var host = new PooledWebApplicationFactory();
        _ = host.Services;
        return host;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var lazy in _hosts.Values)
            if (lazy.IsValueCreated) lazy.Value.Dispose();
    }

    // Хост пула: без общих учёток (владельцы — NewOwnerAsync)
    private sealed class PooledWebApplicationFactory : TestWebApplicationFactory
    {
        protected override IEnumerable<(string Username, string Password, string Role)> InitialUsers() =>
            [("pool-root-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N"), "admin")];
    }
}

/// <summary>Владелец данных в общем хосте: свой пользователь и клиент с его JWT.</summary>
public sealed record TestOwner(string Id, string Username, string Password, HttpClient Client);

/// <summary>
/// Ручка класса на общий хост пула (<c>IClassFixture&lt;PooledHost&gt;</c>): хост общий, каталог —
/// свой на класс. Хост не гасится вместе с классом — его гасит пул на выходе процесса. Владельцев
/// на класс заводит наследник-фикстура в конструкторе (образец — TasksControllerTests.Host).
/// </summary>
public class PooledHost : IDisposable
{
    public PooledHost() : this(SharedHostPool.Shared) { }

    internal PooledHost(SharedHostPool pool, string key = SharedHostPool.DefaultKey)
    {
        Factory = pool.Get(key);
        TempDir = Path.Combine(Factory.TempDir, "class_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(TempDir);
    }

    public TestWebApplicationFactory Factory { get; }

    public IServiceProvider Services => Factory.Services;

    /// <summary>Свой каталог класса внутри TempDir общего хоста.</summary>
    public string TempDir { get; }

    /// <summary>
    /// Новый пользователь с уникальным именем и клиент с его JWT. Зови на класс (в конструкторе
    /// фикстуры-наследника или лениво) либо на тест — владельцы не пересекаются ни с кем.
    /// </summary>
    public async Task<TestOwner> NewOwnerAsync(string role = "admin")
    {
        var username = "owner-" + Guid.NewGuid().ToString("N")[..12];
        var password = Guid.NewGuid().ToString("N");
        var user = Factory.Services.GetRequiredService<UserStore>().Add(username, password, role);
        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new TestOwner(user.Id, username, password, client);
    }

    /// <summary>Синхронная форма для конструкторов тестовых классов.</summary>
    public TestOwner NewOwner(string role = "admin") => NewOwnerAsync(role).GetAwaiter().GetResult();

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(TempDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* уборка — не предмет теста */ }
    }
}
