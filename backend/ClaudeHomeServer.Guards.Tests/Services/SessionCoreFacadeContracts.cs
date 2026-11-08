using System.Reflection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Перечень «контракт фасада» — единственный источник правды (решения лида 0b942ca6 и 98d2368c):
/// типы <c>ClaudeHomeServer.Services.Sessions</c>, которые есть в сигнатуре public/internal члена
/// <c>SessionManager</c> (включая generic-аргументы <c>Task&lt;…&gt;</c> и публичную поверхность таких
/// записей-результатов — так достижимы ровно <c>TurnResult</c> и <c>ChatVisibilityOutcome</c>, см.
/// <see cref="ReachableTypes"/>) либо наследуют
/// <see cref="Exception"/>. Ссылаться на такой тип может ЛЮБОЙ тип Main: допуск выдаётся на тип,
/// а не на потребителя. Читают два сторожа — правило 4 (<c>SessionCoreShapeGuardTests</c>) и
/// root-сторож (<c>RootSubsystemBoundaryTests</c>); второго списка не заводим.
/// Состав держит самопроверка плюс механическая проверка критерия по каждой строке
/// (<see cref="CriterionViolations"/>). Роли ядра и данные ядра (формат хранения вне сигнатур
/// фасада — <c>CoreDataContracts</c> правила 4) сюда не входят.
/// </summary>
internal static class SessionCoreFacadeContracts
{
    private const string Ns = "ClaudeHomeServer.Services.Sessions.";

    internal static readonly Dictionary<string, string> All = new()
    {
        [Ns + "QueuedMessage"] =
            "элемент очереди в сигнатурах фасада (SessionManager.cs:1233, :1316): содержимое очереди, поведения не держит",
        [Ns + "PendingKind"] =
            "enum режима записи очереди: аргумент SendOrEnqueueAsync и значение в ответе GetPending",
        [Ns + "DeliveryCause"] =
            "enum причины доставки хода (бывший SessionManager.DeliveryCause): аргумент cause при вызове фасада",
        [Ns + "SendUserOutcome"] =
            "enum исхода постановки пользовательского сообщения: результат SendMessageAsync",
        [Ns + "SendAndWaitResult"] =
            "иерархия результата отправки с ожиданием; вложенные Busy/Queued/QueueFull/Completed/Running относятся к корню",
        [Ns + "ReportUpResult"] = "enum исхода отчёта «наверх»: результат ReportUpAsync",
        [Ns + "GlobalPersonaInSharedException"] =
            "исключение отказа назначения глобальной персоны в общий проект: ловят по типу контроллеры и тулсет",
        [Ns + "TurnTranscriptDamagedException"] =
            "исключение повреждённого транскрипта: контроллер ловит по типу и отвечает code=transcript_damaged",
        [Ns + "RestartResumeResult"] = "запись исхода авто-возобновления хода: результат ResumeAfterRestartAsync",
        [Ns + "StuckTurnRestartResult"] = "запись исхода перезапуска зависшего хода: результат RestartStuckTurnAsync",
        [Ns + "ChatBranchResult"] = "запись итога ветвления чата: результат BranchAsync",
        [Ns + "IconBatchResult"] = "запись итога пакетного прогона значков: результат SetChatIconsAsync",
        [Ns + "TurnResult"] = "запись итога завершённого хода: результат фасада (Task<TurnResult>)",
        [Ns + "ChatVisibilityOutcome"] = "enum исхода смены видимости чата (внутри ChatVisibilityResult)",
        [Ns + "ChatVisibilityResult"] = "запись результата SetChatVisibility",
        [Ns + "WorkspaceMcpPlan"] = "internal-запись плана сервера рабочего пространства: результат фасада для WorkspaceToolset",
    };

    /// <summary>Тип — контракт фасада (вложенный тип сводится к корню).</summary>
    internal static bool Contains(Type t)
    {
        while (t.DeclaringType is not null) t = t.DeclaringType;
        return All.ContainsKey(t.FullName ?? t.Name);
    }

    private static bool IsVisibleOutside(MethodBase m) =>
        m.IsPublic || m.IsAssembly || m.IsFamily || m.IsFamilyOrAssembly;

    /// <summary>Корневые типы из сигнатур public/internal/protected членов <paramref name="facade"/>:
    /// возврат, параметры, поля, свойства, события; generic-аргументы, массивы и ref раскрываются,
    /// вложенные сводятся к корню.</summary>
    internal static HashSet<Type> SignatureTypes(Type facade)
    {
        var result = new HashSet<Type>();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        void Add(Type? t)
        {
            if (t is null) return;
            if (t.HasElementType) { Add(t.GetElementType()); return; }
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) Add(a);
            while (t.DeclaringType is not null) t = t.DeclaringType;
            result.Add(t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t);
        }

        foreach (var m in facade.GetMethods(all).Where(IsVisibleOutside))
        {
            Add(m.ReturnType);
            foreach (var p in m.GetParameters()) Add(p.ParameterType);
        }
        foreach (var c in facade.GetConstructors(all).Where(IsVisibleOutside))
            foreach (var p in c.GetParameters()) Add(p.ParameterType);
        foreach (var f in facade.GetFields(all).Where(f => f.IsPublic || f.IsAssembly || f.IsFamily || f.IsFamilyOrAssembly))
            Add(f.FieldType);
        foreach (var p in facade.GetProperties(all))
            if (p.GetMethod is { } g && IsVisibleOutside(g) || p.SetMethod is { } s && IsVisibleOutside(s))
                Add(p.PropertyType);
        foreach (var e in facade.GetEvents(all)) Add(e.EventHandlerType);
        return result;
    }

    /// <summary>Сигнатура фасада плюс payload-типы его собственных контрактов: один шаг по публичным
    /// свойствам и полям (не конструкторам) корней, которые одновременно стоят в сигнатуре фасада и в
    /// <see cref="All"/>, и их вложенных не-компиляторных типов. Так достижимы ровно два типа:
    /// <c>TurnResult</c> (<c>SendAndWaitResult.Completed.Result</c>) и <c>ChatVisibilityOutcome</c>
    /// (<c>ChatVisibilityResult.Outcome</c>). Замыкания нет: роль, шов или тип-данные ядра через
    /// публичную поверхность роли в контракт не попадают.</summary>
    internal static HashSet<Type> ReachableTypes(Type facade, string coreNamespace)
    {
        var seen = SignatureTypes(facade);
        var roots = seen.Where(t => t.Namespace == coreNamespace && Contains(t)).ToList();
        const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        void Add(Type? t)
        {
            if (t is null) return;
            if (t.HasElementType) { Add(t.GetElementType()); return; }
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) Add(a);
            while (t.DeclaringType is not null) t = t.DeclaringType;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) t = t.GetGenericTypeDefinition();
            if (t.Namespace == coreNamespace) seen.Add(t);
        }

        foreach (var root in roots)
        {
            var family = new List<Type> { root };
            family.AddRange(root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(n => !n.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)));
            foreach (var t in family)
            {
                foreach (var p in t.GetProperties(pub)) Add(p.PropertyType);
                foreach (var f in t.GetFields(pub)) Add(f.FieldType);
            }
        }
        return seen;
    }

    /// <summary>record class опознаётся по синтезированному <c>&lt;Clone&gt;$</c>. У record struct его нет,
    /// а <c>PrintMembers(StringBuilder)</c> может быть объявлен самим автором: пользовательская декларация
    /// заменяет синтезированный член (спека C# 10.0 «Record structs»), поэтому один <c>PrintMembers</c>
    /// запись от обычной структуры не отличает. Признак структуры-записи — пара: метод
    /// <c>PrintMembers(StringBuilder)</c> (синтезированный или объявленный автором) И хоть один
    /// синтезированный компилятором член машинерии равенства — статические <c>op_Equality</c>/
    /// <c>op_Inequality</c>, <c>Equals(этот_тип)</c> или <c>GetHashCode()</c> без параметров. Список имён
    /// закрытый: «любой метод с <c>CompilerGeneratedAttribute</c>» подошёл бы и обычной структуре —
    /// у неё синтезированные акцессоры авто-свойств помечены тем же атрибутом (держит фикстура
    /// <c>PlainWithOwnPrint</c> в <see cref="SessionCoreShapeGuardTests"/>). Причина провала признака
    /// — в <see cref="RecordShapeNote"/>: он называет, какие <c>CompilerGenerated</c>-члены реально
    /// найдены, поэтому смена формы синтезированного равенства (новый компилятор) красит сторож
    /// сообщением, а не молча.</summary>
    internal static bool IsRecord(Type t) =>
        t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null
        || t.IsValueType && HasPrintMembers(t) && HasSynthesizedEquality(t);

    private static bool HasPrintMembers(Type t) =>
        t.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            null, [typeof(System.Text.StringBuilder)], null) is not null;

    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool HasSynthesizedEquality(Type t) =>
        DeclaredMethods(t).Any(m => m.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
            // скобки явные: без них «or» держится синтаксисом pattern-комбинатора после «is», а не
            // приоритетом && — семантика та же, но при правке приоритета операторов ветка молча
            // потеряла бы op_Inequality
            && (m.IsStatic && (m.Name is "op_Equality" or "op_Inequality")
                || m.Name == "Equals" && m.GetParameters() is [var p] && p.ParameterType == t
                || m.Name == "GetHashCode" && m.GetParameters().Length == 0));

    private static IEnumerable<MethodInfo> DeclaredMethods(Type t) =>
        t.GetMethods(DeclaredMembers);

    /// <summary>Имена членов типа, помеченных <c>CompilerGeneratedAttribute</c> (методы, включая
    /// акцессоры авто-свойств): по ним видно, какая машинерия в типе есть на самом деле.</summary>
    private static List<string> GeneratedMemberNames(Type t) =>
        DeclaredMethods(t)
            .Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .Select(m => m.Name)
            .ToList();

    /// <summary>Диагностика провала признака записи для значения: пустой <see cref="RecordShapeNote"/>
    /// у класса или enum (их признак и не ждали), у значения — список найденных
    /// <c>CompilerGenerated</c>-членов: отсутствие op_Equality/Equals/GetHashCode в нём и есть
    /// причина, по которой запись не опознана.</summary>
    internal static string RecordShapeNote(Type t) =>
        !t.IsValueType || IsRecord(t) ? ""
        : GeneratedMemberNames(t).Count > 0
            ? " (признак записи не найден; члены с [CompilerGenerated]: " + string.Join(", ", GeneratedMemberNames(t)) + ")"
            : " (признак записи не найден; членов с [CompilerGenerated] нет)";

    /// <summary>Форма контракта фасада — данные: record/enum/исключение. Роль или шов контрактом не бывает.</summary>
    internal static bool IsContractShape(Type t) =>
        t.IsEnum || IsRecord(t) || typeof(Exception).IsAssignableFrom(t);

    /// <summary>Вложенные типы корней контрактов допустимы только как record/enum/исключение: вложенный
    /// класс с поведением вместе с корнем стал бы допуском на роль.</summary>
    internal static IEnumerable<string> NestedShapeViolations(IEnumerable<Type> roots) =>
        from root in roots
        from n in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
        where !n.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
        where !IsContractShape(n)
        select $"{n.FullName}: вложенный тип контракта фасада — не record/enum/исключение{RecordShapeNote(n)}";

    /// <summary>Механическая проверка критерия по каждой строке: тип существует в <paramref name="main"/>,
    /// лежит в <c>Services.Sessions</c> и либо достижим из сигнатуры <paramref name="facade"/>
    /// (<see cref="ReachableTypes"/>), либо наследует
    /// <see cref="Exception"/>. Возвращает описания нарушений.</summary>
    internal static IEnumerable<string> CriterionViolations(
        IEnumerable<string> contractNames, Assembly main, Type facade, string coreNamespace)
    {
        var signature = ReachableTypes(facade, coreNamespace);
        foreach (var name in contractNames)
        {
            var type = main.GetType(name);
            if (type is null) { yield return $"{name}: типа нет в сборке (устаревшая строка тихо откроет допуск)"; continue; }
            if (type.Namespace != coreNamespace) { yield return $"{name}: лежит не в {coreNamespace}"; continue; }
            if (!IsContractShape(type)) { yield return $"{name}: не record/enum/исключение — контракт фасада это данные, роль или шов контрактом быть не может{RecordShapeNote(type)}"; continue; }
            if (!typeof(Exception).IsAssignableFrom(type) && !signature.Contains(type))
                yield return $"{name}: нет ни в сигнатуре public/internal члена {facade.Name} (включая публичную поверхность её типов), ни среди наследников Exception — "
                    + "это не контракт фасада (данные ядра идут в CoreDataContracts поимённо)";
        }
    }
}
