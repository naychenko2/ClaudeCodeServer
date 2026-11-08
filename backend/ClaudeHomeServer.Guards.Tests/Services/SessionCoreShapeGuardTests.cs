using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Team;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож формы ядра сессий (S3-0 §6, docs/research/session-manager-split-2026-11.md): пять правил
/// для типов namespace <c>ClaudeHomeServer.Services.Sessions</c> (роли, вынесенные из SessionManager),
/// плюс два трипвайра по фасадному ребру: снятый Llm-контракт и запрет самого фасада как типа ядра
/// (R-ФИНАЛ c1, хвост ревью 3abaaab0).
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
/// форме (поле, параметр, свойство), см. <see cref="FacadeForbiddenLlm"/>. И обратное фасадное
/// ребро (R-ФИНАЛ c1, хвост 3abaaab0): ни один тип ядра не упоминает сам <c>SessionManager</c> —
/// прежний отказ («его ловит правило 1») держался только для параметров конструктора: статический
/// вызов <c>SessionManager.X(…)</c> или параметр метода типа фасада проходил незамеченным.
/// Точка сборки вне проверки по пространству имён (Composition), её ленивые резолвы
/// <c>GetRequiredService&lt;SessionManager&gt;()</c> (SessionCoreRegistration.cs:36,112) допустимы.
/// Область правил — пространство имён <c>ClaudeHomeServer.Services.Sessions</c>: это соглашение, а не
/// механика. Роли ядра сейчас лежат в нём, поэтому все пять правил их видят; роль, вынесенная в другое
/// пространство имён, уходит из-под правил целиком (правила её не увидят, и правило 4 не пометит ссылок
/// на неё). Вынос роли за пределы пространства имён — отдельный шаг с явным пере-решением по сторожу:
/// расширять правила на <c>ClaudeHomeServer.Services</c> нельзя — под них попали бы фасад и штаб
/// (конструктор фасада больше 30 параметров — правило 2 краснеет на нём сразу), а исключения для них
/// были бы большим именем-списком — тем самым слабым местом, из которого исправлено правило 4;
/// сторож по списку имён ролей — тоже опознавание по имени плюс не проверяется полнота самого списка.
/// Плановое правило §5 «класс зависит только от классов прежних шагов» здесь не охраняется и не
/// является сторожем: причина и граница того, что всё же охраняется, записаны в карте §6
/// (R-ФИНАЛ c2).
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
    // Опознавание по полному имени: одноимённый тип из чужого пространства имён не разрыв цикла.
    private static readonly HashSet<string> AllowedLazy =
    [
        "ClaudeHomeServer.Services.Sessions.ITurnStarter",
        "ClaudeHomeServer.Services.Sessions.ITurnMessageSink",
        "ClaudeHomeServer.Services.Team.ITeamNotifier",
        "ClaudeHomeServer.Services.IFeatureChatLinks",
        "ClaudeHomeServer.Services.ReviewTreePool",
        "ClaudeHomeServer.Services.Sessions.IWorkLoopContinuation",
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
    // Ключ — полное имя типа: одноимённая роль из чужого пространства имён провайдера не получает.
    private static readonly Dictionary<string, string> ServiceProviderAllowList = new()
    {
        ["ClaudeHomeServer.Services.Sessions.BuiltinMcpContexts"] = "резолвит лишь McpToolsetRegistry и IDeviceExecChannel (цикл через реестр тулсетов)",
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
        where !AllowedLazy.Contains(arg.FullName ?? arg.Name)
        select $"{t.Name}: Lazy<{arg.FullName ?? arg.Name}> — допустимы только {string.Join(", ", AllowedLazy)}";

    internal static IEnumerable<string> Rule5ServiceProviderOnlyAllowListed(IEnumerable<Type> types) =>
        from t in types
        where !ServiceProviderAllowList.ContainsKey(t.FullName ?? t.Name)
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
    /// фасада — ни на сам <see cref="SessionManager"/>, ни на вложенный в него, ни на top-level,
    /// объявленный рядом с ним (<paramref name="facadeTopLevel"/>, см. <see cref="FacadeTopLevelTypes"/>).
    /// Раньше alias `using X = …SessionManager.X;` прятал такую ссылку, а первая версия сторожа смотрела
    /// только на вложенные типы: top-level (`TurnResult`, `ChatVisibilityResult`) имеют DeclaringType == null
    /// и проходили молча — ребро ядро → фасад оставалось живым при зелёных 68/68. Сам фасад был исключён
    /// как «ловится правилом 1» — но правило 1 смотрит только параметры конструктора: статический вызов
    /// <c>SessionManager.X(…)</c> или параметр метода типа фасада проходил незамеченным (R-ФИНАЛ c1,
    /// хвост 3abaaab0). Проверка — точное равенство типа: сканер даёт declaring-тип вызванного метода
    /// (IL-опкод call) и тип параметра из сигнатуры, обе формы в него попадают. Точка сборки вне
    /// <see cref="CoreTypes"/> по пространству имён (Composition) и резолвит фасад ленивыми швами —
    /// это допустимо, её охраняет правило 4.</summary>
    internal static IEnumerable<string> CoreNoFacadeRefs(IEnumerable<Type> coreTypes,
        IReadOnlySet<Type> facadeTopLevel) =>
        from t in coreTypes
        let hit = BoundaryIlScanner.CollectAllReferencedTypes(t)
            .FirstOrDefault(r => r == typeof(SessionManager)
                || r.DeclaringType == typeof(SessionManager) || facadeTopLevel.Contains(r))
        where hit is not null
        select $"{t.FullName} ссылается на {hit.FullName}: ребро «ядро → фасад» закрыто — контракты "
            + "ядра живут в Services.Sessions, а сам фасад не является типом роли";

    /// <summary>Top-level типы, объявленные в файле фасада SessionManager.cs: по рефлексии они ничем не
    /// отличаются от прочих типов Services, поэтому набор берётся разбором объявлений в колонке 0
    /// самого файла и разрешается в сборке Main. Сам <see cref="SessionManager"/> в набор не входит:
    /// он проверяется точным равенством типа в <see cref="CoreNoFacadeRefs"/> (R-ФИНАЛ c1),
    /// вложенные — по DeclaringType.</summary>
    internal static HashSet<Type> FacadeTopLevelTypes(string facadeSource)
    {
        var names = FacadeTopLevelNames(facadeSource);
        return Main.GetTypes()
            .Where(t => t.DeclaringType is null && t.Namespace == FacadeNamespace && names.Contains(t.Name)
                && t != typeof(SessionManager))
            .ToHashSet();
    }

    /// <summary>Директивы условной компиляции в файле фасада. Текстовый разбор их не понимает: объявление
    /// под `#if` выпало бы из набора молча, поэтому боевая проверка требует, чтобы их не было вовсе.</summary>
    internal static IReadOnlyList<string> FacadeConditionalDirectives(string facadeSource) =>
        Regex.Matches(facadeSource, @"^[ \t]*#[ \t]*(?:if|ifdef|elif|else|endif)\b.*$", RegexOptions.Multiline)
            .Select(m => m.Value.Trim())
            .ToList();

    internal static HashSet<string> FacadeTopLevelNames(string facadeSource) =>
        // Raw-строки и комментарии вырезаются до разбора: «объявление» в колонке 0 внутри них — не объявление.
        // Атрибут допускает строковые и char-литералы с «]» и вложенные скобки в аргументе: [Obsolete("see Foo[0]")],
        // [Foo(']')], [Foo(new[]{1})] — вложенность держит балансирующая группа d, а «]» внутри литерала
        // не закрывает атрибут: литерал разбирается своей альтернативой целиком. Char-альтернатива держит
        // любое число юнитов — одиночный символ и многосимвольный escape-литерал ('\x41'), — та же форма,
        // что char-литерал в StripRawStringsAndComments.
        // Модификатор доступа необязателен: top-level тип без модификатора (static class Foo — в C# internal
        // по умолчанию) — тип файла, и его словари в сверке полноты; строгое требование public|internal
        // молча снимало бы такой тип из списка имён. Префикс «namespace X;» в той же строке тоже в узоре:
        // объявление после него — top-level.
        Regex.Matches(StripRawStringsAndComments(facadeSource),
                @"^(?:\[(?:[^\[\]\r\n""']|""(?:[^""\\\r\n]|\\.)*""|'(?:\\.|[^'\\\r\n])+'|(?<d>\[)|(?<-d>\]))*(?(d)(?!))\]\s*)*(?:namespace\s+[\w.]+;\s*)?(?:(?:public|internal)\s+)?(?:(?:sealed|static|abstract|partial|readonly)\s+)*(?:class|record(?:\s+(?:class|struct))?|struct|enum|interface)\s+(\w+)",
                RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

    /// <summary>Вырезает raw-строки и комментарии за ОДИН проход слева направо: каждая конструкция
    /// разбирается с её собственного начала, поэтому посторонняя <c>"""</c> внутри комментария или обычной
    /// строки не сопрягается с настоящей raw-строкой и не вырезает объявления между ними. Обычные, verbatim-
    /// и char-литералы проходят как есть (в них «//» и «"""» — просто текст).</summary>
    private static string StripRawStringsAndComments(string src) =>
        Regex.Replace(src,
            "\"\"\"[\\s\\S]*?\"\"\"|//[^\\n]*|/\\*[\\s\\S]*?\\*/|@\"(?:[^\"]|\"\")*\"|\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"|'(?:\\\\.|[^'\\\\\\r\\n])+'",
            m => m.Value.StartsWith("\"\"\"", StringComparison.Ordinal) || m.Value.StartsWith("//", StringComparison.Ordinal)
                || m.Value.StartsWith("/*", StringComparison.Ordinal) ? "" : m.Value);

    /// <summary>Ключи индексаторных инициализаторов <c>Dictionary&lt;string, string&gt; Имя = new() { ["ключ"] = … }</c>
    /// по тексту исходника: имя словаря → ПОЛНЫЕ ключи в порядке объявления, с повторами. Индексатор при
    /// повторе ключа молча перезаписывает значение, поэтому рантайм-набор <c>Keys</c> дубля не видит.
    /// Ключ — литерал, возможно с префиксом-константой <c>Имя +</c>: она подставляется из <c>const string</c>
    /// того же исходника, поэтому <c>[Ns + "X"]</c> и полное имя в кавычках дают один и тот же ключ.
    /// Допускаются <c>new()</c> и <c>new Dictionary&lt;string, string&gt;(…)</c>, тело — на одной строке или на нескольких.</summary>
    internal static Dictionary<string, List<string>> InitializerKeys(string source)
    {
        // Одноимённые объявления складываются, а не затирают друг друга: фикстура с именем реального
        // словаря видна как расхождение полноты (DuplicateDeclaredNames), а не как тихая перезапись.
        var result = new Dictionary<string, List<string>>();
        foreach (var (name, keys) in InitializerDeclarations(source))
        {
            if (result.TryGetValue(name, out var prior)) prior.AddRange(keys);
            else result[name] = keys;
        }
        return result;
    }

    /// <summary>Имена словарей, объявленных в тексте больше одного раза (в том числе фикстурой в файле):
    /// разбор по имени для них неоднозначен.</summary>
    internal static List<string> DuplicateDeclaredNames(string source) =>
        InitializerDeclarations(source).GroupBy(d => d.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

    /// <summary>Объявления разбираются построчно: строка начинается с модификаторов и самого объявления,
    /// поэтому текст внутри обычных строковых литералов (фикстуры) за объявление не принимается, а raw-строки
    /// вырезаются целиком. Повтор имени <c>const string</c> не роняет разбор: ошибкой он становится
    /// только если этим именем реально подставляют префикс и значения различаются.</summary>
    private static List<(string Name, List<string> Keys)> InitializerDeclarations(string rawSource)
    {
        var source = Regex.Replace(rawSource, "\"\"\"[\\s\\S]*?\"\"\"", "");
        var consts = new Dictionary<string, HashSet<string>>();
        foreach (Match c in Regex.Matches(source,
                     @"^[ \t]*(?:\w+[ \t]+)*const[ \t]+string[ \t]+(\w+)[ \t]*=[ \t]*""([^""]*)""[ \t]*;",
                     RegexOptions.Multiline))
        {
            if (!consts.TryGetValue(c.Groups[1].Value, out var values)) consts[c.Groups[1].Value] = values = [];
            values.Add(c.Groups[2].Value);
        }
        var result = new List<(string, List<string>)>();
        foreach (Match d in Regex.Matches(source,
                     @"^[ \t]*(?:\w+[ \t]+)*Dictionary<string,\s*string>\s+(\w+)\s*=\s*new\s*(?:Dictionary<string,\s*string>)?\s*\([^)]*\)\s*\{",
                     RegexOptions.Multiline))
        {
            var body = BraceBody(source, d.Index + d.Length);
            var keys = Regex.Matches(body, @"(?:^|[{,])\s*\[\s*(?:(\w+)\s*\+\s*)?""([^""]+)""\s*\]\s*=")
                .Select(m => m.Groups[1].Success ? ResolvePrefix(consts, m.Groups[1].Value) + m.Groups[2].Value : m.Groups[2].Value)
                .ToList();
            result.Add((d.Groups[1].Value, keys));
        }
        return result;
    }

    private static string ResolvePrefix(Dictionary<string, HashSet<string>> consts, string name)
    {
        if (!consts.TryGetValue(name, out var values))
            throw new InvalidOperationException($"Префикс ключа «{name}» не const string в исходнике");
        if (values.Count > 1)
            throw new InvalidOperationException(
                $"Префикс ключа «{name}» объявлен const string несколько раз с разными значениями ({string.Join(" | ", values)}): подстановка неоднозначна");
        return values.Single();
    }

    /// <summary>Тело инициализатора от позиции после «{» до парной «}» без комментариев (<c>//</c> и
    /// <c>/* … */</c>); скобки внутри обычных строк с экранированием, verbatim-строк <c>@"…""…"</c> и
    /// char-литералов (<c>'}'</c>) не считаются. Незакрытая форма — громкое исключение, не тихий пропуск.</summary>
    private static string BraceBody(string source, int start)
    {
        var sb = new System.Text.StringBuilder();
        var depth = 1;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '@' && next == '"')
            {
                // Verbatim-строка: «""» — содержание, закрывает только непарная кавычка, в том числе
                // сразу после «\»: обычная строковая ветка съела бы «\"» как escape и проглотила
                // конец строки вместе с последующей «}» инициализатора.
                sb.Append("@\"");
                for (i += 2; i < source.Length; i++)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { sb.Append("\"\""); i++; continue; }
                        break;
                    }
                    sb.Append(source[i]);
                }
                if (i < source.Length) sb.Append('"');
            }
            else if (c == '"')
            {
                sb.Append(c);
                for (i++; i < source.Length && source[i] != '"'; i++)
                {
                    sb.Append(source[i]);
                    if (source[i] == '\\' && i + 1 < source.Length) sb.Append(source[++i]);
                }
                if (i < source.Length) sb.Append('"');
            }
            else if (c == '\'')
            {
                for (i++; i < source.Length && source[i] != '\''; i++)
                    if (source[i] == '\\') i++;
                sb.Append("''");
            }
            else if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 1;
                sb.Append(' ');
            }
            else
            {
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return sb.ToString();
                sb.Append(c);
            }
        }
        throw new InvalidOperationException("Не найдена парная «}» инициализатора словаря");
    }

    internal static List<string> DuplicateInitializerKeys(string source) =>
        InitializerKeys(source)
            .SelectMany(kv => kv.Value.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => $"{kv.Key}: {g.Key}"))
            .ToList();

    private static string ReadGuardsSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var file = Path.Combine(dir.FullName, "backend", "ClaudeHomeServer.Guards.Tests", "Services", fileName);
            if (File.Exists(file)) return File.ReadAllText(file);
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Не найден " + fileName + ": подъём от " + AppContext.BaseDirectory);
    }

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
    public void Словари_allow_list_без_дублей_ключей()
    {
        var dups = DuplicateInitializerKeys(ReadGuardsSource("SessionCoreShapeGuardTests.cs"))
            .Concat(DuplicateInitializerKeys(ReadGuardsSource("SessionCoreFacadeContracts.cs"))).ToList();
        dups.Should().BeEmpty("индексатор при повторе ключа молча затирает причину прежней строки: " + string.Join("; ", dups));
    }

    [Fact]
    public void Самопроверка_поиск_дублей_ключей_не_вакуумный_и_ловит_повтор()
    {
        // Полнота разбора: у КАЖДОГО статического Dictionary<string, string> обоих типов есть разобранный
        // словарь, а число разобранных ключей равно рантаймовому Count. Словарь в незнакомой форме
        // (не разобран) или реальный дубль (ключей в тексте больше, чем в словаре) краснят самопроверку.
        AssertParsedMatchesRuntime(typeof(SessionCoreShapeGuardTests), "SessionCoreShapeGuardTests.cs");
        AssertParsedMatchesRuntime(typeof(SessionCoreFacadeContracts), "SessionCoreFacadeContracts.cs");

        var own = InitializerKeys(ReadGuardsSource("SessionCoreShapeGuardTests.cs"));
        own["CoreDataReferrerAllowList"].Should().Contain("ClaudeHomeServer.Services.ChatHistoryService");
        InitializerKeys(ReadGuardsSource("SessionCoreFacadeContracts.cs"))["All"]
            .Should().Contain("ClaudeHomeServer.Services.Sessions.QueuedMessage");

        // Синтетический повтор, в том числе с префиксом Ns + и полным именем в кавычках (один рантайм-ключ).
        const string head = "const string Ns = \"P.\";\nstatic readonly Dictionary<string, string> Demo = new()\n    {\n";
        const string synthetic = head
            + "        [\"A.B\"] = \"1\",\n        [Ns + \"C\"] = \"2\",\n        [\"A.B\"] = \"3\",\n        [\"P.C\"] = \"4\",\n    };\n";
        DuplicateInitializerKeys(synthetic).Should().BeEquivalentTo(["Demo: A.B", "Demo: P.C"]);
        DuplicateInitializerKeys(synthetic.Replace("[\"A.B\"] = \"3\",", "[\"X\"] = \"3\",").Replace("[\"P.C\"] = \"4\",", "[Ns + \"Y\"] = \"4\","))
            .Should().BeEmpty();

        // Однострочный словарь не сливается с соседним многострочным, а new Dictionary<…>(…) и StringComparer разбираются.
        const string mixed = "Dictionary<string, string> A = new() { [\"k\"] = \"1\" };\n"
            + "Dictionary<string, string> B = new(StringComparer.Ordinal)\n{\n    [\"k\"] = \"2\",\n    [\"m\"] = \"3\", // [\"z\"] = x\n};\n"
            + "Dictionary<string, string> C = new Dictionary<string, string>() { [\"q\"] = \"1\", [\"q\"] = \"2\" };\n";
        var parsed = InitializerKeys(mixed);
        parsed["A"].Should().Equal("k");
        parsed["B"].Should().Equal("k", "m");
        DuplicateInitializerKeys(mixed).Should().Equal("C: q");
    }

    /// <summary>Статические <c>Dictionary&lt;string, string&gt;</c> ВСЕХ типов файла: top-level типы по тексту
    /// (имя + пространство имён якоря) вместе со вложенными на любую глубину. Имена меряются без арности
    /// («`1»): текст называет generic-тип как «Holder», Type.Name — «Holder`1». Нестатические поля и локальные
    /// переменные в сверку полноты не входят. Generic-определения рантаймом не мерятся: статическое поле
    /// открытого generic не имеет значения (FieldInfo.GetValue бросает), его Count неизмерим — граница
    /// названа здесь и самопроверена (Самопроверка_полноты_generic_тип_исключён_рантайм_сверкой_явно).
    /// Имя текста, не разрешённое в тип пространства имён якоря, — громкий отказ с перечнем имён
    /// (Самопроверка_отказ_полноты_несфальшивированное_имя_громкий_и_называет_причину).</summary>
    internal static List<FieldInfo> FileStaticDictionaryFields(Type anchor, string source)
    {
        var names = FacadeTopLevelNames(source);
        var roots = anchor.Assembly.GetTypes()
            .Where(t => !t.IsNested && t.Namespace == anchor.Namespace && names.Contains(WithoutArity(t.Name))).ToList();
        // Причина отказа — не «нет в сборке» вообще: тип может быть в сборке, но в другом пространстве имён
        // (второй namespace-блок файла) или в другой сборке; либо имя вообще не объявление типа
        // (ложное имя из строки-фикстуры). Формулировка обязана называть фактическую причину.
        var unresolved = names.Except(roots.Select(t => WithoutArity(t.Name))).ToList();
        if (unresolved.Count > 0)
            throw new InvalidOperationException(
                "Имена top-level типов текста не разрешены в типы пространства имён якоря: " + string.Join(", ", unresolved) +
                " — такой тип не в этой сборке или вне пространства имён якоря (второй namespace-блок файла); "
                + "или имя не объявление типа: сверка полноты была бы неполной");
        return roots.SelectMany(WithNested)
            // Открытый generic вне рантайм-сверки намеренно: FieldInfo.GetValue(null) по статическому полю
            // generic-определения бросает — Count неизмерим, сверить нельзя. Граница не тихая: текст-разбор
            // словарь generic-типа видит (фикстура GenericHolder), а исключение поля названо тестом
            // Самопроверка_полноты_generic_тип_исключён_рантайм_сверкой_явно.
            .Where(t => !t.IsGenericTypeDefinition)
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => f.FieldType == typeof(Dictionary<string, string>))
            .ToList();
    }

    /// <summary>Имя типа без арности: «Holder`1» → «Holder» — так текст (regex ловит \w+ до «&lt;»)
    /// сопоставляется с generic-типом сборки. Для неgeneric-имён — без изменений.</summary>
    private static string WithoutArity(string name) => name.Split('`')[0];

    private static IEnumerable<Type> WithNested(Type t) =>
        new[] { t }.Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested));

    [Fact]
    public void Самопроверка_разбора_повтор_имени_const_и_фикстура_с_именем_словаря_не_роняют_и_не_подменяют()
    {
        // Повтор имени const с тем же значением и неиспользуемый повтор разбор не роняют.
        const string dupConst = "const string Ns = \"P.\";\nconst string Ns = \"P.\";\nconst string Unused = \"1\";\nconst string Unused = \"2\";\n"
            + "static readonly Dictionary<string, string> Demo = new()\n{\n    [Ns + \"C\"] = \"x\",\n};\n";
        InitializerKeys(dupConst)["Demo"].Should().Equal("P.C");

        // Префикс с РАЗНЫМИ значениями — осмысленный отказ про неоднозначность, а не ArgumentException.
        var ambiguous = dupConst.Replace("const string Ns = \"P.\";\nconst string Ns = \"P.\";", "const string Ns = \"P.\";\nconst string Ns = \"Q.\";");
        FluentActions.Invoking(() => InitializerKeys(ambiguous))
            .Should().Throw<InvalidOperationException>().WithMessage("*Ns*неоднозначна*");

        // Фикстура с именем реального словаря не затирает запись настоящего: имя видно как объявленное дважды.
        const string twin = "Dictionary<string, string> Real = new() { [\"a\"] = \"1\" };\nDictionary<string, string> Real = new() { [\"b\"] = \"2\" };\n";
        DuplicateDeclaredNames(twin).Should().Equal("Real");
        InitializerKeys(twin)["Real"].Should().Equal("a", "b");

        // Текст внутри обычной строки (фикстура в одну строку) объявлением не считается.
        InitializerKeys("var f = \"Dictionary<string, string> Fake = new() { [\\\"k\\\"] = 1 };\";\n").Should().BeEmpty();
    }

    [Fact]
    public void Самопроверка_разбора_блок_комментарий_char_и_verbatim_не_меняют_набор_ключей()
    {
        const string head = "Dictionary<string, string> D = new()\n{\n";
        // Блок-комментарий со скобкой и ключом внутри.
        InitializerKeys(head + "    /* [\"fake\"] = \"x\", } */\n    [\"a\"] = \"1\",\n    [\"b\"] = \"2\",\n};\nDictionary<string, string> E = new() { [\"e\"] = \"1\" };")
            .Should().BeEquivalentTo(new Dictionary<string, List<string>> { ["D"] = ["a", "b"], ["E"] = ["e"] });
        // Char-литералы со скобкой и кавычкой в значении.
        InitializerKeys(head + "    [\"a\"] = '}'.ToString(),\n    [\"b\"] = '\"'.ToString(),\n};\nDictionary<string, string> E = new() { [\"e\"] = \"1\" };")
            .Should().BeEquivalentTo(new Dictionary<string, List<string>> { ["D"] = ["a", "b"], ["E"] = ["e"] });
        // Verbatim: первая строка держит «}» и «//» внутри себя (после экранированной кавычки «""»),
        // вторая заканчивается обратной косой сразу перед закрывающей кавычкой. Первая: без verbatim-ветки
        // удвоенные кавычки разбились бы на соседние обычные строки, и «//» оказался внутри «строки» лишь
        // случайно. Вторая — решающая: без verbatim-ветки кавычка после обратной косой съедается как escape,
        // закрывающая кавычка теряется, и следующие ключи вместе с закрывающей «}» проглатываются как текст
        // строки — verbatim-ветка обязана доесть строку до настоящей (непарной) границы.
        InitializerKeys(head + "    [\"a\"] = @\"x\"\"}\"\" // y\",\n    [\"b\"] = @\"C:\\\",\n    [\"c\"] = \"3\",\n};\nDictionary<string, string> E = new() { [\"e\"] = \"1\" };")
            .Should().BeEquivalentTo(new Dictionary<string, List<string>> { ["D"] = ["a", "b", "c"], ["E"] = ["e"] });
        // Незакрытый блок-комментарий — громкий отказ.
        FluentActions.Invoking(() => InitializerKeys(head + "    /* [\"a\"] = \"1\",\n};"))
            .Should().Throw<InvalidOperationException>().WithMessage("*парная*");
    }

    [Fact]
    public void Самопроверка_полнота_видит_статические_словари_вложенных_и_других_top_level_типов_файла()
    {
        var names = FileStaticDictionaryFields(typeof(SessionCoreShapeGuardTests), ReadGuardsSource("SessionCoreShapeGuardTests.cs"))
            .Select(f => f.Name).ToList();
        names.Should().Contain("CoreDataReferrerAllowList", "словарь самого класса");
        names.Should().Contain(nameof(FakeNestedDicts.Inner.NestedDict), "словарь вложенного типа");
        names.Should().Contain(nameof(ForeignDictHolder.ForeignDict), "словарь другого top-level класса файла");
        names.Should().NotContain(nameof(FakeNestedDicts.InstanceDict), "нестатический словарь в сверку не входит");
    }

    [Fact]
    public void Самопроверка_полноты_объявление_без_модификатора_или_после_namespace_состоит_в_сверке()
    {
        // Top-level тип без модификатора доступа (в C# internal по умолчанию) — тип файла: без опционального
        // модификатора в узоре FacadeTopLevelNames его имя не попадало в names, и его словари выпадали
        // из полноты молча. Фикстура BareNamedDicts обязана быть в перечне полей сверки.
        var names = FileStaticDictionaryFields(typeof(SessionCoreShapeGuardTests), ReadGuardsSource("SessionCoreShapeGuardTests.cs"))
            .Select(f => f.Name).ToList();
        names.Should().Contain(nameof(BareNamedDicts.BareDict),
            "тип без модификатора доступа — top-level тип файла: его словарь обязан в сверке полноты");

        // Тот же случай на стороне текста: объявление после «namespace X;» в той же строке — без префиксной
        // группы узора его имя не попадало в names, и весь тип выпадал из полноты молча.
        FacadeTopLevelNames("namespace ClaudeHomeServer.Tests.Services; static class Prefixed {}")
            .Should().Contain("Prefixed", "объявление после «namespace X;» в той же строке — top-level тип файла");
    }

    [Fact]
    public void Самопроверка_полноты_generic_тип_исключён_рантайм_сверкой_явно()
    {
        var source = ReadGuardsSource("SessionCoreShapeGuardTests.cs");
        // Текст-разбор словарь внутри generic-типа НЕ слеп: фикстура GenericHolder разбирается.
        InitializerKeys(source).Should().ContainKey("GenericDict",
            "generic-тип файла текстовым разбором видим: иначе его словарь исчезал бы и из текста");
        // Рантайм-сверка для открытого generic невозможна: FieldInfo.GetValue по его статическому полю
        // бросает, Count неизмерим — поле исключено, и это названная граница, а не тихое выпадение:
        // без фильтра !IsGenericTypeDefinition в FileStaticDictionaryFields эта проверка краснеет,
        // а боевая полнота падает с тем самым исключением GetValue.
        var fields = FileStaticDictionaryFields(typeof(SessionCoreShapeGuardTests), source)
            .Select(f => f.Name).ToList();
        fields.Should().NotContain("GenericDict",
            "открытый generic вне рантайм-сверки намеренно (Count неизмерим) — граница описана в FileStaticDictionaryFields");
    }

    [Fact]
    public void Самопроверка_отказ_полноты_несфальшивированное_имя_громкий_и_называет_причину()
    {
        // «Громкий отказ» FileStaticDictionaryFields самопроверен здесь: обе фикстуры обязаны его вызвать,
        // и отказ обязан назвать имя. Случай 1: имя вообще не тип в сборке. Случай 2: имя поймано узором
        // через префиксную группу (объявление после «namespace X;»), но тип не в пространстве имён якоря
        // (в реальной жизни — второй namespace-блок того же файла).
        const string absent = "NamedInAnotherNamespace";
        FluentActions.Invoking(() => FileStaticDictionaryFields(typeof(SessionCoreShapeGuardTests),
                "internal static class " + absent + " {}\n"))
            .Should().Throw<InvalidOperationException>().WithMessage("*" + absent + "*");
        FluentActions.Invoking(() => FileStaticDictionaryFields(typeof(SessionCoreShapeGuardTests),
                "namespace ClaudeHomeServer.Tests.Other;\ninternal static class " + absent + " {}\n"))
            .Should().Throw<InvalidOperationException>().WithMessage("*" + absent + "*");
    }

    internal sealed class FakeNestedDicts
    {
        internal sealed class Inner
        {
            internal static readonly Dictionary<string, string> NestedDict = new() { ["n"] = "1" };
        }

        internal readonly Dictionary<string, string> InstanceDict = new() { ["i"] = "1" };
    }

    private static void AssertParsedMatchesRuntime(Type anchor, string sourceFile)
    {
        var source = ReadGuardsSource(sourceFile);
        var parsed = InitializerKeys(source);
        var fields = FileStaticDictionaryFields(anchor, source);
        fields.Should().NotBeEmpty(sourceFile + ": словарей не найдено — проверка вакуумна");
        var twice = DuplicateDeclaredNames(source);
        foreach (var f in fields)
        {
            twice.Should().NotContain(f.Name,
                sourceFile + ": имя «" + f.Name + "» объявлено в тексте больше одного раза (фикстура с именем реального словаря?) — разбор по имени неоднозначен");
            parsed.Should().ContainKey(f.Name, f.DeclaringType!.Name + "." + f.Name + ": словарь не разобран текстовым разбором");
            parsed[f.Name].Count.Should().Be(((Dictionary<string, string>)f.GetValue(null)!).Count,
                f.DeclaringType.Name + "." + f.Name + ": число ключей в тексте расходится с рантаймовым Count");
        }
    }

    [Fact]
    public void Разбор_файла_фасада_не_вакуумный()
    {
        // Файл фасада держит ровно SessionManager; набор top-level пуст ТОЛЬКО потому, что все
        // контракты ушли в ядро. Имя фасада в разборе обязано находиться — иначе регэксп сломан
        // и сторож проходит вакуумно.
        // Шаг «имя → тип» тоже должен находить фасад: опечатка в FacadeNamespace иначе делает BeEmpty ниже вакуумным.
        typeof(SessionManager).Namespace.Should().Be(FacadeNamespace,
            "FacadeNamespace — пространство имён фасада; расхождение делает проверку набора top-level типов вакуумной");
        var source = ReadFacadeSource();
        FacadeTopLevelNames(source).Should().Contain(nameof(SessionManager));
        FacadeTopLevelTypes(source).Should().BeEmpty(
            "файл фасада держит ровно SessionManager: новый top-level тип рядом с ним должен пройти "
            + "через трипвайр «ядро → фасад», а не молча появиться");
    }

    [Fact]
    public void Файл_фасада_без_директив_условной_компиляции() =>
        FacadeConditionalDirectives(ReadFacadeSource()).Should().BeEmpty(
            "текстовый разбор объявлений не понимает #if: объявление под ним выпало бы из набора top-level "
            + "типов молча — сначала научи разбор, потом вводи директиву");

    [Fact]
    public void Самопроверка_разбор_фасада_не_берёт_тип_ядра_и_видит_атрибут_и_директивы()
    {
        // Имя типа ядра в тексте файла фасада не делает его top-level типом фасада: фильтр namespace держит.
        FacadeTopLevelTypes("public record TurnResult(string Reply);").Should().BeEmpty(
            "TurnResult живёт в Services.Sessions, а не в Services");
        FacadeTopLevelNames("[Obsolete] public class WithAttr {}\n[A][B(1)] internal sealed record Two(int X);")
            .Should().BeEquivalentTo("WithAttr", "Two");
        FacadeTopLevelNames("[Obsolete(\"see Foo[0]\")] public class BracketAttr {}").Should().BeEquivalentTo("BracketAttr");
        FacadeTopLevelNames("var s = \"\"\"\npublic class InRaw {}\n\"\"\";\npublic class Real {}").Should().BeEquivalentTo("Real");
        // Посторонняя «"""» в комментарии не сопрягается с настоящей raw-строкой: объявление между ними сохраняется.
        FacadeTopLevelNames("// лишняя \"\"\" в комментарии\npublic class Between {}\nvar s = \"\"\"\npublic class InRaw {}\n\"\"\";\npublic class Real {}")
            .Should().BeEquivalentTo("Between", "Real");
        // «]» в код-аргументе атрибута (массив, вложенный массив) не выбрасывает объявление.
        FacadeTopLevelNames("[Foo(new[]{1})] public class ArrAttr {}\n[Foo(new[]{new[]{1}}), Bar] internal class NestedArr {}")
            .Should().BeEquivalentTo("ArrAttr", "NestedArr");
        // Тот же класс тихого пропуска в char-форме: «]» внутри char-литерала не «закрывает» атрибут.
        FacadeTopLevelNames("[Foo(']')] public class CharAttr {}\n[Bar('\\'')] internal class QuoteChar {}")
            .Should().BeEquivalentTo("CharAttr", "QuoteChar");
        // Многояюнтовый char-литерал — одиночный символ и escape-литерал: атрибут не выбрасывается,
        // альтернатива держит литерал любой длины.
        FacadeTopLevelNames("[Foo('A')] public class PlainChar {}\n[Bar('\\x41')] internal class HexChar {}")
            .Should().BeEquivalentTo("PlainChar", "HexChar");
        FacadeConditionalDirectives("#if false\npublic class Hidden {}\n  #endif\n#region R").Should()
            .BeEquivalentTo("#if false", "#endif");
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
        ServiceProviderAllowList.Keys.Should().BeEquivalentTo(["ClaudeHomeServer.Services.Sessions.BuiltinMcpContexts"],
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
    public void Вложенные_типы_корней_контрактов_фасада_только_record_enum_исключения()
    {
        var roots = SessionCoreFacadeContracts.All.Keys
            .Select(n => Main.GetType(n) ?? throw new InvalidOperationException($"{n}: типа нет в сборке (устаревшая строка контрактов фасада)"))
            .ToList();
        roots.SelectMany(r => r.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            .Should().NotBeEmpty("SendAndWaitResult несёт вложенные Busy/Queued/…: иначе проверка вакуумная");
        SessionCoreFacadeContracts.NestedShapeViolations(roots).Should().BeEmpty();
    }

    [Fact]
    public void Самопроверка_вложенный_класс_с_поведением_краснеет_в_контракте() =>
        SessionCoreFacadeContracts.NestedShapeViolations([typeof(FakeRootWithNestedClass)]).Should().ContainSingle()
            .Which.Should().Contain("FakeRootWithNestedClass+Role");

    [Fact]
    public void Самопроверка_вложенный_record_struct_распознаётся_как_record_а_обычная_struct_нет()
    {
        SessionCoreFacadeContracts.IsRecord(typeof(FakeRootWithStructs.RecStruct)).Should().BeTrue(
            "у record struct нет <Clone>$, признак — PrintMembers плюс синтезированное равенство");
        SessionCoreFacadeContracts.IsRecord(typeof(FakeRootWithStructs.PlainStruct)).Should().BeFalse();
        SessionCoreFacadeContracts.NestedShapeViolations([typeof(FakeRootWithStructs)]).Should().ContainSingle()
            .Which.Should().Contain("FakeRootWithStructs+PlainStruct");
    }

    [Fact]
    public void Самопроверка_рукописный_PrintMembers_в_обычной_struct_не_делает_её_записью()
    {
        SessionCoreFacadeContracts.IsRecord(typeof(FakeRootWithHandwrittenPrint.HandStruct)).Should().BeFalse(
            "признак записи — синтезированный компилятором PrintMembers, а не метод с тем же именем");
        SessionCoreFacadeContracts.NestedShapeViolations([typeof(FakeRootWithHandwrittenPrint)]).Should().ContainSingle()
            .Which.Should().Contain("FakeRootWithHandwrittenPrint+HandStruct");
    }

    [Fact]
    public void Самопроверка_record_struct_с_объявленным_автором_PrintMembers_остаётся_записью()
    {
        // Х9: своя декларация PrintMembers заменяет синтезированный член (спека C# 10.0 «Record structs»),
        // поэтому признак структуры-записи держится на синтезированной машинерии равенства.
        SessionCoreFacadeContracts.IsRecord(typeof(FakeRootWithOwnPrint.RecordWithOwnPrint)).Should().BeTrue(
            "record struct с собственным PrintMembers остаётся записью: op_Equality/Equals/GetHashCode синтезированы");
        SessionCoreFacadeContracts.IsRecord(typeof(FakeRootWithOwnPrint.PlainWithOwnPrint)).Should().BeFalse(
            "рукописный PrintMembers без синтезированного равенства — обычная структура; её синтезированные "
            + "акцессоры авто-свойств get_/set_ признаком не считаются");
        SessionCoreFacadeContracts.NestedShapeViolations([typeof(FakeRootWithOwnPrint)]).Should().ContainSingle()
            .Which.Should().Contain("FakeRootWithOwnPrint+PlainWithOwnPrint");
    }

    [Fact]
    public void Самопроверка_диагностика_неопознанной_записи_называет_найденные_члены()
    {
        // Смена формы синтезированного равенства (новый компилятор) обязана красить сторож
        // причиной, а не молча: описание нарушения называет, какие [CompilerGenerated]-члены в типе
        // есть на самом деле. Фикстура — обычная структура с рукописным PrintMembers и
        // авто-свойством: его акцессоры синтезированы, а машинерии равенства нет — по списку это видно.
        var plain = typeof(FakeRootWithOwnPrint.PlainWithOwnPrint);
        SessionCoreFacadeContracts.RecordShapeNote(plain).Should().Contain("get_Y")
            .And.Contain("set_Y", "акцессоры авто-свойства должны быть в диагностике: их нет среди членов равенства");
        SessionCoreFacadeContracts.NestedShapeViolations([typeof(FakeRootWithOwnPrint)]).Should().ContainSingle()
            .Which.Should().Contain("признак записи не найден");
        // У опознанной записи диагностики нет: она не шумит в passing-пути.
        SessionCoreFacadeContracts.RecordShapeNote(typeof(FakeRootWithOwnPrint.RecordWithOwnPrint)).Should().BeEmpty();
    }

    private sealed class FakeRootWithOwnPrint
    {
        public readonly record struct RecordWithOwnPrint(int X)
        {
            private bool PrintMembers(System.Text.StringBuilder sb) => sb.Append(X).Length > 0;
        }

        public struct PlainWithOwnPrint
        {
            public int X;
            public int Y { get; set; }
            private bool PrintMembers(System.Text.StringBuilder sb) => false;
        }
    }

    private sealed class FakeRootWithStructs
    {
        public readonly record struct RecStruct(int X);
        public struct PlainStruct { public int X; }
    }

    private sealed class FakeRootWithHandwrittenPrint
    {
        public struct HandStruct
        {
            public int X;
            private bool PrintMembers(System.Text.StringBuilder sb) => false;
        }
    }

    [Fact]
    public void Самопроверка_роль_в_перечне_контрактов_фасада_краснеет_по_форме() =>
        SessionCoreFacadeContracts.CriterionViolations(
                ["ClaudeHomeServer.Services.Sessions.SessionStore"], Main, typeof(SessionManager), CoreNamespace)
            .Should().ContainSingle().Which.Should().Contain("не record/enum/исключение");

    private sealed class FakeRootWithNestedClass
    {
        public sealed class Role { public void Act() { } }
        public enum Ok { A }
        public sealed record Data(int X);
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

    // Формы ссылки на сам фасад (R-ФИНАЛ c1): параметр метода (правило 1 до него не дотягивается)
    // и статический вызов (сканер даёт declaring-тип вызванного метода по IL-опкоду call;
    // сам вызов в тесте ни разу не исполняется — достаточно тела).
    private sealed class FakeMethodTakesManager { public void Go(SessionManager manager) { } }

    private static class FakeCallsFacadeStatic
    {
        public static string ChatPreview(string text) => SessionManager.ChatPreview(text);
    }

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
        // SessionStore не может быть изменён в тесте, поэтому нарушитель — синтетическая роль вне allow-list с IServiceProvider.
        Rule5ServiceProviderOnlyAllowListed([typeof(FakeStoreWithProvider)]).Should().ContainSingle();
        Rule5ServiceProviderOnlyAllowListed([typeof(FakeProvider)]).Should().ContainSingle();
    }

    [Fact]
    public void Самопроверка_трипвайр_ядра_краснеет_на_ссылке_на_вложенный_тип_фасада()
    {
        CoreNoFacadeRefs([typeof(FakeUsesFacadeNested)], new HashSet<Type>()).Should().ContainSingle();
    }

    [Fact]
    public void Самопроверка_трипвайр_ядра_краснеет_на_самом_фасаде_во_всех_формах()
    {
        // R-ФИНАЛ c1 (хвост 3abaaab0): раньше фасад исключался как «ловится правилом 1», и прежняя
        // самопроверка утверждала BeEmpty на нём — это и есть подтверждённый слепотой случай.
        // Каждая форма ссылки обязана краснеть: поле и параметр конструктора — FakeTakesManager,
        // параметр метода — FakeMethodTakesManager (правило 1 его не видит вовсе),
        // статический вызов — FakeCallsFacadeStatic.
        CoreNoFacadeRefs([typeof(FakeTakesManager)], new HashSet<Type>()).Should().ContainSingle(
            "поле и параметр конструктора типа фасада");
        CoreNoFacadeRefs([typeof(FakeMethodTakesManager)], new HashSet<Type>()).Should().ContainSingle(
            "параметр метода типа фасада — не конструктор, правило 1 до него не дотягивается");
        CoreNoFacadeRefs([typeof(FakeCallsFacadeStatic)], new HashSet<Type>()).Should().ContainSingle(
            "статический вызов SessionManager.ChatPreview: declaring-тип вызванного метода в IL");
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
    public void Самопроверка_правило3_ловится_одноимённый_Lazy_из_чужого_пространства_имён()
    {
        // Разрыв цикла опознаётся по полному имени: свой ITurnStarter в чужом namespace — не шов ядра.
        Rule3LazyOnlyNamed([typeof(ForeignNames.FakeLazyForeignSeam)]).Should().ContainSingle();
        Rule3LazyOnlyNamed([typeof(FakeLazyNamed)]).Should().BeEmpty("настоящие швы по-прежнему проходят");
    }

    [Fact]
    public void Самопроверка_правило5_ловится_одноимённая_роль_из_чужого_пространства_имён()
    {
        // Allow-list правила 5 по полному имени: BuiltinMcpContexts из чужого namespace провайдера не получает.
        Rule5ServiceProviderOnlyAllowListed([typeof(ForeignNames.BuiltinMcpContexts)]).Should().ContainSingle();
        Rule5ServiceProviderOnlyAllowListed([typeof(BuiltinMcpContexts)]).Should().BeEmpty(
            "настоящая роль из allow-list проходит");
        // Живость строки allow-list: BeEmpty выше зелёна и когда роль перестала брать провайдер
        // (нарушений просто нет) — поэтому отдельно: реальная роль обязана иметь параметр IServiceProvider.
        // Сверка — тем же Mentions, что и правило 5 (:183): обёрнутый провайдер (Func<IServiceProvider>)
        // тоже считается живым — самопроверка обязана мерить то же, что мерит правило, а не более строгую форму.
        Ctors(typeof(BuiltinMcpContexts)).SelectMany(c => c.GetParameters())
            .Should().Contain(p => Mentions(p.ParameterType, typeof(IServiceProvider)),
                "строка allow-list живая: BuiltinMcpContexts (BuiltinMcpContexts.cs:47) берёт IServiceProvider "
                + "в конструкторе (или обёрнутую форму — как правило 5) — иначе допуск правила 5 ничего не охраняет");
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

// Одноимённики допустимых типов в чужом (вложенном) пространстве имён — нарушители самопроверок правил 3 и 5.
// Фикстур полноты здесь нет: поддерживающий правила 3/5 не должен искать их под этим именем
// (словарь другого top-level класса — в ForeignDictHolder).
internal static class ForeignNames
{
    internal interface ITurnStarter { }

    internal sealed class FakeLazyForeignSeam
    {
        public FakeLazyForeignSeam(Lazy<ITurnStarter> starter) { S = starter; }
        public Lazy<ITurnStarter> S { get; }
    }

    internal sealed class BuiltinMcpContexts
    {
        public BuiltinMcpContexts(IServiceProvider services) { S = services; }
        public IServiceProvider S { get; }
    }
}

// Фикстура полноты: отдельный top-level класс файла со своим словарём — его словари обязан
// достать FileStaticDictionaryFields (Самопроверка_полнота_видит_статические_словари_...).
internal static class ForeignDictHolder
{
    internal static readonly Dictionary<string, string> ForeignDict = new() { ["f"] = "1" };
}

// Фикстура полноты: top-level тип БЕЗ модификатора доступа (C# — internal по умолчанию).
// Без опционального модификатора в узоре FacadeTopLevelNames его имя не попадало в names,
// и его словари выпадали из сверки молча (Самопроверка_полноты_объявление_без_модификатора_или_после_namespace_...).
static class BareNamedDicts
{
    internal static readonly Dictionary<string, string> BareDict = new() { ["b"] = "1" };
}

// Фикстура границы полноты: generic-тип файла. Его словарь видим текстовым разбором, но
// рантаймовый Count открытого generic неизмерим (FieldInfo.GetValue по статическому полю бросает) —
// поле исключено из сверки явной границей (Самопроверка_полноты_generic_тип_исключён_...).
internal static class GenericHolder<T>
{
    internal static readonly Dictionary<string, string> GenericDict = new() { ["g"] = "1" };
}

// Подставляемый тип самопроверки правила 4: одноимённый допустимой регистрации
// SessionCoreRegistration, но в тестовом пространстве имён (не Composition).
// При сверке по имени типа проходил бы мимо allow-list — при сверке по полному имени
// обязан быть пойман. В боевой скан правила 4 не попадает: тот обходит только Main.
internal sealed class SessionCoreRegistration
{
    public SessionStore? Store;
}
