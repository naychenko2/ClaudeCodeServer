using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Docs;

// Схема типов документов: секция «docTypes» файла .docs репозитория.
//
// Зачем в репозитории, а не в настройке продукта: тип документа — свойство самого корпуса
// («всё в docs/adr — это решения»), а не предпочтение конкретного владельца. Файл
// версионируется вместе с документами, поэтому схема одна у всех, кто открыл репозиторий.
//
// Чтение терпимое: мусор молча отбрасывается, а структурная ошибка уезжает отдельным
// сообщением и НЕ роняет область документации — схема не имеет права утащить её за собой.
internal static class DocTypeSchema
{
    public const int MaxTypes = 20;
    public const int MaxProperties = 20;
    public const int MaxChoices = 30;
    public const int MaxTypeFolders = 10;
    private const int MaxIdLength = 40;
    private const int MaxKeyLength = 64;
    private const int MaxMatchLength = 100;
    private const int MaxTitleLength = 80;
    private const int MaxValueLength = 200;

    // Чем можно красить значения выбора. Имена РОЛЕЙ дизайн-системы, а не цветов: в токенах
    // проекта нет «зелёного» — есть --c-success, --c-danger и т.д. Написав в файле «green»,
    // мы заставили бы фронт изобретать цвет мимо дизайн-системы, чего она и не позволяет.
    public static readonly IReadOnlyList<string> Colors =
        ["gray", "accent", "success", "warning", "danger", "info", "plan"];

    private const string DefaultColor = "gray";

    // Маски компилируются на каждый вызов проекции, а типов немного — держим готовые
    private static readonly ConcurrentDictionary<string, Regex> MaskCache = new(StringComparer.Ordinal);
    private const int MaxCachedMasks = 200;

    // ---------- чтение из .docs ----------

    // Секция как она лежит в файле. error заполняется только структурной ошибкой (секция не
    // массив) — всё остальное чинится молчаливой нормализацией.
    public static IReadOnlyList<DocTypeDef> Read(JsonElement? section, out string? error)
    {
        error = null;
        if (section is not { } raw || raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];

        if (raw.ValueKind != JsonValueKind.Array)
        {
            error = "Секция docTypes должна быть массивом типов документов";
            return [];
        }

        var result = new List<DocTypeDef>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw.EnumerateArray())
        {
            if (result.Count >= MaxTypes) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            var type = ReadType(item);
            if (type is null || !ids.Add(type.Id)) continue;
            result.Add(type);
        }
        return result;
    }

    private static DocTypeDef? ReadType(JsonElement item)
    {
        var id = NormalizeId(Str(item, "id"));
        if (id is null) return null;

        var folders = new List<string>();
        if (item.TryGetProperty("folders", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
            foreach (var f in foldersEl.EnumerateArray())
                if (f.ValueKind == JsonValueKind.String) folders.Add(f.GetString() ?? "");

        var properties = new List<DocPropertyDef>();
        if (item.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Array)
            foreach (var p in propsEl.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var def = ReadProperty(p);
                if (def is not null) properties.Add(def);
            }

        return Build(id, Str(item, "title"), folders, Str(item, "match"),
            Str(item, "badgeProperty"), properties);
    }

    private static DocPropertyDef? ReadProperty(JsonElement p)
    {
        var kindRaw = Str(p, "kind");
        if (!TryParseKind(kindRaw, out var kind)) return null;

        var choices = new List<DocPropertyChoice>();
        if (p.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
            foreach (var c in choicesEl.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String)
                    choices.Add(new DocPropertyChoice(c.GetString() ?? ""));
                else if (c.ValueKind == JsonValueKind.Object)
                    choices.Add(new DocPropertyChoice(
                        Str(c, "value") ?? "", Str(c, "color") ?? DefaultColor, Str(c, "title")));
            }

        return Build(Str(p, "key"), kind, Str(p, "title"), choices,
            Bool(p, "autoUpdate"), Bool(p, "required"));
    }

    // ---------- нормализация (общая для файла и для запроса фронта) ----------

    public static IReadOnlyList<DocTypeDef> Normalize(IReadOnlyList<DocTypeDef>? raw)
    {
        if (raw is null) return [];
        var result = new List<DocTypeDef>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in raw)
        {
            if (result.Count >= MaxTypes) break;
            if (t is null) continue;

            var id = NormalizeId(t.Id);
            if (id is null || !ids.Add(id)) continue;

            var properties = new List<DocPropertyDef>();
            foreach (var p in t.Properties ?? [])
            {
                if (p is null) continue;
                var def = Build(p.Key, p.Kind, p.Title, p.Choices, p.AutoUpdate, p.Required);
                if (def is not null) properties.Add(def);
            }

            var type = Build(id, t.Title, t.Folders ?? [], t.Match, t.BadgeProperty, properties);
            if (type is not null) result.Add(type);
        }
        return result;
    }

    // Сборка типа с проверками. null — тип непригоден и отбрасывается целиком.
    private static DocTypeDef? Build(string? id, string? title, IReadOnlyList<string> rawFolders,
        string? rawMatch, string? rawBadge, List<DocPropertyDef> properties)
    {
        var normalizedId = NormalizeId(id);
        if (normalizedId is null) return null;

        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawFolders)
        {
            var folder = DocsIndexService.NormalizeFolder(raw);
            if (folder is null || !seen.Add(folder)) continue;
            folders.Add(folder);
            if (folders.Count >= MaxTypeFolders) break;
        }

        // Тип без папок не привязан ни к чему: он либо не совпал бы ни с одним документом,
        // либо (если считать пустой список за «везде») молча накрыл бы весь корпус
        if (folders.Count == 0) return null;

        // Свойств нет — показывать и править нечего, тип бессмыслен
        var deduped = new List<DocPropertyDef>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in properties)
        {
            if (!keys.Add(p.Key)) continue;
            deduped.Add(p);
            if (deduped.Count >= MaxProperties) break;
        }
        if (deduped.Count == 0) return null;

        var badge = Trim(rawBadge, MaxKeyLength);
        if (badge is not null && !deduped.Any(p => p.Key.Equals(badge, StringComparison.OrdinalIgnoreCase)))
            badge = null;
        badge ??= deduped.FirstOrDefault(p => p.Kind == DocPropertyKind.Choice)?.Key;

        return new DocTypeDef(
            normalizedId,
            Trim(title, MaxTitleLength) ?? normalizedId,
            folders,
            NormalizeMask(rawMatch),
            badge,
            deduped);
    }

    // Сборка свойства. null — свойство отбрасывается.
    private static DocPropertyDef? Build(string? rawKey, DocPropertyKind kind, string? title,
        IReadOnlyList<DocPropertyChoice>? rawChoices, bool autoUpdate, bool required)
    {
        var key = Trim(rawKey, MaxKeyLength);
        // «*» и «:» в ключе разорвали бы строку «**Ключ:** значение» при записи, перенос строки —
        // тем более: ключ обязан остаться одной строкой шапки
        if (key is null || key.Contains('*') || key.Contains(':')) return null;

        List<DocPropertyChoice>? choices = null;
        if (kind == DocPropertyKind.Choice)
        {
            choices = [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in rawChoices ?? [])
            {
                if (choices.Count >= MaxChoices) break;
                var value = Trim(c?.Value, MaxValueLength);
                if (value is null || !seen.Add(value)) continue;
                var color = (c?.Color ?? "").Trim().ToLowerInvariant();
                // Незнакомое имя (и уж тем более «#22c55e») — серым: сырой цвет в схему не пускаем
                if (!Colors.Contains(color)) color = DefaultColor;
                choices.Add(new DocPropertyChoice(value, color, Trim(c?.Title, MaxTitleLength)));
            }
            // Выбор без словаря — не выбор: показывать в меню нечего
            if (choices.Count == 0) return null;
        }

        return new DocPropertyDef(key, kind, Trim(title, MaxTitleLength), choices,
            AutoUpdate: autoUpdate && kind == DocPropertyKind.Date,
            Required: required);
    }

    // Неизвестный вид — отбрасываем свойство, а НЕ считаем текстом: молча превратить «выбор»
    // в «текст» значит потерять словарь значений и покрасить плашку нечем
    private static bool TryParseKind(string? raw, out DocPropertyKind kind)
    {
        kind = DocPropertyKind.Text;
        var s = (raw ?? "").Trim();
        if (s.Equals("choice", StringComparison.OrdinalIgnoreCase)) { kind = DocPropertyKind.Choice; return true; }
        if (s.Equals("date", StringComparison.OrdinalIgnoreCase)) { kind = DocPropertyKind.Date; return true; }
        if (s.Equals("text", StringComparison.OrdinalIgnoreCase)) { kind = DocPropertyKind.Text; return true; }
        if (s.Equals("docLink", StringComparison.OrdinalIgnoreCase)) { kind = DocPropertyKind.DocLink; return true; }
        return false;
    }

    private static string? NormalizeId(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        if (s.Length > MaxIdLength) s = s[..MaxIdLength];
        foreach (var ch in s)
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')) return null;
        return s;
    }

    // Маска — только имя файла: «docs/*.md» задавал бы папку второй дорогой мимо folders
    private static string? NormalizeMask(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Length > MaxMatchLength) return null;
        if (s.Contains('/') || s.Contains('\\') || s.Contains(':')) return null;
        return s;
    }

    // ---------- сопоставление документа с типом ----------

    // Тип бывает только у markdown: свойства живут строками «**Ключ:** значение» в шапке,
    // а у .txt такая строка ничего не значит — и ровно то же условие стоит гейтом записи.
    // Без этой проверки .txt из типизированной папки получал бы плашку и редактор, а любое
    // сохранение отвечало бы 400
    public static bool IsTypeable(DocEntry doc) =>
        !doc.Binary && System.IO.Path.GetExtension(doc.Path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    // Тип документа: побеждает самый длинный совпавший префикс папки (вложенный тип сильнее
    // объемлющего), при равенстве — тип с маской сильнее типа без маски, дальше — порядок в файле.
    public static DocTypeDef? Match(string docPath, IReadOnlyList<DocTypeDef> types)
    {
        if (types.Count == 0 || string.IsNullOrEmpty(docPath)) return null;

        var slash = docPath.LastIndexOf('/');
        var folder = slash < 0 ? "" : docPath[..slash];
        var name = slash < 0 ? docPath : docPath[(slash + 1)..];

        DocTypeDef? best = null;
        var bestScore = -1;
        foreach (var type in types)
        {
            var depth = FolderScore(folder, type.Folders);
            if (depth < 0) continue;
            if (type.Match is not null && !MaskOf(type.Match).IsMatch(name)) continue;

            var score = depth * 2 + (type.Match is not null ? 1 : 0);
            if (score <= bestScore) continue;
            best = type;
            bestScore = score;
        }
        return best;
    }

    // Длина совпавшего префикса папки или -1, если документ не в папках типа
    private static int FolderScore(string folder, IReadOnlyList<string> folders)
    {
        var best = -1;
        foreach (var f in folders)
        {
            var hit = folder.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase);
            if (hit && f.Length > best) best = f.Length;
        }
        return best;
    }

    private static Regex MaskOf(string mask)
    {
        if (MaskCache.TryGetValue(mask, out var cached)) return cached;

        var pattern = "^" + Regex.Escape(mask).Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$";
        // NonBacktracking: маска приходит из файла репозитория, а не из кода продукта.
        // IgnoreCase — как и весь остальной разбор корпуса: на Linux иначе «ADR-*.md»
        // не поймал бы «adr-005.md», и тип документа зависел бы от регистра имени файла
        var regex = new Regex(pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

        // Потолок кеша: маски приходят из правимого файла, и без него поток правок схемы
        // складывал бы скомпилированные регексы в статику навсегда. Реальных масок единицы,
        // так что переполнение означает злоупотребление — дальше просто не кешируем
        if (MaskCache.Count < MaxCachedMasks) MaskCache.TryAdd(mask, regex);
        return regex;
    }

    // Проставить тип документам индекса. Делается ВНЕ кеша корпуса: схема живёт в .docs, а
    // отпечаток кеша считается по документам — правка схемы не меняет ни один документ, и
    // тип, проставленный при сборке, показывался бы старым до перезапуска сервера.
    // Свойства оставляем только типизированным: индекс перезапрашивается на каждое изменение
    // файлов, и возить шапки всех документов подряд ради метки у типизированных незачем.
    public static IReadOnlyList<DocEntry> Apply(IReadOnlyList<DocEntry> docs,
        IReadOnlyList<DocTypeDef> types)
    {
        if (types.Count == 0) return [.. docs.Select(d => d.Properties is null ? d : d with { Properties = null })];

        var result = new List<DocEntry>(docs.Count);
        foreach (var doc in docs)
        {
            var type = IsTypeable(doc) ? Match(doc.Path, types) : null;
            result.Add(type is null
                ? doc with { Properties = null, Type = null }
                : doc with { Type = type.Id });
        }
        return result;
    }

    // ---------- запись в .docs ----------

    public static JsonArray ToJson(IReadOnlyList<DocTypeDef> types)
    {
        var array = new JsonArray();
        foreach (var t in types)
        {
            var folders = new JsonArray();
            foreach (var f in t.Folders) folders.Add((JsonNode)JsonValue.Create(f));

            var properties = new JsonArray();
            foreach (var p in t.Properties)
            {
                var prop = new JsonObject
                {
                    ["key"] = p.Key,
                    ["kind"] = KindName(p.Kind),
                };
                if (p.Title is not null) prop["title"] = p.Title;
                if (p.Required) prop["required"] = true;
                if (p.AutoUpdate) prop["autoUpdate"] = true;
                if (p.Choices is { Count: > 0 })
                {
                    var choices = new JsonArray();
                    foreach (var c in p.Choices)
                    {
                        var choice = new JsonObject { ["value"] = c.Value, ["color"] = c.Color };
                        if (c.Title is not null) choice["title"] = c.Title;
                        choices.Add(choice);
                    }
                    prop["choices"] = choices;
                }
                properties.Add(prop);
            }

            var type = new JsonObject
            {
                ["id"] = t.Id,
                ["title"] = t.Title,
                ["folders"] = folders,
            };
            if (t.Match is not null) type["match"] = t.Match;
            if (t.BadgeProperty is not null) type["badgeProperty"] = t.BadgeProperty;
            type["properties"] = properties;
            array.Add(type);
        }
        return array;
    }

    // Имя вида в файле — camelCase, как на проводе
    private static string KindName(DocPropertyKind kind) => kind switch
    {
        DocPropertyKind.Choice => "choice",
        DocPropertyKind.Date => "date",
        DocPropertyKind.DocLink => "docLink",
        _ => "text",
    };

    // ---------- мелочи ----------

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static string? Trim(string? raw, int max)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.Contains('\n') || s.Contains('\r')) return null;
        if (s.Length <= max) return s;

        // Обрезаем по границе руны: разрубленная суррогатная пара (эмодзи ровно на пределе)
        // роняет запись JSON исключением «Cannot transcode invalid UTF-16» — а его в
        // контроллере не ждут, и пользователь получил бы 500 на длинное название
        var cut = max;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut];
    }
}
