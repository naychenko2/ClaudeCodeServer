using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
///   на контракты фасада (<see cref="SessionCoreFacadeContracts"/>) и данные ядра (<c>CoreDataContracts</c>) не распространяется: позиционные параметры
///   записи — поля контракта, а не зависимости роли;</item>
/// <item><c>Lazy&lt;T&gt;</c> только для пяти именованных разрывов циклов;</item>
/// <item>на типы Services.Sessions ссылаются только SessionManager, сами Services.Sessions и тесты
///   (default-deny: закрывает обход фасада — новые пути входа к per-owner изоляции); allow-list
///   рефереров опознаётся по полному имени (с пространством имён), а не по имени типа: одноимённый
///   тип из чужого пространства имён — не регистрация, он обязан быть пойман; допуск гранулярный
///   по роли корня (хвост ревью 447ec76b): точка сборки ядра называет роли и швы — это её назначение
///   (DI-композиция, не вызов поведения); на «контракты фасада» (тип в сигнатуре члена SessionManager
///   либо наследник Exception, <see cref="SessionCoreFacadeContracts"/>) может ссылаться любой тип Main —
///   допуск на тип; на «данные ядра» (<c>CoreDataContracts</c>, формат хранения вне сигнатур фасада) —
///   только поимённый вызывающий; ссылка на роль или шов краснит правило;</item>
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
    private const string FacadeNamespace = "ClaudeHomeServer.Services";
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

    // Данные ядра: формат хранения, которого нет в сигнатурах фасада (решение лида 0b942ca6). Допуск
    // поимённый на потребителя (CoreDataReferrerAllowList). Типы, которые есть в сигнатуре
    // public/internal члена SessionManager либо наследуют Exception, — «контракт фасада»: они живут
    // в SessionCoreFacadeContracts, и ссылаться на них может любой тип Main (допуск на тип).
    // Правило 2 на оба вида не считается: позиционные параметры записи — поля данных, а не
    // зависимости роли (у QueuedMessage их четырнадцать). Новая строка здесь — явное решение лида.
    private static readonly Dictionary<string, string> CoreDataContracts = new()
    {
        ["ClaudeHomeServer.Services.Sessions.PendingQueueFile"] =
            "контракт файла очереди pending.json (PendingQueue.cs:92, перенесён хвостом S3): формат "
            + "данных, не поведение, и его нет в сигнатурах фасада; его держит ChatHistoryService "
            + "(LoadPending/SavePending, строки 187-193) напрямую, мимо фасада",
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

    // Вызывающие данных ядра (CoreDataContracts), оставшиеся снаружи ядра. Допуск поимённый и только
    // на типы-данные. Ссылки на контракты фасада (SessionCoreFacadeContracts) сюда не заносятся:
    // они разрешены любому типу Main.
    private static readonly Dictionary<string, string> CoreDataReferrerAllowList = new()
    {
        ["ClaudeHomeServer.Services.ChatHistoryService"] =
            "LoadPending/SavePending читают и пишут pending.json: возвращаемый тип, generic-аргумент "
            + "и параметр — Sessions.PendingQueueFile (ChatHistoryService.cs:187-193); тип в сигнатурах "
            + "фасада не встречается, поэтому это поимённый допуск, а не контракт фасада",
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
        where !CoreDataContracts.ContainsKey(t.FullName ?? t.Name) && !SessionCoreFacadeContracts.Contains(t)
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

    /// <summary>Трипвайр обратного ребра (хвост линии ядра): типы ядра не ссылаются ни на один тип
    /// фасада — ни на вложенный в <see cref="SessionManager"/>, ни на top-level, объявленный рядом с
    /// ним (<paramref name="facadeTopLevel"/>, см. <see cref="FacadeTopLevelTypes"/>). Раньше alias
    /// `using X = …SessionManager.X;` прятал такую ссылку, а первая версия сторожа смотрела только на
    /// вложенные типы: top-level (`TurnResult`, `ChatVisibilityResult`) имеют DeclaringType == null и
    /// проходили молча — ребро ядро → фасад оставалось живым при зелёных 68/68.</summary>
    internal static IEnumerable<string> CoreNoFacadeRefs(IEnumerable<Type> coreTypes,
        IReadOnlySet<Type> facadeTopLevel) =>
        from t in coreTypes
        let hit = BoundaryIlScanner.CollectAllReferencedTypes(t)
            .FirstOrDefault(r => r.DeclaringType == typeof(SessionManager) || facadeTopLevel.Contains(r))
        where hit is not null
        select $"{t.FullName} ссылается на {hit.FullName}: контракт, который нужен ядру, живёт в "
            + "Services.Sessions, а не типом фасада (ребро «ядро → фасад»)";

    /// <summary>Top-level типы, объявленные в файле фасада SessionManager.cs: по рефлексии они ничем не
    /// отличаются от прочих типов Services, поэтому набор берётся разбором объявлений в колонке 0
    /// самого файла и разрешается в сборке Main. Сам <see cref="SessionManager"/> в набор не входит
    /// (его ловят правило 1 и параметры конструкторов), вложенные — по DeclaringType.</summary>
    internal static HashSet<Type> FacadeTopLevelTypes(string facadeSource)
    {
        var names = FacadeTopLevelNames(facadeSource);
        return Main.GetTypes()
            .Where(t => t.DeclaringType is null && t.Namespace == FacadeNamespace && names.Contains(t.Name)
                && t != typeof(SessionManager))
            .ToHashSet();
    }

    internal static HashSet<string> FacadeTopLevelNames(string facadeSource) =>
        Regex.Matches(facadeSource,
                @"^(?:public|internal)\s+(?:(?:sealed|static|abstract|partial|readonly)\s+)*(?:class|record(?:\s+(?:class|struct))?|struct|enum|interface)\s+(\w+)",
                RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

    private static string ReadFacadeSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var file = Path.Combine(dir.FullName, "backend", "ClaudeHomeServer", "Services", "SessionManager.cs");
            if (File.Exists(file)) return File.ReadAllText(file);
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Не найден SessionManager.cs: подъём от " + AppContext.BaseDirectory + " не встретил backend/ClaudeHomeServer/Services.");
    }

    /// <summary>Правило 4: кто из <paramref name="referrers"/> ссылается на типы Services.Sessions.
    /// Допуск меряется гранью корня: фасад и сами Services.Sessions — целиком; точка сборки ядра —
    /// целиком (роли и швы — её назначение); вызывающий из data-allow-list — ещё и типы-данные.
    /// Контракты фасада (<see cref="SessionCoreFacadeContracts"/>) разрешены любому типу Main.
    /// Ссылка сверх своей грани — нарушение.</summary>
    internal static IEnumerable<string> Rule4OnlyFacadeReferences(IEnumerable<Type> referrers)
    {
        foreach (var t in referrers)
        {
            var scope = ReferrerScope(t);
            if (scope == RefScope.Full) continue;
            var hit = BoundaryIlScanner.CollectAllReferencedTypes(t)
                .FirstOrDefault(r => r.Namespace == CoreNamespace
                    && !SessionCoreFacadeContracts.Contains(r)
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

    // Вложенный тип-данные (Queued внутри SendAndWaitResult) принадлежит контракту своего корня.
    private static bool IsCoreData(Type t)
    {
        while (t.DeclaringType is not null) t = t.DeclaringType;
        return CoreDataContracts.ContainsKey(t.FullName ?? t.Name);
    }

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
    public void Ядро_не_ссылается_на_типы_фасада_вложенные_и_top_level() =>
        CoreNoFacadeRefs(CoreTypes(), FacadeTopLevelTypes(ReadFacadeSource())).Should().BeEmpty();

    [Fact]
    public void Разбор_файла_фасада_не_вакуумный_и_не_видит_типов_ядра()
    {
        // Файл фасада держит ровно SessionManager; набор top-level пуст ТОЛЬКО потому, что все
        // контракты ушли в ядро. Имя фасада в разборе обязано находиться — иначе регэксп сломан
        // и сторож проходит вакуумно.
        var source = ReadFacadeSource();
        FacadeTopLevelNames(source).Should().Contain(nameof(SessionManager));
        FacadeTopLevelTypes(source).Should().NotContain(t => t.Namespace == CoreNamespace);
    }

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
    public void Точки_сборки_ядра_правила4_состоят_только_из_SessionCoreRegistration() =>
        CoreCompositionRoots.Keys.Should().BeEquivalentTo(
            ["ClaudeHomeServer.Services.Composition.SessionCoreRegistration"],
            "строка здесь даёт корню полный доступ к ядру (грань Full): новая строка требует нового решения "
            + "Александра; точка сборки поведение ролей не вызывает, второй тип сюда не добавляется");

    [Fact]
    public void Вызывающие_данных_правила4_состоят_только_из_ChatHistoryService() =>
        CoreDataReferrerAllowList.Keys.Should().BeEquivalentTo(
            ["ClaudeHomeServer.Services.ChatHistoryService"],
            "допуск на данные ядра поимённый (pending.json пишется мимо фасада); ссылки на контракты "
            + "фасада сюда не заносятся — им хватает строки в SessionCoreFacadeContracts");

    [Fact]
    public void Данные_ядра_состоят_только_из_PendingQueueFile() =>
        CoreDataContracts.Keys.Should().BeEquivalentTo(
            ["ClaudeHomeServer.Services.Sessions.PendingQueueFile"],
            "данные ядра — формат хранения вне сигнатур фасада; новая строка требует решения лида");

    [Fact]
    public void Контракты_фасада_состоят_только_из_названных_типов() =>
        SessionCoreFacadeContracts.All.Keys.Should().BeEquivalentTo(
            [
                "ClaudeHomeServer.Services.Sessions.QueuedMessage",
                "ClaudeHomeServer.Services.Sessions.PendingKind",
                "ClaudeHomeServer.Services.Sessions.DeliveryCause",
                "ClaudeHomeServer.Services.Sessions.SendUserOutcome",
                "ClaudeHomeServer.Services.Sessions.SendAndWaitResult",
                "ClaudeHomeServer.Services.Sessions.ReportUpResult",
                "ClaudeHomeServer.Services.Sessions.GlobalPersonaInSharedException",
                "ClaudeHomeServer.Services.Sessions.TurnTranscriptDamagedException",
                "ClaudeHomeServer.Services.Sessions.RestartResumeResult",
                "ClaudeHomeServer.Services.Sessions.StuckTurnRestartResult",
                "ClaudeHomeServer.Services.Sessions.ChatBranchResult",
                "ClaudeHomeServer.Services.Sessions.IconBatchResult",
                "ClaudeHomeServer.Services.Sessions.TurnResult",
                "ClaudeHomeServer.Services.Sessions.ChatVisibilityOutcome",
                "ClaudeHomeServer.Services.Sessions.ChatVisibilityResult",
                "ClaudeHomeServer.Services.Sessions.WorkspaceMcpPlan",
            ],
            "допуск на тип открывает его любому типу Main: новая строка требует решения лида");

    [Fact]
    public void Каждая_строка_контрактов_фасада_проходит_критерий()
    {
        // Не только состав: каждая строка реально в сигнатуре public/internal члена SessionManager
        // либо наследует Exception; тип существует и лежит в Services.Sessions.
        SessionCoreFacadeContracts.CriterionViolations(
            SessionCoreFacadeContracts.All.Keys, Main, typeof(SessionManager), CoreNamespace).Should().BeEmpty();
        SessionCoreFacadeContracts.SignatureTypes(typeof(SessionManager)).Should().Contain(typeof(PendingKind),
            "иначе сборщик сигнатур вакуумный");
    }

    [Fact]
    public void Данные_ядра_существуют_и_лежат_в_Services_Sessions() =>
        CoreDataContracts.Keys.Where(n => Main.GetType(n)?.Namespace != CoreNamespace).Should().BeEmpty(
            "устаревшая строка данных тихо откроет поимённый допуск");

    [Fact]
    public void Данные_ядра_не_пересекаются_с_контрактами_фасада() =>
        CoreDataContracts.Keys.Intersect(SessionCoreFacadeContracts.All.Keys).Should().BeEmpty(
            "тип, который есть в сигнатуре фасада, — контракт фасада, а не данные с поимённым допуском");

    [Fact]
    public void Самопроверка_правило2_не_считает_поля_записи_данных() =>
        Rule2MaxParams([typeof(QueuedMessage)]).Should().BeEmpty(
            "позиционные параметры контракта очереди — поля данных, а не зависимости роли; "
            + "без строки в SessionCoreFacadeContracts эта проверка краснеет");

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

    public sealed record FakeFacadeTop(int X);

    private sealed class FakeUsesFacadeTop { public FakeFacadeTop? Top; }

    private sealed class FakeUsesFacadeNested { public SessionManager.TeamRunQuota Quota; }

    private sealed class FakeReferrer { public SessionStore? Store; }

    private sealed class FakeReferrerTakesData { public PendingQueueFile? File; }

    private sealed class FakeReferrerTakesContract { public DeliveryCause Cause; public SendAndWaitResult.Queued? Q; }

    // Синтетический «фасад» для самопроверки критерия: тип в сигнатуре и тип вне неё.
    private sealed class FakeFacade
    {
        public Task<ReportUpResult> Report() => throw new NotSupportedException();
        private PendingKind Hidden() => throw new NotSupportedException();
    }

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
    public void Самопроверка_трипвайр_ядра_краснеет_на_ссылке_на_вложенный_тип_фасада()
    {
        CoreNoFacadeRefs([typeof(FakeUsesFacadeNested)], new HashSet<Type>()).Should().ContainSingle();
        CoreNoFacadeRefs([typeof(FakeTakesManager)], new HashSet<Type>()).Should().BeEmpty(
            "сам фасад как параметр — не вложенный тип (его ловит правило 1)");
    }

    [Fact]
    public void Самопроверка_трипвайр_ядра_краснеет_на_top_level_типе_фасада()
    {
        // Тот самый случай ревью e8b4269d: top-level тип фасада (DeclaringType == null) в публичной
        // сигнатуре типа ядра. Набор top-level берётся из разбора исходника фасада — как в боевой проверке.
        var facade = FacadeTopLevelTypes("public record FakeFacadeTop(int X);\npublic sealed record Other(int Y);");
        facade.Should().BeEmpty("в сборке нет таких типов — синтетический набор подаётся явно ниже");
        var top = new HashSet<Type> { typeof(FakeFacadeTop) };
        CoreNoFacadeRefs([typeof(FakeUsesFacadeTop)], top).Should().ContainSingle();
        CoreNoFacadeRefs([typeof(FakeUsesFacadeTop)], new HashSet<Type>()).Should().BeEmpty(
            "без набора top-level сторож слеп — поэтому боевая проверка собирает набор из файла фасада");
        FacadeTopLevelNames("public sealed record Completed(TurnResult R);\npublic enum Z { A }\ninternal static class K {}\n    public record Nested(int A);")
            .Should().BeEquivalentTo("Completed", "Z", "K");
    }

    [Fact]
    public void Самопроверка_правило4_краснеет_на_ссылке_постороннего_типа_на_стор() =>
        Rule4OnlyFacadeReferences([typeof(FakeReferrer)]).Should().ContainSingle();

    [Fact]
    public void Самопроверка_правило4_пропускает_контракт_фасада_любому_типу_и_включая_вложенный() =>
        Rule4OnlyFacadeReferences([typeof(FakeReferrerTakesContract)]).Should().BeEmpty(
            "контракт фасада — допуск на тип, а не на потребителя (вложенный тип сводится к корню)");

    [Fact]
    public void Самопроверка_критерий_контракта_краснеет_на_типе_вне_сигнатуры_и_на_несуществующем()
    {
        const string ns = "ClaudeHomeServer.Services.Sessions";
        var good = SessionCoreFacadeContracts.CriterionViolations(
            [ns + ".ReportUpResult"], Main, typeof(FakeFacade), ns);
        good.Should().BeEmpty("тип в generic-аргументе Task<…> публичного члена — контракт");
        SessionCoreFacadeContracts.CriterionViolations([ns + ".PendingKind"], Main, typeof(FakeFacade), ns)
            .Should().ContainSingle("PendingKind только в private-члене фальшивого фасада");
        SessionCoreFacadeContracts.CriterionViolations([ns + ".PendingQueueFile"], Main, typeof(SessionManager), ns)
            .Should().ContainSingle("данные ядра вне сигнатур фасада критерий не проходят");
        SessionCoreFacadeContracts.CriterionViolations([ns + ".НетТакого"], Main, typeof(SessionManager), ns)
            .Should().ContainSingle();
        SessionCoreFacadeContracts.CriterionViolations(["ClaudeHomeServer.Services.SessionManager"], Main, typeof(SessionManager), ns)
            .Should().ContainSingle("тип не из Services.Sessions");
        SessionCoreFacadeContracts.CriterionViolations([ns + ".GlobalPersonaInSharedException"], Main, typeof(FakeFacade), ns)
            .Should().BeEmpty("наследник Exception проходит критерий без сигнатуры");
    }

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
