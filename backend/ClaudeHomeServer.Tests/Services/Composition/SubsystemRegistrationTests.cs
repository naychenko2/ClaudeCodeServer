using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Composition;

// Контракт подсистемной регистрации: только инвариантные проверки, пока ни одной
// подсистемы в продукте не заведено. После первой миграции (Video) сюда же ляжет
// сквозной обход `All`-коллекции по образцу `LocalActionRoutingTests.Catalog_AllKeysUnique`.
public class SubsystemRegistrationTests
{
    private sealed class FakeSubsystem : IAppSubsystem
    {
        public string Key { get; }
        public string Title { get; }
        public int RegisterCalls { get; private set; }

        public FakeSubsystem(string key, string title = "fake")
        {
            Key = key;
            Title = title;
        }

        public void Register(IServiceCollection services, IConfiguration config)
        {
            RegisterCalls++;
        }
    }

    private static (IServiceCollection services, IConfiguration config) NewHost()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        return (services, config);
    }

    // Дубликат `Key` — контрактная ошибка: одинаковые секции настроек перетрут
    // друг друга непредсказуемо, тихо проглатывать нельзя.
    [Fact]
    public void AddSubsystems_Throws_OnDuplicateKey()
    {
        var (services, config) = NewHost();
        var a = new FakeSubsystem("video");
        var b = new FakeSubsystem("video");

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddSubsystems(config, a, b));
        Assert.Contains("video", ex.Message);

        // Первая подсистема успела зарегистрироваться до того, как вторая вызвала конфликт —
        // так и задумано: ошибка ловится ДО Register второй, но первая уже отработала.
        Assert.Equal(1, a.RegisterCalls);
        Assert.Equal(0, b.RegisterCalls);
    }

    // null/пустой Key ловим явно: иначе `HashSet.Add(null)` падает с NRE внутри
    // таблицы, а две подсистемы с пустым ключом дают ту же ошибку "дубликат Key=''"
    // без отличия смыслового конфликта от недозаполненности.
    [Fact]
    public void AddSubsystems_Throws_OnNullKey()
    {
        var (services, config) = NewHost();
        var bad = new FakeSubsystem(key: null!);

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddSubsystems(config, bad));
        Assert.Contains("Key", ex.Message);
        Assert.Equal(0, bad.RegisterCalls);
    }

    [Fact]
    public void AddSubsystems_Throws_OnEmptyKey()
    {
        var (services, config) = NewHost();
        var bad = new FakeSubsystem(key: "");

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddSubsystems(config, bad));
        Assert.Contains("Key", ex.Message);
        Assert.Equal(0, bad.RegisterCalls);
    }

    [Fact]
    public void AddSubsystems_Throws_OnWhitespaceKey()
    {
        var (services, config) = NewHost();
        var bad = new FakeSubsystem(key: "   ");

        Assert.Throws<ArgumentException>(() =>
            services.AddSubsystems(config, bad));
    }

    // Порядок `Register` обязан совпадать с порядком в массиве — на нём строятся
    // зависимости между подсистемами (нижний слой → верхний).
    [Fact]
    public void AddSubsystems_CallsRegisterInOrder()
    {
        var (services, config) = NewHost();
        var callOrder = new List<string>();
        IAppSubsystem Make(string key)
        {
            var sub = new FakeSubsystem(key);
            // Подменяем Register через обёртку: оригинал только инкрементит счётчик.
            var original = sub;
            return new OrderedSubsystem(original, key, callOrder);
        }

        services.AddSubsystems(config, Make("base"), Make("middle"), Make("top"));

        Assert.Equal(new[] { "base", "middle", "top" }, callOrder);
    }

    private sealed class OrderedSubsystem : IAppSubsystem
    {
        private readonly FakeSubsystem _inner;
        private readonly string _key;
        private readonly List<string> _log;

        public OrderedSubsystem(FakeSubsystem inner, string key, List<string> log)
        {
            _inner = inner;
            _key = key;
            _log = log;
        }

        public string Key => _inner.Key;
        public string Title => _inner.Title;

        public void Register(IServiceCollection services, IConfiguration config)
        {
            _log.Add(_key);
            _inner.Register(services, config);
        }
    }

    // Возврат той же коллекции — обязателен для композиции с другими `Add*` вызовами
    // (`services.AddSubsystems(...).AddObservability(...)`).
    [Fact]
    public void AddSubsystems_ReturnsSameServicesCollection()
    {
        var (services, config) = NewHost();

        var returned = services.AddSubsystems(config, new FakeSubsystem("video"));

        Assert.Same(services, returned);
    }

    [Fact]
    public void AddSubsystems_Throws_OnNullEntry()
    {
        var (services, config) = NewHost();

        Assert.Throws<ArgumentException>(() =>
            services.AddSubsystems(config, new FakeSubsystem("video"), null!));
    }

    [Fact]
    public void AddSubsystems_Throws_OnNullSubsystemsArray()
    {
        var (services, config) = NewHost();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddSubsystems(config, null!));
    }
}