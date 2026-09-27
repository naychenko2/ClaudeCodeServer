using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>Правка поля элемента: null — «не трогать», пустая строка — «очистить».</summary>
public sealed record ArchitectureElementPatch(
    string? Name = null,
    string? Description = null,
    string? Technology = null,
    string? Url = null,
    IReadOnlyList<string>? Tags = null,
    bool? External = null);

/// <summary>Правка связи: null — «не трогать», пустая строка — «очистить».</summary>
public sealed record ArchitectureConnectionPatch(
    string? Label = null,
    string? Technology = null,
    string? Description = null);

/// <summary>Новый элемент: уровень C4, имя и родитель (у системы — нет), прочее — по желанию.</summary>
public sealed record ArchitectureElementDraft(
    string Level,
    string Name,
    string? ParentId = null,
    string? Description = null,
    string? Technology = null,
    string? Url = null,
    IReadOnlyList<string>? Tags = null,
    bool External = false);

/// <summary>Итог удаления: удалённые элементы (сам и вложенные), связи и документы.</summary>
public sealed record ArchitectureDeleteResult(
    IReadOnlyList<ArchitectureModelEditor.Element> Elements,
    int Connections,
    int Documents);

/// <summary>Правка не применима к модели (нет элемента, цикл, пустое имя…): текст — для модели.</summary>
public sealed class ArchitectureEditException(string message) : Exception(message);

/// <summary>
/// Чтение и точечная правка persist-обёртки Viaduct (<c>{ state: { model: FlatC4Model }, version }</c>)
/// для MCP-тулсета. Чистые функции над JSON: файл, версию и замок держит
/// <see cref="ArchitectureModelStore"/>, здесь только разбор, выдача и мутация дерева.
///
/// Правка не пересоздаёт элемент: меняются ровно названные поля, всё остальное
/// (позиция, доки, чужие поля редактора) остаётся как было — ровно как у генератора
/// (<see cref="ArchitectureModelMerger"/>).
/// </summary>
public static class ArchitectureModelEditor
{
    /// <summary>Потолок описания карточки — как в редакторе Viaduct (ELEMENT_DESCRIPTION_MAX).</summary>
    public const int DescriptionMax = 120;

    /// <summary>Тегов на элементе и длина тега — как в редакторе Viaduct.</summary>
    public const int MaxTags = 3;
    public const int TagMaxLength = 32;

    // Порядок уровней C4 = порядок массивов модели; поле родителя — у каждого уровня своё
    private static readonly (string Array, string Level, string? ParentField)[] Levels =
    [
        ("systems", "system", null),
        ("containers", "container", "systemId"),
        ("components", "component", "containerId"),
        ("codeElements", "code", "componentId"),
    ];

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Элемент модели с уровнем C4 (массив, из которого он взят).</summary>
    public sealed record Element(JsonObject Node, string Level)
    {
        public string Id => Node["id"].Str() ?? "";
        public string Name => Node["name"].Str() ?? "";
        public string? ParentId => Node[ParentFieldOf(Level) ?? ""].Str();
    }

    /// <summary>Разбор файла: корень-обёртка. Не JSON-объект → <see cref="ArchitectureEditException"/>.</summary>
    public static JsonObject Parse(string content)
    {
        try
        {
            return JsonNode.Parse(content) as JsonObject
                ?? throw new ArchitectureEditException("Файл модели — не JSON-объект.");
        }
        catch (JsonException ex)
        {
            throw new ArchitectureEditException($"Файл модели повреждён (не разбирается как JSON): {ex.Message}");
        }
    }

    /// <summary>Сериализация обратно в файл: отступы, кириллица без \u-экранирования (читается в git diff).</summary>
    public static string Serialize(JsonObject doc) => doc.ToJsonString(Indented) + "\n";

    public static IReadOnlyList<Element> Elements(JsonObject doc)
    {
        var model = doc["state"]?["model"] as JsonObject;
        if (model is null) return [];
        var result = new List<Element>();
        foreach (var (array, level, _) in Levels)
        {
            if (model[array] is not JsonArray list) continue;
            result.AddRange(list.OfType<JsonObject>().Select(o => new Element(o, level)));
        }
        return result;
    }

    // --- Чтение ---

    /// <summary>
    /// Сводка модели: счётчики и дерево «система → контейнеры → компоненты» с id —
    /// чтобы модель могла сразу адресовать элемент в arch_get_element.
    /// </summary>
    public static string RenderContext(JsonObject doc, int maxLines = 300)
    {
        var elements = Elements(doc);
        var byId = ById(elements);
        var sb = new StringBuilder();
        var counts = Levels.Select(l => $"{LevelTitle(l.Level)}: {elements.Count(e => e.Level == l.Level)}");
        var connections = elements.Sum(e => Connections(e.Node).Count());
        sb.AppendLine($"Элементов — {string.Join(", ", counts)}; связей: {connections}.");

        var lines = 0;
        var truncated = false;
        foreach (var root in elements.Where(e => e.ParentId is null || !byId.ContainsKey(e.ParentId)))
            Walk(root, 0);
        if (truncated)
            sb.AppendLine($"… дерево обрезано на {maxLines} строках — ищи остальное через arch_search.");
        return sb.ToString().TrimEnd();

        void Walk(Element e, int depth)
        {
            if (lines >= maxLines) { truncated = true; return; }
            sb.Append(new string(' ', depth * 2)).Append("- ").AppendLine(Brief(e));
            lines++;
            foreach (var child in elements.Where(c => c.ParentId == e.Id && c.Level != e.Level))
                Walk(child, depth + 1);
        }
    }

    /// <summary>Поиск по имени, описанию, технологии, тегам и id — подстрокой без учёта регистра.</summary>
    public static IReadOnlyList<Element> Search(JsonObject doc, string query, string? level, int limit)
    {
        var q = query.Trim();
        return Elements(doc)
            .Where(e => level is null || string.Equals(e.Level, level, StringComparison.OrdinalIgnoreCase))
            .Where(e => q.Length == 0 || Haystack(e).Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase)))
            // Совпадение по имени — выше совпадения в описании
            .OrderBy(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(limit)
            .ToList();
    }

    public static string RenderSearch(JsonObject doc, IReadOnlyList<Element> found)
    {
        var byId = ById(Elements(doc));
        return string.Join("\n", found.Select(e => $"{Brief(e)} — {PathOf(e, byId)}"));
    }

    /// <summary>
    /// Элемент целиком: поля, родитель, дети, исходящие и входящие связи с именами,
    /// документы с полным markdown.
    /// </summary>
    public static string RenderElement(JsonObject doc, Element e)
    {
        var elements = Elements(doc);
        var byId = ById(elements);
        var sb = new StringBuilder();
        sb.AppendLine($"# {e.Name} [{LevelTitle(e.Level)}] id={e.Id}");
        sb.AppendLine($"Путь: {PathOf(e, byId)}");
        AppendField(sb, "Описание", e.Node["description"].Str());
        AppendField(sb, "Технология", e.Node["technology"].Str());
        AppendField(sb, "Ссылка", e.Node["url"].Str());
        if (e.Node["external"] is JsonValue ext && ext.TryGetValue<bool>(out var isExternal) && isExternal)
            sb.AppendLine("Внешний: да");
        var tags = TagsOf(e.Node);
        if (tags.Count > 0) sb.AppendLine($"Теги: {string.Join(", ", tags)}");
        // Контракт endpoint/channel/table у элементов SDK — отдаём как есть, без толкования
        foreach (var key in new[] { "kind", "method", "path", "protocol" })
            AppendField(sb, key, e.Node[key].Str());

        var children = elements.Where(c => c.ParentId == e.Id && c.Level != e.Level).ToList();
        if (children.Count > 0)
        {
            sb.AppendLine().AppendLine($"## Вложенные ({children.Count})");
            foreach (var c in children) sb.AppendLine("- " + Brief(c));
        }

        var outgoing = Connections(e.Node).ToList();
        if (outgoing.Count > 0)
        {
            sb.AppendLine().AppendLine($"## Исходящие связи ({outgoing.Count})");
            foreach (var c in outgoing) sb.AppendLine("→ " + ConnectionLine(c, c["targetId"].Str(), byId));
        }
        var incoming = elements
            .SelectMany(src => Connections(src.Node).Where(c => c["targetId"].Str() == e.Id).Select(c => (src, c)))
            .ToList();
        if (incoming.Count > 0)
        {
            sb.AppendLine().AppendLine($"## Входящие связи ({incoming.Count})");
            foreach (var (src, c) in incoming) sb.AppendLine("← " + ConnectionLine(c, src.Id, byId));
        }

        var docs = DocsOf(e.Node);
        if (docs.Count > 0)
        {
            sb.AppendLine().AppendLine($"## Документы ({docs.Count})");
            foreach (var d in docs)
            {
                sb.AppendLine().AppendLine($"### {d["title"].Str() ?? "(без названия)"}");
                sb.AppendLine(d["markdown"].Str() ?? "");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // --- Правка ---

    /// <summary>
    /// Пустая модель для проекта без файла — той же формы, что пишет «Собрать из кода»
    /// (<see cref="ArchitectureModelMerger.EnsureShape"/>), чтобы редактор открыл её как свою.
    /// </summary>
    public static JsonObject NewDocument()
    {
        var doc = new JsonObject();
        ArchitectureModelMerger.EnsureShape(doc);
        return doc;
    }

    public static Element? Find(JsonObject doc, string id) =>
        Elements(doc).FirstOrDefault(e => e.Id == id);

    /// <summary>Правит названные поля элемента; возвращает список изменённых полей (пусто — нечего менять).</summary>
    public static IReadOnlyList<string> UpdateElement(JsonObject doc, string id, ArchitectureElementPatch patch)
    {
        var e = Find(doc, id) ?? throw NotFound(id);
        var changed = new List<string>();
        if (patch.Name is { } name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArchitectureEditException("Имя элемента не может быть пустым.");
            SetString(e.Node, "name", name.Trim(), changed);
        }
        if (patch.Description is { } description)
        {
            var clamped = description.Trim();
            if (clamped.Length > DescriptionMax) clamped = clamped[..DescriptionMax];
            SetString(e.Node, "description", clamped, changed);
        }
        if (patch.Technology is { } technology) SetString(e.Node, "technology", technology.Trim(), changed);
        if (patch.Url is { } url) SetString(e.Node, "url", url.Trim(), changed);
        if (patch.Tags is { } tags)
        {
            var normalized = NormalizeTags(tags);
            if (!TagsOf(e.Node).SequenceEqual(normalized))
            {
                if (normalized.Count == 0) e.Node.Remove("tags");
                else e.Node["tags"] = new JsonArray(normalized.Select(t => (JsonNode)t).ToArray());
                changed.Add("tags");
            }
        }
        if (patch.External is { } external)
        {
            var current = e.Node["external"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            if (current != external)
            {
                if (external) e.Node["external"] = true;
                else e.Node.Remove("external");
                changed.Add("external");
            }
        }
        return changed;
    }

    /// <summary>
    /// Создаёт или правит связь <paramref name="sourceId"/> → <paramref name="targetId"/>
    /// (одна связь на пару, как у генератора). Возвращает «created» / «updated» / «unchanged».
    /// </summary>
    public static string UpsertConnection(JsonObject doc, string sourceId, string targetId,
        ArchitectureConnectionPatch patch)
    {
        var source = Find(doc, sourceId) ?? throw NotFound(sourceId);
        if (Find(doc, targetId) is null) throw NotFound(targetId);
        if (sourceId == targetId) throw new ArchitectureEditException("Связь элемента с самим собой не поддерживается.");

        if (source.Node["connections"] is not JsonArray list)
        {
            list = new JsonArray();
            source.Node["connections"] = list;
        }
        var existing = list.OfType<JsonObject>().FirstOrDefault(c => c["targetId"].Str() == targetId);
        var created = existing is null;
        if (existing is null)
        {
            existing = new JsonObject { ["targetId"] = targetId };
            list.Add(existing);
        }
        var changed = new List<string>();
        if (patch.Label is { } label) SetString(existing, "label", label.Trim(), changed);
        if (patch.Technology is { } technology) SetString(existing, "technology", technology.Trim(), changed);
        if (patch.Description is { } description) SetString(existing, "description", description.Trim(), changed);
        return created ? "created" : changed.Count > 0 ? "updated" : "unchanged";
    }

    /// <summary>Удаляет связь source → target; false — такой связи не было.</summary>
    public static bool RemoveConnection(JsonObject doc, string sourceId, string targetId)
    {
        var source = Find(doc, sourceId) ?? throw NotFound(sourceId);
        if (source.Node["connections"] is not JsonArray list) return false;
        var victims = list.OfType<JsonObject>().Where(c => c["targetId"].Str() == targetId).ToList();
        foreach (var v in victims) list.Remove(v);
        return victims.Count > 0;
    }

    /// <summary>
    /// Создаёт элемент: родитель строго на ступень выше (у системы родителя нет), id — UUID,
    /// как у элементов редактора Viaduct (crypto.randomUUID), позиция — первая свободная
    /// клетка сетки среди соседей того же родителя (та же сетка, что у генератора).
    /// Имя, уже занятое у того же родителя, — отказ: повтор вызова не плодит дубли.
    /// </summary>
    public static Element CreateElement(JsonObject doc, ArchitectureElementDraft draft)
    {
        var levelIndex = Array.FindIndex(Levels, l => l.Level == draft.Level);
        if (levelIndex < 0)
            throw new ArchitectureEditException(
                $"Неизвестный уровень «{draft.Level}» — допустимы system, container, component, code.");
        var (arrayName, level, parentField) = Levels[levelIndex];
        var name = draft.Name.Trim();
        if (name.Length == 0) throw new ArchitectureEditException("Имя элемента не может быть пустым.");
        var parentId = string.IsNullOrWhiteSpace(draft.ParentId) ? null : draft.ParentId.Trim();

        Element? parent = null;
        if (parentField is null)
        {
            if (parentId is not null)
                throw new ArchitectureEditException("Система — верхний уровень C4, parentId у неё не указывается.");
        }
        else
        {
            var expected = Levels[levelIndex - 1].Level;
            if (parentId is null)
                throw new ArchitectureEditException(
                    $"Для уровня {level} нужен parentId — id элемента уровня {expected}.");
            parent = Find(doc, parentId) ?? throw NotFound(parentId);
            if (parent.Level != expected)
                throw new ArchitectureEditException(
                    $"Родитель «{parent.Name}» — уровня {parent.Level}, а для {level} нужен родитель уровня {expected}.");
        }

        var model = EnsureModel(doc);
        if (model[arrayName] is not JsonArray list)
        {
            list = new JsonArray();
            model[arrayName] = list;
        }
        var siblings = list.OfType<JsonObject>()
            .Where(o => parentField is null || o[parentField].Str() == parentId)
            .ToList();
        if (siblings.FirstOrDefault(o => string.Equals(o["name"].Str()?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            is { } twin)
            throw new ArchitectureEditException(
                $"У этого родителя уже есть элемент «{twin["name"].Str()}» (id={twin["id"].Str()}) — правь его через arch_update_element.");

        var (x, y) = ArchitectureModelMerger.FreeCell(siblings,
            ArchitectureModelMerger.GridColumnsFor(siblings.Count + 1));
        var node = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = name,
            ["type"] = level,
            ["position"] = new JsonObject { ["x"] = x, ["y"] = y },
            ["connections"] = new JsonArray(),
        };
        // Цепочка предков — как у генератора: компонент знает и контейнер, и систему
        if (parent is not null)
        {
            node[parentField!] = parent.Id;
            foreach (var (_, _, ancestorField) in Levels[..levelIndex])
                if (ancestorField is not null && ancestorField != parentField && parent.Node[ancestorField].Str() is { } a)
                    node[ancestorField] = a;
        }
        list.Add(node);

        var created = new Element(node, level);
        // Описание, технология, теги, url, external — той же нормализацией, что и правка
        UpdateElement(doc, created.Id, new ArchitectureElementPatch(
            Description: draft.Description, Technology: draft.Technology, Url: draft.Url,
            Tags: draft.Tags, External: draft.External));
        return created;
    }

    /// <summary>
    /// Удаляет элемент со всеми вложенными, связями, касающимися удалённых (в обе стороны),
    /// и их документами. Элемент любого уровня с вложенными — только при <paramref name="cascade"/>:
    /// одним вызовом по ошибке полмодели не сносится.
    /// </summary>
    public static ArchitectureDeleteResult DeleteElement(JsonObject doc, string id, bool cascade)
    {
        var root = Find(doc, id) ?? throw NotFound(id);
        var elements = Elements(doc);

        // Поддерево — по любому полю предка (systemId/containerId/componentId), до неподвижной
        // точки: элемент с битым промежуточным родителем тоже уходит вместе с системой
        var removed = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var ancestorFields = Levels.Select(l => l.ParentField).OfType<string>().ToArray();
        bool grew;
        do
        {
            grew = false;
            foreach (var e in elements)
                if (!removed.Contains(e.Id) && ancestorFields.Any(f => e.Node[f].Str() is { } p && removed.Contains(p)))
                    grew |= removed.Add(e.Id);
        } while (grew);
        var victims = elements.Where(e => removed.Contains(e.Id)).ToList();

        // Предохранитель — для любого уровня: удаление контейнера с компонентами сносит не меньше
        if (victims.Count > 1 && !cascade)
        {
            var children = elements.Where(c => c.ParentId == root.Id && c.Level != root.Level).ToList();
            // Прямых детей нет, а потомки есть (битая промежуточная цепочка) — показываем их,
            // а не пустой список
            var (title, shown) = children.Count > 0
                ? ("Прямые дети", children)
                : ("Найденные вложенные", victims.Where(v => v.Id != root.Id).ToList());
            var listed = string.Join("\n", shown.Take(20).Select(c => "- " + Brief(c)));
            var more = shown.Count > 20 ? $"\n… и ещё {shown.Count - 20}" : "";
            throw new ArchitectureEditException(
                $"У элемента «{root.Name}» [{LevelTitle(root.Level)}] есть вложенные "
                + $"({victims.Count - 1} элементов вместе с глубокими). "
                + "Удаление снесёт их все — если это и нужно, повтори вызов с cascade=true. "
                + $"{title}:\n{listed}{more}");
        }

        var connections = victims.Sum(v => Connections(v.Node).Count());
        var documents = victims.Sum(v => DocsOf(v.Node).Count);
        var model = (JsonObject)doc["state"]!["model"]!;
        foreach (var (arrayName, _, _) in Levels)
        {
            if (model[arrayName] is not JsonArray list) continue;
            foreach (var node in list.OfType<JsonObject>().Where(o => removed.Contains(o["id"].Str() ?? "")).ToList())
                list.Remove(node);
            // Входящие связи выживших к удалённым
            foreach (var survivor in list.OfType<JsonObject>())
            {
                if (survivor["connections"] is not JsonArray links) continue;
                foreach (var link in links.OfType<JsonObject>().Where(c => removed.Contains(c["targetId"].Str() ?? "")).ToList())
                {
                    links.Remove(link);
                    connections++;
                }
            }
        }

        // Холст стоял внутри удалённого — возвращаем его на уровень систем, иначе редактор
        // откроется на несуществующем элементе
        if (new[] { "activeSystemId", "activeContainerId", "activeComponentId" }
            .Any(f => model[f].Str() is { } a && removed.Contains(a)))
        {
            model.Remove("activeSystemId");
            model.Remove("activeContainerId");
            model.Remove("activeComponentId");
            model["viewLevel"] = "system";
        }
        return new ArchitectureDeleteResult(victims, connections, documents);
    }

    // --- Хелперы ---

    private static JsonObject EnsureModel(JsonObject doc)
    {
        if (doc["state"] is not JsonObject state)
        {
            state = new JsonObject();
            doc["state"] = state;
        }
        if (state["model"] is not JsonObject model)
        {
            model = new JsonObject();
            state["model"] = model;
        }
        return model;
    }

    private static ArchitectureEditException NotFound(string id) =>
        new($"Элемент «{id}» в модели не найден — возьми id из arch_search или arch_context.");

    private static string? ParentFieldOf(string level) =>
        Levels.FirstOrDefault(l => l.Level == level).ParentField;

    private static Dictionary<string, Element> ById(IEnumerable<Element> elements)
    {
        var map = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var e in elements) map.TryAdd(e.Id, e);
        return map;
    }

    private static string LevelTitle(string level) => level switch
    {
        "system" => "система",
        "container" => "контейнер",
        "component" => "компонент",
        "code" => "код",
        _ => level,
    };

    private static string Brief(Element e)
    {
        var tech = e.Node["technology"].Str() is { Length: > 0 } t ? $" ({t})" : "";
        var desc = e.Node["description"].Str() is { Length: > 0 } d ? $" — {d}" : "";
        return $"{e.Name}{tech} [{LevelTitle(e.Level)}] id={e.Id}{desc}";
    }

    private static string PathOf(Element e, Dictionary<string, Element> byId)
    {
        var names = new List<string> { e.Name };
        var seen = new HashSet<string>(StringComparer.Ordinal) { e.Id };
        var current = e;
        // seen — защита от цикла ссылок на родителя в испорченном файле
        while (current.ParentId is { } pid && byId.TryGetValue(pid, out var parent) && seen.Add(parent.Id))
        {
            names.Insert(0, parent.Name);
            current = parent;
        }
        return string.Join(" / ", names);
    }

    private static IEnumerable<string> Haystack(Element e)
    {
        yield return e.Id;
        yield return e.Name;
        if (e.Node["description"].Str() is { } d) yield return d;
        if (e.Node["technology"].Str() is { } t) yield return t;
        foreach (var tag in TagsOf(e.Node)) yield return tag;
    }

    private static IEnumerable<JsonObject> Connections(JsonObject node) =>
        node["connections"] is JsonArray list ? list.OfType<JsonObject>() : [];

    private static string ConnectionLine(JsonObject c, string? otherId, Dictionary<string, Element> byId)
    {
        var other = otherId is not null && byId.TryGetValue(otherId, out var el) ? $"{el.Name} (id={el.Id})" : $"id={otherId}";
        var label = c["label"].Str() is { Length: > 0 } l ? $" «{l}»" : "";
        var tech = c["technology"].Str() is { Length: > 0 } t ? $" [{t}]" : "";
        return other + label + tech;
    }

    // Документы элемента: массив documentations, плюс устаревшее одиночное поле
    // documentation, если его нет в массиве (как listEntityDocumentations у Viaduct)
    private static IReadOnlyList<JsonObject> DocsOf(JsonObject node)
    {
        var list = node["documentations"] is JsonArray a ? a.OfType<JsonObject>().ToList() : [];
        if (node["documentation"] is JsonObject legacy
            && !list.Any(d => d["id"].Str() is { } id && id == legacy["id"].Str()))
            list.Add(legacy);
        return list;
    }

    private static List<string> TagsOf(JsonObject node) =>
        node["tags"] is JsonArray a ? a.Select(t => t.Str()).OfType<string>().ToList() : [];

    // Нормализация тегов — как sanitizeTags у Viaduct: пробелы схлопнуты, без дублей
    // без учёта регистра, не больше трёх
    private static List<string> NormalizeTags(IEnumerable<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var item in raw)
        {
            var tag = string.Join(' ', item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (tag.Length > TagMaxLength) tag = tag[..TagMaxLength];
            if (tag.Length == 0 || !seen.Add(tag)) continue;
            result.Add(tag);
            if (result.Count >= MaxTags) break;
        }
        return result;
    }

    // Пустая строка — поле удаляется (редактор Viaduct хранит «нет значения» отсутствием поля)
    private static void SetString(JsonObject node, string field, string value, List<string> changed)
    {
        var current = node[field].Str() ?? "";
        if (current == value) return;
        if (value.Length == 0) node.Remove(field);
        else node[field] = value;
        changed.Add(field);
    }

    private static void AppendField(StringBuilder sb, string title, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"{title}: {value}");
    }

    // Строковое значение узла или null. private — одноимённый хелпер есть у Merger,
    // два доступных extension-метода в одном namespace дали бы неоднозначность вызова
    private static string? Str(this JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
