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
/// <item>параметров ≤ 10 без <c>ILogger&lt;T&gt;</c> (новая зависимость — сигнал резать класс);
///   на типы-данные из <see cref="CoreDataContracts"/> не распространяется: позиционные параметры
///   записи — поля контракта, а не зависимости роли;</item>
/// <item><c>Lazy&lt;T&gt;</c> только для пяти именованных разрывов циклов;</item>
/// <item>на типы Services.Sessions ссылаются только SessionManager, сами Services.Sessions и тесты
///   (default-deny: закрывает обход фасада — новые пути входа к per-owner изоляции); allow-list
///   рефереров опознаётся по полному имени (с пространством имён), а не по имени типа: одноимённый
///   тип из чужого пространства имён — не регистрация, он обязан быть пойман; допуск гранулярный
///   по роли корня (хвост ревью 447ec76b): точка сборки ядра называет роли и швы — это её назначение
///   (DI-композиция, не вызов поведения), вызывающий за ядром — только типы-данные
///   (<see cref="CoreDataContracts"/>); ссылка такого корня на роль или шов краснит правило;</item>
/// <item><c>IServiceProvider</c> в конструкторе — только у поимённого allow-list (одна роль:
///   <c>BuiltinMcpContexts</c>; её ограничение на резолв — комментарий у поля и пункт ревью,
///   рефлексией оно не видно).</item>
/// </list>
/// Плюс трипвайр по самому фасаду (хвост 1f7152c9, не правило над типами пространства имён):
/// <c>SessionManager</c> не упоминает снятый Llm-контракт <c>ICheapTextRunner</c> — ни в какой
/// форме (поле, параметр, свойство), см. <see cref="FacadeForbiddenLlm"/>.
/// Область правил — пространство имён <c>ClaudeHomeServer.Services.Sessions</c>: это соглашение, а не
/// механика. Роли ядра сейчас лежат в нём, поэтому все пять правил их видят; роль, вынесенная в другое
/// пространство имён, уходит из-под правил целиком (правила её не увидят, и правило 4 не пометит ссылок
/// на неё). Вынос роли за пределы пространства имён — отдельный шаг с явным пере-решением по сторожу:
/// расширять правила на <c>ClaudeHomeServer.Services</c> нельзя — под них попали бы фасад и штаб
/// (конструктор фасада больше 30 параметров — правило 2 краснеет на нём сразу), а исключения для них
/// были бы большим именем-списком — тем самым слабым местом, из которого исправлено правило 4;
/// сторож по списку имён ролей — тоже опознавание по имени плюс не проверяется полнота самого списка.
/// Правила — чистые функции над набором типов, поэтому «мутация сторожа» доказывается самопроверками
/// на синтетических нарушителях: сторож, который не краснеет, ничего не охраняет.
/// Сборку Main форс-загружаем, иначе сторож проходит вакуумно.
/// </summary>
public class SessionCoreShapeGuardTests
{
    private const string CoreNamespace = "ClaudeHomeServer.Services.Sessions";
    private const int MaxCtorParams = 10;

    // Единственные допустимые аргументы Lazy<T> (пять именованных разрывов циклов) плюс
    // один переходный: IWorkLoopContinuation (S3-7) — шов живёт ровно до шага 2 выноса
    // цикла «до готово» (TurnContinuation), когда продолжение зовёт ядро через
    // TrySubmitContinuation; после этого список возвращается к пяти.
    private static readonly HashSet<string> AllowedLazy =
    [
        "ITurnStarter", "ITurnMessageSink", "ITeamNotifier", "IFeatureChatLinks", "ReviewTreePool",
        "IWorkLoopContinuation",
    ];

    // Типы-данные ядра: контракты, а не роли. Правило 2 на них не считается — позиционные
    // параметры записи это поля данных (у QueuedMessage их четырнадцать: идентификатор, текст,
    // отправитель, режим…), а лимит стоит про зависимостях роли. Тот же список держит и грань
    // правила 4: допуск data-реферера — ссылка ровно на эти типы, ничего больше (хвост 447ec76b).
    // Новая строка здесь — такое же явное решение, как allow-list правила 5 (состав списка держит
    // самопроверка ниже).
    private static readonly Dictionary<string, string> CoreDataContracts = new()
    {
        ["ClaudeHomeServer.Services.Sessions.QueuedMessage"] =
            "контракт содержимого очереди (S3-1, план §1, row «SessionEntry.cs (типы)»): "
            + "перенесён из фасада дословно, поведения не держит",
        ["ClaudeHomeServer.Services.Sessions.PendingKind"] =
            "enum режима записи очереди (SessionEntry.cs:367, перенесён тем же шагом S3-1): значение, "
            + "не поведение; его называют TaskExecutionService (kind: …Report, строки 2093 и 2541) и "
            + "SessionMessagesController (разбор kind для /pending, строки 273 и 275)",
        ["ClaudeHomeServer.Services.Sessions.PendingQueueFile"] =
            "контракт файла очереди pending.json (PendingQueue.cs:92, перенесён хвостом S3): формат "
            + "данных, не поведение; его держит ChatHistoryService (LoadPending/SavePending, строки 187-193)",
    };

    // Поимённый allow-list правила 5 (у каждой строки — причина). SessionStore, ChatVisibility и
    // ChatDeletion в список не входят: их ленивые резолвы описаны типом (Lazy<T>).
    private static readonly Dictionary<string, string> ServiceProviderAllowList = new()
    {
        ["BuiltinMcpContexts"] = "резолвит лишь McpToolsetRegistry и IDeviceExecChannel (цикл через реестр тулсетов)",
    };

    // Трипвайр по фасаду (хвост 1f7152c9, находка ревью R-S3-8c): параметр `cheap` снят на S3-8c —
    // уточнение заголовка живёт в ChatNaming, раннер он берёт из контейнера напрямую
    // (SessionCoreRegistration.cs:116). Возврат зависимости к фасаду в ЛЮБОЙ форме (поле,
    // параметр конструктора, публичный доступ) воспроизводит DI-цикл
    // SessionManager → ChatNaming → SessionManager: в вариант с полем резолв RefineChatTitle
    // повесил прогон на 9 минут (проба карточки). Сканер покрывает все три формы: IL тел
    // плюс сигнатуры/поля (BoundaryIlScanner.CollectAllReferencedTypes).
    private static readonly Type[] FacadeForbiddenLlm = [typeof(ClaudeHomeServer.Services.Llm.ICheapTextRunner)];

    // Кто, кроме SessionManager и самих Services.Sessions, может ссылаться на типы ядра, и какой
    // гранью. Раньше одна строка снимала с корня запрет на ЛЮБУЮ ссылку в Services.Sessions: такой
    // корень мог молча взять шов (ITurnStarter) или роль ядра (хвост ревью 447ec76b). Теперь грань
    // у списка: точка сборки ядра называет роли и швы, вызывающий за ядром — только типы-данные.
    // Ключ — полное имя типа (с пространством имён), не имя: опознавание по имени пропускало бы
    // одноимённый тип из чужого пространства имён (самопроверка ниже краснеет на нём).
    private static readonly Dictionary<string, string> CoreCompositionRoots = new()
    {
        ["ClaudeHomeServer.Services.Composition.SessionCoreRegistration"] =
            "регистрация AddSessionCore в DI — единственная точка сборки ядра: она зовёт GetRequiredService"
            + "<…> по всем ролям и швам (SessionCoreRegistration.cs:22-333) — это её назначение, а не обход "
            + "фасада; поведение она не вызывает. Пространство имён Composition — по замыслу "
            + "(комментарий у SessionCoreRegistration.cs:5)",
    };

    // Вызывающие контракта очереди, оставшиеся снаружи ядра (S3-1, план §1). Ни один из них
    // не зовёт поведение ядра — вход к per-owner изоляции у них прежний, через фасад
    // SessionManager. Хранители данных очереди (PendingQueue, PendingQueueFile) здесь больше
    // не перечислены: перенесены в Services.Sessions (хвост S3) и стали своими для ядра.
    // Допуск каждого — только на типы из CoreDataContracts (грань правила 4 выше).
    private static readonly Dictionary<string, string> CoreDataReferrerAllowList = new()
    {
        ["ClaudeHomeServer.Services.ChatHistoryService"] =
            "LoadPending/SavePending читают и пишут pending.json: возвращаемый тип, generic-аргумент "
            + "и параметр — Sessions.PendingQueueFile (ChatHistoryService.cs:187-193). После переноса "
            + "хранителя в Services.Sessions (хвост S3) это живая ссылка на тип-данные ядра: строка "
            + "нужна, снятие красит правило 4 (проверено в копии мутации)",
        ["ClaudeHomeServer.Services.TaskExecutionService"] =
            "зовёт фасад (SendOrEnqueueAsync) и передаёт ему константу PendingKind.Report: "
            + "к ядро обращается только значением enum. С задачей fada2814 скан видит такую "
            + "константу по позиции аргумента в сигнатуре вызова — допуск стал живым: снятие "
            + "этой строки красит правило 4 (проверено в копии мутации)",
        ["ClaudeHomeServer.Controllers.SessionMessagesController"] =
            "зовёт фасад (GetPending) и читает Kind для ответа /pending: значение enum "
            + "Sessions.PendingKind (SessionMessagesController.cs:273,275), не поведение ядра",
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
        where !CoreDataContracts.ContainsKey(t.FullName ?? t.Name)
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

    /// <summary>Трипвайр по фасаду: ни один из <paramref name="facades"/> не упоминает снятый
    /// Llm-контракт — ни в какой форме (поле, параметр, свойство).</summary>
    internal static IEnumerable<string> FacadeNoForbiddenLlm(IEnumerable<Type> facades) =>
        from t in facades
        let hit = BoundaryIlScanner.CollectAllReferencedTypes(t)
            .FirstOrDefault(r => FacadeForbiddenLlm.Contains(r))
        where hit is not null
        select $"{t.Name} упоминает {hit.FullName}: зависимость снята на S3-8c, "
            + "уточнение заголовка живёт в ChatNaming, раннер он берёт из контейнера напрямую";

    /// <summary>Правило 4: кто из <paramref name="referrers"/> ссылается на типы Services.Sessions.
    /// Допуск меряется гранью корня: фасад и сами Services.Sessions — целиком; точка сборки ядра —
    /// целиком (роли и швы — её назначение); вызывающий из data-allow-list — только типы-данные.
    /// Ссылка сверх своей грани — нарушение.</summary>
    internal static IEnumerable<string> Rule4OnlyFacadeReferences(IEnumerable<Type> referrers)
    {
        foreach (var t in referrers)
        {
            var scope = ReferrerScope(t);
            if (scope == RefScope.Full) continue;
            var hit = BoundaryIlScanner.CollectAllReferencedTypes(t)
                .FirstOrDefault(r => r.Namespace == CoreNamespace
                    && !(scope == RefScope.DataOnly && IsCoreData(r)));
            if (hit is not null)
                yield return $"{t.FullName} ссылается на {hit.Name} из Services.Sessions в обход фасада "
                    + "SessionManager"
                    + (scope == RefScope.DataOnly ? " (допуск этого корня — только типы-данные ядра)" : "");
        }
    }

    /// <summary>Грань корня-реферера: вложенный тип относится к своему top-level корню (сканер
    /// собирает его вместе с ним).</summary>
    private static RefScope ReferrerScope(Type t)
    {
        var root = t;
        while (root.DeclaringType is not null) root = root.DeclaringType;
        if (root == typeof(SessionManager) || root.Namespace == CoreNamespace) return RefScope.Full;
        var fullName = root.FullName ?? root.Name;
        if (CoreCompositionRoots.ContainsKey(fullName)) return RefScope.Full;
        if (CoreDataReferrerAllowList.ContainsKey(fullName)) return RefScope.DataOnly;
        return RefScope.None;
    }

    private static bool IsCoreData(Type t) => CoreDataContracts.ContainsKey(t.FullName ?? t.Name);

    private enum RefScope { None, DataOnly, Full }

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
    public void Фасад_не_тянет_обратно_ICheapTextRunner() =>
        FacadeNoForbiddenLlm([typeof(SessionManager)]).Should().BeEmpty();

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

    [Fact]
    public void Список_типов_данных_состоит_только_из_контрактов_очереди() =>
        CoreDataContracts.Keys.Should().BeEquivalentTo(
            [
                "ClaudeHomeServer.Services.Sessions.QueuedMessage",
                "ClaudeHomeServer.Services.Sessions.PendingKind",
                "ClaudeHomeServer.Services.Sessions.PendingQueueFile",
            ],
            "тип-данные в ядре — явное решение: на них не считается правило 2, и только на них "
            + "распространяется допуск data-реферера правила 4; новая строка требует его же");

    [Fact]
    public void Самопроверка_правило2_не_считает_поля_записи_данных() =>
        Rule2MaxParams([typeof(QueuedMessage)]).Should().BeEmpty(
            "позиционные параметры контракта очереди — поля данных, а не зависимости роли; "
            + "без строки в CoreDataContracts эта проверка краснеет");

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

    private sealed class FakeReferrerTakesData { public QueuedMessage? Item; }

    // Три формы «возврата» из пробы 1f7152c9: поле, параметр конструктора, публичный доступ.
    private sealed class FakeFacadeField
    {
        internal ClaudeHomeServer.Services.Llm.ICheapTextRunner? Cheap;
    }

    private sealed class FakeFacadeParam
    {
        public FakeFacadeParam(ClaudeHomeServer.Services.Llm.ICheapTextRunner? cheap) { }
    }

    private sealed class FakeFacadeProperty
    {
        public ClaudeHomeServer.Services.Llm.ICheapTextRunner? Cheap { get; }
    }

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

    [Fact]
    public void Самопроверка_правило4_ловит_и_тип_данных_от_недопущенного_корня() =>
        Rule4OnlyFacadeReferences([typeof(FakeReferrerTakesData)]).Should().ContainSingle(
            "типы-данные не становятся свободными для всех: их допуск живёт со строкой data-allow-list, "
            + "а не с тем, что цель ссылки — данные (default-deny правила 4 сохраняется)");

    [Fact]
    public void Самопроверка_правило4_ловится_одноимённый_тип_из_чужого_пространства_имён()
    {
        // Allow-list опознаётся по полному имени: тип с именем допустимой регистрации,
        // но в чужом пространстве имён — не регистрация, ссылка на ядро обязана быть помечена.
        Rule4OnlyFacadeReferences([typeof(SessionCoreRegistration)]).Should().ContainSingle();
    }

    [Fact]
    public void Самопроверка_трипвайр_фасада_краснеет_на_поле_параметре_и_свойстве()
    {
        // Каждая форма возврата из пробы 1f7152c9 обязана краснеть отдельно: скан
        // (BoundaryIlScanner) видит поле через сигнатуры, параметр — через сигнатуру
        // конструктора, свойство — через его тип; ни одна не требует тел методов.
        FacadeNoForbiddenLlm([typeof(FakeFacadeField)]).Should().ContainSingle();
        FacadeNoForbiddenLlm([typeof(FakeFacadeParam)]).Should().ContainSingle();
        FacadeNoForbiddenLlm([typeof(FakeFacadeProperty)]).Should().ContainSingle();
        // Трипвайр не подменяет правило 1: чужой тип (SessionManager) он не ловит.
        FacadeNoForbiddenLlm([typeof(FakeTakesManager)]).Should().BeEmpty();
    }
}

// Подставляемый тип самопроверки правила 4: одноимённый допустимой регистрации
// SessionCoreRegistration, но в тестовом пространстве имён (не Composition).
// При сверке по имени типа проходил бы мимо allow-list — при сверке по полному имени
// обязан быть пойман. В боевой скан правила 4 не попадает: тот обходит только Main.
internal sealed class SessionCoreRegistration
{
    public SessionStore? Store;
}
