using System.Reflection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Перечень «контракт фасада» — единственный источник правды (решения лида 0b942ca6 и 98d2368c):
/// типы <c>ClaudeHomeServer.Services.Sessions</c>, которые есть в сигнатуре public/internal члена
/// <c>SessionManager</c> (включая generic-аргументы <c>Task&lt;…&gt;</c> и публичную поверхность таких
/// записей-результатов — так достижим <c>TurnResult</c>) либо наследуют
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

    /// <summary>Типы, достижимые из сигнатуры фасада через публичную поверхность (свойства, поля,
    /// параметры конструкторов) типов <paramref name="coreNamespace"/> и их вложенных типов:
    /// <c>TurnResult</c> не стоит в члене фасада, но это поле <c>SendAndWaitResult.Completed.Result</c>,
    /// которое вызывающий обязан прочесть. Роли ядра сюда не попадают сами собой: они не торчат из
    /// публичной поверхности записей-результатов; состав всё равно явный (<see cref="All"/>).</summary>
    internal static HashSet<Type> ReachableTypes(Type facade, string coreNamespace)
    {
        var seen = SignatureTypes(facade);
        var queue = new Queue<Type>(seen.Where(t => t.Namespace == coreNamespace));
        const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        void Add(Type? t)
        {
            if (t is null) return;
            if (t.HasElementType) { Add(t.GetElementType()); return; }
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) Add(a);
            while (t.DeclaringType is not null) t = t.DeclaringType;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) t = t.GetGenericTypeDefinition();
            if (t.Namespace == coreNamespace && seen.Add(t)) queue.Enqueue(t);
        }

        while (queue.Count > 0)
        {
            var root = queue.Dequeue();
            var family = new List<Type> { root };
            family.AddRange(root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            foreach (var t in family)
            {
                foreach (var p in t.GetProperties(pub)) Add(p.PropertyType);
                foreach (var f in t.GetFields(pub)) Add(f.FieldType);
                foreach (var c in t.GetConstructors(pub))
                    foreach (var p in c.GetParameters()) Add(p.ParameterType);
            }
        }
        return seen;
    }

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
            if (!typeof(Exception).IsAssignableFrom(type) && !signature.Contains(type))
                yield return $"{name}: нет ни в сигнатуре public/internal члена {facade.Name} (включая публичную поверхность её типов), ни среди наследников Exception — "
                    + "это не контракт фасада (данные ядра идут в CoreDataContracts поимённо)";
        }
    }
}
