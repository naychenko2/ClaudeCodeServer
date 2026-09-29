using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.HandsBridge.Browser.Snapshot;

/// <summary>
/// Готовый снимок страницы для модели.
/// </summary>
/// <param name="Text">Строки вида <c>- button "Войти" [ref=e12]</c>, отступ — глубина.</param>
/// <param name="Refs">Ссылка снимка → <c>backendDOMNodeId</c>; только ссылки, попавшие в текст.</param>
/// <param name="InputNodes">Узлов во входном AX-дереве.</param>
/// <param name="Lines">Строк в тексте, включая хвост об обрезке.</param>
/// <param name="TruncatedNodes">Элементов снимка, не влезших в бюджет.</param>
public sealed record AxSnapshot(
    string Text,
    IReadOnlyDictionary<string, int> Refs,
    int InputNodes,
    int Lines,
    int TruncatedNodes);

/// <summary>
/// Сжатие ответа <c>Accessibility.getFullAXTree</c> в компактный снимок по образцу AI-снимка
/// Playwright. Чистая функция: одно и то же дерево даёт один и тот же текст и те же ссылки.
/// </summary>
public static class AxSnapshotFormatter
{
    /// <summary>Потолок символов снимка вместе с хвостом об обрезке.</summary>
    public const int DefaultBudgetChars = 12_000;

    private const int MaxNameChars = 120;
    private const int MaxTextChars = 300;

    // Запас под хвост: строка обрезки обязана влезть в бюджет всегда
    private const int TailReserveChars = 320;

    // Роли, по которым модель действует: только они получают ссылку
    private static readonly HashSet<string> InteractiveRoles = new(StringComparer.Ordinal)
    {
        "link", "button", "textbox", "searchbox", "combobox", "checkbox", "radio", "menuitem",
        "menuitemcheckbox", "menuitemradio", "tab", "option", "switch", "slider", "spinbutton", "treeitem",
    };

    // Контейнеры без смысла для модели: без имени растворяются, дети поднимаются к родителю
    private static readonly HashSet<string> TransparentRoles = new(StringComparer.Ordinal)
    {
        "generic", "none", "presentation", "LayoutTable", "LayoutTableRow", "LayoutTableCell",
    };

    // Узлы-шум: текстовые боксы дублируют StaticText, маркер списка — пуля, перевод строки — пусто
    private static readonly HashSet<string> DroppedRoles = new(StringComparer.Ordinal)
    {
        "InlineTextBox", "ListMarker", "LineBreak",
    };

    // Имя этих ролей Chrome склеивает из содержимого: при живых детях оно дублирует их текст
    private static readonly HashSet<string> NameFromContentRoles = new(StringComparer.Ordinal)
    {
        "cell", "gridcell", "row", "listitem", "LayoutTableCell", "LayoutTableRow",
    };

    // Поля ввода: значение печатается после двоеточия
    private static readonly HashSet<string> ValueRoles = new(StringComparer.Ordinal)
    {
        "textbox", "searchbox", "combobox", "slider", "spinbutton",
    };

    /// <param name="axTree">Результат команды: объект с массивом <c>nodes</c>.</param>
    /// <param name="rootBackendNodeId">Корень поддерева (элемент по ссылке прошлого снимка); null — вся страница.</param>
    /// <param name="budgetChars">Потолок символов текста снимка.</param>
    public static AxSnapshot Format(JsonElement axTree, int? rootBackendNodeId = null, int budgetChars = DefaultBudgetChars)
    {
        var nodes = ParseNodes(axTree, out var inputNodes);
        var root = FindRoot(nodes, rootBackendNodeId);
        if (root is null)
        {
            const string gone = "- (element not found on the page: take a fresh browser_snapshot)";
            return new AxSnapshot(gone, new Dictionary<string, int>(), inputNodes, 1, 0);
        }

        var items = new Simplifier(nodes).Run(root);
        return new Renderer(budgetChars).Run(items, inputNodes);
    }

    // ---------- разбор ----------

    private sealed record AxNode(
        string Id,
        bool Ignored,
        string Role,
        string Name,
        string Value,
        string[] ChildIds,
        string? ParentId,
        int? BackendId,
        string Attrs);

    private static Dictionary<string, AxNode> ParseNodes(JsonElement axTree, out int inputNodes)
    {
        var result = new Dictionary<string, AxNode>(StringComparer.Ordinal);
        inputNodes = 0;
        if (!axTree.TryGetProperty("nodes", out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        // Chrome повторяет в массиве некоторые InlineTextBox дважды — первый выигрывает
        inputNodes = array.GetArrayLength();
        foreach (var n in array.EnumerateArray())
        {
            var id = n.TryGetProperty("nodeId", out var idEl) ? idEl.ToString() : null;
            if (id is null || result.ContainsKey(id))
                continue;

            var children = n.TryGetProperty("childIds", out var ch) && ch.ValueKind == JsonValueKind.Array
                ? ch.EnumerateArray().Select(c => c.ToString()).ToArray()
                : [];

            result[id] = new AxNode(
                id,
                n.TryGetProperty("ignored", out var ig) && ig.ValueKind == JsonValueKind.True,
                InnerString(n, "role"),
                InnerString(n, "name"),
                InnerString(n, "value"),
                children,
                n.TryGetProperty("parentId", out var p) ? p.ToString() : null,
                n.TryGetProperty("backendDOMNodeId", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var bid) ? bid : null,
                ReadAttrs(n, InnerString(n, "role")));
        }
        return result;
    }

    // role/name/value в CDP — объект AXValue { type, value }
    private static string InnerString(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var v) || v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("value", out var inner))
            return "";
        return inner.ValueKind switch
        {
            JsonValueKind.String => inner.GetString() ?? "",
            JsonValueKind.Number => inner.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    // Состояния, от которых зависит действие модели; остальные свойства — шум
    private static string ReadAttrs(JsonElement node, string role)
    {
        if (!node.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var p in props.EnumerateArray())
        {
            var name = InnerStringRaw(p, "name");
            var value = InnerString(p, "value");
            var attr = (name, value) switch
            {
                ("checked", "true") => "[checked]",
                ("checked", "mixed") => "[checked=mixed]",
                ("pressed", "true") => "[pressed]",
                ("pressed", "mixed") => "[pressed=mixed]",
                ("selected", "true") => "[selected]",
                ("disabled", "true") => "[disabled]",
                ("expanded", "true") => "[expanded]",
                ("expanded", "false") => "[collapsed]",
                ("level", _) when value.Length > 0 && role == "heading" => $"[level={value}]",
                _ => null,
            };
            if (attr is not null)
                sb.Append(' ').Append(attr);
        }
        return sb.ToString();
    }

    private static string InnerStringRaw(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static AxNode? FindRoot(Dictionary<string, AxNode> nodes, int? rootBackendNodeId)
    {
        if (rootBackendNodeId is { } backendId)
        {
            // У одного DOM-узла бывает и проигнорированный двойник — берём значимый
            return nodes.Values.FirstOrDefault(n => n.BackendId == backendId && !n.Ignored)
                ?? nodes.Values.FirstOrDefault(n => n.BackendId == backendId);
        }
        return nodes.Values.FirstOrDefault(n => n.ParentId is null || !nodes.ContainsKey(n.ParentId));
    }

    // ---------- упрощение дерева ----------

    private sealed class Item
    {
        public string Role = "";
        public string Name = "";
        public string Attrs = "";
        public string Text = "";
        public int? BackendId;
        public bool Interactive;
        public List<Item> Children = [];

        public bool IsText => Role.Length == 0;

        public int Count() => 1 + Children.Sum(c => c.Count());
    }

    private sealed class Simplifier(Dictionary<string, AxNode> nodes)
    {
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);

        public List<Item> Run(AxNode root) => Visit(root, "");

        // Возвращает 0..n элементов: узел, растворившийся в родителе, отдаёт своих детей
        private List<Item> Visit(AxNode node, string ancestorName)
        {
            if (!_visited.Add(node.Id) || DroppedRoles.Contains(node.Role))
                return [];

            if (node.Role == "StaticText")
                return node.Ignored ? [] : TextItem(node.Name, ancestorName);

            var name = Normalize(node.Name);
            var nameFromContent = NameFromContentRoles.Contains(node.Role) && node.ChildIds.Length > 0;
            var childAncestorName = name.Length > 0 && !node.Ignored && !nameFromContent ? name : ancestorName;
            var children = VisitChildren(node, childAncestorName);
            if (nameFromContent && children.Count > 0)
                name = "";

            if (node.Ignored)
                return children;

            // Корень страницы не печатается: заголовок и адрес мост даёт шапкой снимка
            var interactive = InteractiveRoles.Contains(node.Role);
            if (node.Role == "RootWebArea" || (!interactive && name.Length == 0 && TransparentRoles.Contains(node.Role)))
                return children;

            var item = new Item
            {
                Role = node.Role,
                Name = name,
                Attrs = node.Attrs,
                BackendId = node.BackendId,
                Interactive = interactive && node.BackendId is not null,
            };
            if (ValueRoles.Contains(node.Role))
                item.Text = Normalize(node.Value);

            if (!item.Interactive && name.Length == 0 && item.Text.Length == 0)
            {
                // Пустой неинтерактивный узел не несёт ничего; с одним ребёнком цепочка схлопывается
                if (children.Count == 0)
                    return [];
                if (children.Count == 1 && !children[0].IsText)
                    return children;
            }

            // Единственный текст без имени у узла печатается в его же строке: "- paragraph: …"
            if (children is [{ IsText: true } only] && item.Text.Length == 0)
                item.Text = only.Text;
            else
                item.Children = children;
            return [item];
        }

        private List<Item> VisitChildren(AxNode node, string ancestorName)
        {
            var result = new List<Item>();
            var run = new StringBuilder();

            void FlushRun()
            {
                if (run.Length > 0)
                    AppendMerged(result, TextItem(run.ToString(), ancestorName));
                run.Clear();
            }

            foreach (var childId in node.ChildIds)
            {
                if (!nodes.TryGetValue(childId, out var child))
                    continue;

                // Соседние StaticText одного родителя — куски одного текста: склеиваются как есть
                if (child.Role == "StaticText" && !child.Ignored)
                {
                    if (_visited.Add(child.Id))
                        run.Append(child.Name);
                    continue;
                }

                FlushRun();
                AppendMerged(result, Visit(child, ancestorName));
            }
            FlushRun();

            // Подпись поля ("LabelText: Пароль" рядом с textbox "Пароль") повторяет имя соседа
            var siblingNames = result.Where(i => i.Interactive && i.Name.Length > 0).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            result.RemoveAll(i => i.Children.Count == 0 && i.Name.Length == 0 && siblingNames.Contains(i.Text));
            return result;
        }

        // Текст из разных растворившихся контейнеров склеивается через пробел
        private static void AppendMerged(List<Item> into, List<Item> items)
        {
            foreach (var item in items)
            {
                if (item.IsText && into.Count > 0 && into[^1].IsText)
                    into[^1].Text = into[^1].Text + " " + item.Text;
                else
                    into.Add(item);
            }
        }

        // Выбрасывается текст, повторяющий имя предка ("link "Войти"" → текст "Войти"), и голые
        // разделители без букв и цифр ("|", "(", "·") — на ленте ссылок они удваивают число строк
        private static List<Item> TextItem(string raw, string ancestorName)
        {
            var text = Normalize(raw);
            if (!text.Any(char.IsLetterOrDigit) || (ancestorName.Length > 0 && ancestorName.Contains(text, StringComparison.Ordinal)))
                return [];
            return [new Item { Text = text }];
        }
    }

    private static string Normalize(string s)
    {
        if (s.Length == 0)
            return s;
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ---------- печать ----------

    private sealed class Renderer(int budgetChars)
    {
        private readonly StringBuilder _sb = new();
        private readonly Dictionary<string, int> _refs = new(StringComparer.Ordinal);
        private int _lines;
        private int _truncated;
        private bool _full;

        public AxSnapshot Run(List<Item> items, int inputNodes)
        {
            foreach (var item in items)
                Render(item, 0);

            if (_truncated > 0)
            {
                _sb.Append(
                    $"- ... {_truncated} more elements not shown: the snapshot is capped at {budgetChars} characters. " +
                    "To see a part of the page, call browser_snapshot with the ref of an element there to get just its subtree.");
                _lines++;
            }
            else if (_lines == 0)
            {
                _sb.Append("- (page is empty)");
                _lines++;
            }

            return new AxSnapshot(_sb.ToString().TrimEnd('\n'), _refs, inputNodes, _lines, _truncated);
        }

        private void Render(Item item, int depth)
        {
            if (_full)
            {
                _truncated += item.Count();
                return;
            }

            var line = FormatLine(item, depth, $"e{_refs.Count + 1}");
            if (_sb.Length + line.Length + 1 > budgetChars - TailReserveChars)
            {
                _full = true;
                _truncated += item.Count();
                return;
            }

            if (item.Interactive)
                _refs[$"e{_refs.Count + 1}"] = item.BackendId!.Value;
            _sb.Append(line).Append('\n');
            _lines++;

            foreach (var child in item.Children)
                Render(child, depth + 1);
        }

        private static string FormatLine(Item item, int depth, string nextRef)
        {
            var sb = new StringBuilder();
            sb.Append(' ', depth * 2).Append("- ");
            if (item.IsText)
                return sb.Append("text: ").Append(Clip(item.Text, MaxTextChars)).ToString();

            sb.Append(item.Role);
            if (item.Name.Length > 0)
                sb.Append(' ').Append(Quote(Clip(item.Name, MaxNameChars)));
            sb.Append(item.Attrs);
            if (item.Interactive)
                sb.Append(" [ref=").Append(nextRef).Append(']');
            if (item.Text.Length > 0)
                sb.Append(": ").Append(Clip(item.Text, MaxTextChars));
            return sb.ToString();
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
