using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>
/// Находка проверки модели: вид (<see cref="ArchitectureModelValidator"/>.Kind*), id элемента,
/// у которого она найдена, и текст для человека и модели.
/// </summary>
public sealed record ArchitectureModelFinding(string Kind, string ElementId, string Text);

/// <summary>
/// Проверка C4-модели Viaduct на несостыковки, которые редактор молча рисует не так, как
/// записано: висящие ссылки (родитель, цель связи или шаг потока указывают на элемент,
/// которого нет), точки связи, которых у карточки нет, и дубли id. Порт по смыслу проверки
/// gpb-event-vscode (<c>danglingRefs</c>/<c>unknownHandles</c>, коммит 4a6ef85) плюс дубли id.
///
/// Чистая функция над деревом файла; запись по находкам НЕ блокируется — это
/// предупреждение (обоснование — docs/features/architecture-section.md, «Проверка модели»).
/// Модель пишут три источника (редактор, «Собрать из кода», тулсет arch_*), поэтому
/// проверка живёт на сервере и отдаётся в ответах всех трёх путей.
/// </summary>
public static class ArchitectureModelValidator
{
    public const string KindDanglingParent = "dangling_parent";
    public const string KindDanglingConnection = "dangling_connection";
    public const string KindDanglingFlowStep = "dangling_flow_step";
    public const string KindUnknownHandle = "unknown_handle";
    public const string KindDuplicateId = "duplicate_id";

    // Коллекции элементов модели и заголовок уровня для текста находки
    private static readonly (string Array, string Title)[] ElementArrays =
    [
        ("systems", "система"),
        ("containers", "контейнер"),
        ("components", "компонент"),
        ("codeElements", "код"),
    ];

    private const string FlowsArray = "dataFlows";

    // Ссылки элемента на родителей: поле и коллекция, где родитель должен лежать
    private static readonly Dictionary<string, (string Field, string Parents, string ParentsTitle)[]> ParentRefs = new()
    {
        ["containers"] = [("systemId", "systems", "систем")],
        ["components"] = [("systemId", "systems", "систем"), ("containerId", "containers", "контейнеров")],
        ["codeElements"] =
        [
            ("systemId", "systems", "систем"), ("containerId", "containers", "контейнеров"),
            ("componentId", "components", "компонентов"),
        ],
    };

    // Точки связи обычной карточки — так их называет редактор: сторона с номером по порядку
    // сторон, а у стороны ещё слоты — у левой/правой второй «-s1», у верхней/нижней крайние
    // «-s0» и «-s2» вокруг среднего. Сверено с бандлом Viaduct (handlePositions + слоты)
    private static readonly HashSet<string> CardSource = Slotted("source", ["right", "bottom", "left", "top"]);
    private static readonly HashSet<string> CardTarget = Slotted("target", ["left", "top", "bottom", "right"]);

    // У карточки-таблицы (элемент с columns) точки без номера
    private static readonly HashSet<string> TableSource = new(StringComparer.Ordinal) { "source-right", "source-bottom" };
    private static readonly HashSet<string> TableTarget = new(StringComparer.Ordinal) { "target-left", "target-top" };

    /// <summary>Сколько находок показывать в текстовых ответах (сводка тулсета и т.п.).</summary>
    public const int MaxShown = 20;

    /// <summary>
    /// Находки по файлу модели (persist-обёртка <c>{ state: { model } }</c>). Нет модели или
    /// не та форма — находок нет: форму стережёт хранилище, а не эта проверка.
    /// </summary>
    public static IReadOnlyList<ArchitectureModelFinding> Validate(JsonNode? doc)
    {
        // Индексатор JsonNode[string] бросает на не-объекте (корень-массив, "state": 1) —
        // форму проверяем явно: предупреждение не имеет права ронять запрос
        if (doc is not JsonObject root || root["state"] is not JsonObject state
            || state["model"] is not JsonObject model) return [];

        var elements = new List<(JsonObject Node, string Array, string Title)>();
        foreach (var (array, title) in ElementArrays)
            if (model[array] is JsonArray list)
                elements.AddRange(list.OfType<JsonObject>().Select(o => (o, array, title)));
        var flows = model[FlowsArray] is JsonArray fl ? fl.OfType<JsonObject>().ToList() : [];

        var findings = new List<ArchitectureModelFinding>();
        findings.AddRange(DuplicateIds(elements, flows));

        var idsByArray = ElementArrays.ToDictionary(a => a.Array, _ => new HashSet<string>(StringComparer.Ordinal));
        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (node, array, _) in elements)
            if (Str(node["id"]) is { } id)
            {
                idsByArray[array].Add(id);
                byId.TryAdd(id, node);
            }
        // Шаг потока может ссылаться и на другой поток (канал/эндпоинт — это компоненты)
        var all = new HashSet<string>(byId.Keys, StringComparer.Ordinal);
        foreach (var flow in flows)
            if (Str(flow["id"]) is { } id) all.Add(id);

        foreach (var (node, array, title) in elements)
        {
            var id = Str(node["id"]) ?? "";
            var where = Label(title, node);
            foreach (var (field, parents, parentsTitle) in ParentRefs.GetValueOrDefault(array) ?? [])
                if (Str(node[field]) is { } parent && !idsByArray[parents].Contains(parent))
                    findings.Add(new(KindDanglingParent, id,
                        $"{where}: {field} → {parent} — такого элемента среди {parentsTitle} нет"));

            if (node["connections"] is not JsonArray connections) continue;
            foreach (var connection in connections.OfType<JsonObject>())
            {
                var targetId = Str(connection["targetId"]);
                if (targetId is null) continue;
                if (!all.Contains(targetId))
                {
                    // Висящая связь — одна находка; про точки у несуществующей цели не говорим
                    findings.Add(new(KindDanglingConnection, id, $"{where}: связь → {targetId} — такого элемента нет"));
                    continue;
                }
                var target = byId.GetValueOrDefault(targetId);
                var wrong = new List<string>();
                CheckHandle(connection["sourceHandle"], node, isSource: true, wrong);
                if (target is not null) CheckHandle(connection["targetHandle"], target, isSource: false, wrong);
                if (wrong.Count > 0)
                {
                    var to = target is not null && Str(target["name"]) is { Length: > 0 } n ? $"«{n}»" : targetId;
                    findings.Add(new(KindUnknownHandle, id,
                        $"{where}: связь → {to} не нарисуется — у карточки нет точки {string.Join(", ", wrong)}"));
                }
            }
        }

        foreach (var flow in flows)
        {
            var where = Label("поток данных", flow);
            if (flow["steps"] is not JsonArray steps) continue;
            foreach (var step in steps.OfType<JsonObject>())
            {
                var missing = StepRefs(step).Distinct(StringComparer.Ordinal).Where(r => !all.Contains(r)).ToList();
                if (missing.Count == 0) continue;
                var name = Str(step["name"]) is { Length: > 0 } s ? $" «{s}»" : "";
                findings.Add(new(KindDanglingFlowStep, Str(flow["id"]) ?? "",
                    $"{where}: шаг{name} ссылается на {string.Join(", ", missing)} — таких элементов нет"));
            }
        }
        return findings;
    }

    /// <summary>То же по тексту файла; не JSON — находок нет (это отказ хранилища, не предупреждение).</summary>
    public static IReadOnlyList<ArchitectureModelFinding> ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        try
        {
            return Validate(JsonNode.Parse(content));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Находки строками для текстового ответа: первые <see cref="MaxShown"/> и «…и ещё N».</summary>
    public static string Render(IReadOnlyList<ArchitectureModelFinding> findings)
    {
        var lines = findings.Take(MaxShown).Select(f => "- " + f.Text).ToList();
        if (findings.Count > MaxShown) lines.Add($"…и ещё {findings.Count - MaxShown}.");
        return string.Join("\n", lines);
    }

    // Дубль id: редактор держит элементы в словаре по id — второй затирает первый, а связи
    // на этот id уходят не туда. Элементы и потоки — разные пространства id
    private static IEnumerable<ArchitectureModelFinding> DuplicateIds(
        List<(JsonObject Node, string Array, string Title)> elements, List<JsonObject> flows)
    {
        var groups = elements
            .Select(e => (Id: Str(e.Node["id"]), Label: Label(e.Title, e.Node)))
            .Concat(flows.Select(f => (Id: Str(f["id"]) is { } fid ? FlowsArray + ":" + fid : null, Label: Label("поток данных", f))))
            .Where(e => e.Id is not null)
            .GroupBy(e => e.Id!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var g in groups)
        {
            var id = g.Key.StartsWith(FlowsArray + ":", StringComparison.Ordinal) ? g.Key[(FlowsArray.Length + 1)..] : g.Key;
            yield return new(KindDuplicateId, id,
                $"id {id} повторяется ({g.Count()}): {string.Join(", ", g.Select(e => e.Label))} — редактор покажет только один");
        }
    }

    // Точка задана и у карточки её нет — в список «неверных» с перечнем допустимых.
    // Точка не задана — редактор ставит свою по умолчанию, это не находка
    private static void CheckHandle(JsonNode? value, JsonObject owner, bool isSource, List<string> wrong)
    {
        if (Str(value) is not { } handle) return;
        var table = owner["columns"] is JsonArray;
        var known = (isSource, table) switch
        {
            (true, true) => TableSource,
            (true, false) => CardSource,
            (false, true) => TableTarget,
            (false, false) => CardTarget,
        };
        if (known.Contains(handle)) return;
        var shown = string.Join(", ", known.Where(h => !h.Contains("-s", StringComparison.Ordinal)));
        wrong.Add($"{(isSource ? "sourceHandle" : "targetHandle")} «{handle}» (есть {shown})");
    }

    // Элементы, на которые ссылается шаг потока данных: концы, связи, эндпоинты и каналы
    private static IEnumerable<string> StepRefs(JsonObject step)
    {
        foreach (var end in new[] { step["from"], step["to"] })
            if (end is JsonObject o && Str(o["id"]) is { } id) yield return id;
        if (step["connections"] is JsonArray links)
            foreach (var link in links.OfType<JsonObject>())
            {
                if (Str(link["sourceId"]) is { } s) yield return s;
                if (Str(link["targetId"]) is { } t) yield return t;
            }
        foreach (var list in new[] { step["endpointIds"], step["channelIds"] })
            if (list is JsonArray a)
                foreach (var item in a)
                    if (Str(item) is { } r) yield return r;
    }

    // Порядок сторон задаёт номер точки; слоты — как в бандле редактора
    private static HashSet<string> Slotted(string kind, string[] sides)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sides.Length; i++)
        {
            var @base = $"{kind}-{sides[i]}-{i}";
            if (sides[i] is "left" or "right")
            {
                set.Add(@base);
                set.Add(@base + "-s1");
            }
            else
            {
                set.Add(@base + "-s0");
                set.Add(@base);
                set.Add(@base + "-s2");
            }
        }
        return set;
    }

    // Элемент в находке: уровень, имя и id — по одному id человек элемент не найдёт
    private static string Label(string title, JsonObject node)
    {
        var name = Str(node["name"]) is { Length: > 0 } n ? $" «{n}»" : "";
        return $"{title}{name} ({Str(node["id"]) ?? "без id"})";
    }

    // Пустая строка — «ссылки нет», как отсутствующее поле: иначе ложная «висящая → »
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
