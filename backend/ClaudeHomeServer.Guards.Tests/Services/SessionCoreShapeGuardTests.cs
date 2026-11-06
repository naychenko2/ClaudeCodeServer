using System.Reflection;
using System.Runtime.CompilerServices;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Team;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож формы ядра сессий (S3-0 §6, docs/research/session-manager-split-2026-11.md): пять правил
/// для типов namespace <c>ClaudeHomeServer.Services.Sessions</c> (роли, вынесенные из SessionManager).
/// <list type="number">
/// <item>ни один параметр конструктора не <see cref="SessionManager"/> (роль не знает фасад);</item>
/// <item>параметров ≤ 10 без <c>ILogger&lt;T&gt;</c> (новая зависимость — сигнал резать класс);</item>
/// <item><c>Lazy&lt;T&gt;</c> только для пяти именованных разрывов циклов;</item>
/// <item>на типы Services.Sessions ссылаются только SessionManager, сами Services.Sessions и тесты
///   (default-deny: закрывает обход фасада — новые пути входа к per-owner изоляции);</item>
/// <item><c>IServiceProvider</c> в конструкторе — только у поимённого allow-list (одна роль:
///   <c>BuiltinMcpContexts</c>; её ограничение на резолв — комментарий у поля и пункт ревью,
///   рефлексией оно не видно).</item>
/// </list>
/// Правила — чистые функции над набором типов, поэтому «мутация сторожа» доказывается самопроверками
/// на синтетических нарушителях: сторож, который не краснеет, ничего не охраняет.
/// Сборку Main форс-загружаем, иначе сторож проходит вакуумно.
/// </summary>
public class SessionCoreShapeGuardTests
{
    private const string CoreNamespace = "ClaudeHomeServer.Services.Sessions";
    private const int MaxCtorParams = 10;

    // Единственные допустимые аргументы Lazy<T> (пять именованных разрывов циклов)
    private static readonly HashSet<string> AllowedLazy =
    [
        "ITurnStarter", "ITurnMessageSink", "ITeamNotifier", "IFeatureChatLinks", "ReviewTreePool",
    ];

    // Поимённый allow-list правила 5 (у каждой строки — причина). SessionStore, ChatVisibility и
    // ChatDeletion в список не входят: их ленивые резолвы описаны типом (Lazy<T>).
    private static readonly Dictionary<string, string> ServiceProviderAllowList = new()
    {
        ["BuiltinMcpContexts"] = "резолвит лишь McpToolsetRegistry и IDeviceExecChannel (цикл через реестр тулсетов)",
    };

    // Кто, кроме SessionManager и самих Services.Sessions, может ссылаться на типы ядра
    private static readonly Dictionary<string, string> CoreReferrerAllowList = new()
    {
        ["SessionCoreRegistration"] = "регистрация AddSessionCore в DI — единственная точка сборки ядра",
    };

    static SessionCoreShapeGuardTests()
    {
        // Main (ClaudeHomeServer.dll): без явной загрузки сторож в фильтрованном прогоне не видит типов ядра
        _ = typeof(SessionManager).Assembly;
        _ = typeof(SessionStore).Assembly;
    }

    private static Assembly Main => typeof(SessionManager).Assembly;

    private static bool IsGenerated(Type t) =>
        t.IsDefined(typeof(CompilerGeneratedAttribute), false) || t.Name.Contains('<');

    /// <summary>Типы ядра: namespace Services.Sessions, включая вложенные, кроме сгенерированных компилятором.</summary>
    private static List<Type> CoreTypes() =>
        Main.GetTypes().Where(t => t.Namespace == CoreNamespace && !IsGenerated(t)).ToList();

    private static IEnumerable<ConstructorInfo> Ctors(Type t) =>
        t.IsAbstract && t.IsSealed
            ? []
            : t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool Mentions(Type t, Type target) =>
        t == target || (t.IsGenericType && t.GetGenericArguments().Any(a => Mentions(a, target)));

    // ---- правила как чистые функции: возвращают описания нарушений ----

    internal static IEnumerable<string> Rule1NoSessionManagerParam(IEnumerable<Type> types) =>
        from t in types
        from c in Ctors(t)
        from p in c.GetParameters()
        where Mentions(p.ParameterType, typeof(SessionManager))
        select $"{t.Name}: параметр «{p.Name}» — SessionManager (роль не должна знать фасад)";

    internal static IEnumerable<string> Rule2MaxParams(IEnumerable<Type> types) =>
        from t in types
        from c in Ctors(t)
        let count = c.GetParameters().Count(p => !IsLogger(p.ParameterType))
        where count > MaxCtorParams
        select $"{t.Name}: {count} параметров конструктора без ILogger (потолок {MaxCtorParams})";

    private static bool IsLogger(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().FullName == "Microsoft.Extensions.Logging.ILogger`1";

    internal static IEnumerable<string> Rule3LazyOnlyNamed(IEnumerable<Type> types) =>
        from t in types
        from c in Ctors(t)
        from p in c.GetParameters()
        where p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Lazy<>)
        let arg = p.ParameterType.GetGenericArguments()[0]
        where !AllowedLazy.Contains(arg.Name)
        select $"{t.Name}: Lazy<{arg.Name}> — допустимы только {string.Join(", ", AllowedLazy)}";

    internal static IEnumerable<string> Rule5ServiceProviderOnlyAllowListed(IEnumerable<Type> types) =>
        from t in types
        where !ServiceProviderAllowList.ContainsKey(t.Name)
        from c in Ctors(t)
        from p in c.GetParameters()
        where Mentions(p.ParameterType, typeof(IServiceProvider))
        select $"{t.Name}: параметр «{p.Name}» — IServiceProvider (через провайдер роль обходит правила 1 и 2; "
            + "ленивый резолв описывается типом Lazy<T>)";

    /// <summary>Правило 4: кто из <paramref name="referrers"/> ссылается на типы Services.Sessions.</summary>
    internal static IEnumerable<string> Rule4OnlyFacadeReferences(IEnumerable<Type> referrers)
    {
        foreach (var t in referrers)
        {
            if (IsAllowedReferrer(t)) continue;
            var hit = BoundaryIlScanner.CollectAllReferencedTypes(t)
                .FirstOrDefault(r => r.Namespace == CoreNamespace);
            if (hit is not null)
                yield return $"{t.FullName} ссылается на {hit.Name} из Services.Sessions в обход фасада SessionManager";
        }
    }

    private static bool IsAllowedReferrer(Type t)
    {
        var root = t;
        while (root.DeclaringType is not null) root = root.DeclaringType;
        return root == typeof(SessionManager)
            || root.Namespace == CoreNamespace
            || CoreReferrerAllowList.ContainsKey(root.Name);
    }

    // ---- боевые проверки ----

    [Fact]
    public void Набор_типов_ядра_непустой_и_сборка_загружена()
    {
        var names = CoreTypes().Select(t => t.Name).ToList();
        names.Should().Contain(["SessionEntry", "SessionStore"],
            "иначе сторож проходит вакуумно (сборка не загружена или namespace переехал)");
    }

    [Fact]
    public void Правило1_параметр_конструктора_не_SessionManager() =>
        Rule1NoSessionManagerParam(CoreTypes()).Should().BeEmpty();

    [Fact]
    public void Правило2_не_больше_десяти_параметров_без_ILogger() =>
        Rule2MaxParams(CoreTypes()).Should().BeEmpty();

    [Fact]
    public void Правило3_Lazy_только_для_пяти_именованных_типов() =>
        Rule3LazyOnlyNamed(CoreTypes()).Should().BeEmpty();

    [Fact]
    public void Правило5_IServiceProvider_только_у_поимённого_allow_list() =>
        Rule5ServiceProviderOnlyAllowListed(CoreTypes()).Should().BeEmpty();

    [Fact]
    public void Правило4_на_типы_ядра_ссылаются_только_фасад_и_сами_Services_Sessions()
    {
        // Referrers — все типы Main (тесты в Main не лежат). Вложенные обходит сам сканер.
        var referrers = Main.GetTypes().Where(t => t.DeclaringType is null).ToList();
        referrers.Should().Contain(typeof(SessionManager), "иначе скан вакуумный");

        Rule4OnlyFacadeReferences(referrers).Should().BeEmpty();
    }

    [Fact]
    public void Правило4_скан_видит_ссылку_фасада_на_стор()
    {
        // Страховка от вакуумного скана: SessionManager ссылается на SessionStore и скан это находит
        BoundaryIlScanner.CollectAllReferencedTypes(typeof(SessionManager))
            .Should().Contain(typeof(SessionStore));
    }

    [Fact]
    public void Allow_list_правила5_состоит_только_из_BuiltinMcpContexts() =>
        ServiceProviderAllowList.Keys.Should().BeEquivalentTo(["BuiltinMcpContexts"],
            "решение 05.11 (1dda7103): allow-list — одна роль; новая строка требует нового решения Александра");

    // ---- самопроверки: каждое правило обязано краснеть на синтетическом нарушителе ----

    private sealed class FakeTakesManager
    {
        public FakeTakesManager(SessionManager manager) { M = manager; }
        public SessionManager M { get; }
    }

    private sealed class FakeTooMany
    {
        public FakeTooMany(int a1, int a2, int a3, int a4, int a5, int a6, int a7, int a8, int a9, int a10, int a11) { }
    }

    private sealed class FakeTenWithLogger
    {
        public FakeTenWithLogger(int a1, int a2, int a3, int a4, int a5, int a6, int a7, int a8, int a9, int a10,
            Microsoft.Extensions.Logging.ILogger<FakeTenWithLogger> log) { }
    }

    private sealed class FakeLazyForeign
    {
        public FakeLazyForeign(Lazy<string> value) { V = value; }
        public Lazy<string> V { get; }
    }

    private sealed class FakeLazyNamed
    {
        public FakeLazyNamed(Lazy<ReviewTreePool> pool, Lazy<ITeamNotifier> notifier) { }
    }

    private sealed class FakeProvider
    {
        public FakeProvider(IServiceProvider services) { S = services; }
        public IServiceProvider S { get; }
    }

    private sealed class FakeStoreWithProvider
    {
        public FakeStoreWithProvider(IServiceProvider services) { S = services; }
        public IServiceProvider S { get; }
    }

    private sealed class FakeReferrer { public SessionStore? Store; }

    [Fact]
    public void Самопроверка_правило1_краснеет_на_SessionManager_в_конструкторе() =>
        Rule1NoSessionManagerParam([typeof(FakeTakesManager)]).Should().ContainSingle();

    [Fact]
    public void Самопроверка_правило2_краснеет_на_одиннадцати_параметрах_и_не_считает_ILogger()
    {
        Rule2MaxParams([typeof(FakeTooMany)]).Should().ContainSingle();
        Rule2MaxParams([typeof(FakeTenWithLogger)]).Should().BeEmpty("ILogger<T> не считается");
    }

    [Fact]
    public void Самопроверка_правило3_краснеет_на_чужом_Lazy_и_пропускает_именованные()
    {
        Rule3LazyOnlyNamed([typeof(FakeLazyForeign)]).Should().ContainSingle();
        Rule3LazyOnlyNamed([typeof(FakeLazyNamed)]).Should().BeEmpty();
    }

    [Fact]
    public void Самопроверка_правило5_краснеет_на_IServiceProvider_в_сторе()
    {
        // Мутация из постановки: временный IServiceProvider в SessionStore должен покрасить правило 5.
        // SessionStore не может быть изменён в тесте, поэтому нарушитель носит то же имя-роль без права на провайдер.
        Rule5ServiceProviderOnlyAllowListed([typeof(FakeStoreWithProvider)]).Should().ContainSingle();
        Rule5ServiceProviderOnlyAllowListed([typeof(FakeProvider)]).Should().ContainSingle();
    }

    [Fact]
    public void Самопроверка_правило4_краснеет_на_ссылке_постороннего_типа_на_стор() =>
        Rule4OnlyFacadeReferences([typeof(FakeReferrer)]).Should().ContainSingle();
}
