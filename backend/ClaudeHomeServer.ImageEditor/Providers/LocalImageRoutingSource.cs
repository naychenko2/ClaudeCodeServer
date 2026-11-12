using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace ClaudeHomeServer.Services.ImageEditor;

// Таблица «Авто» локальных моделей из конфига: секция LocalMedia:Routing перечитывается на лету (appsettings.Local.json
// грузится с reloadOnChange). Нет секции — таблица из кода (LocalImageRouting.Table); секция невалидна — тоже она,
// с предупреждением в логе: опечатка в конфиге не должна оставить «Авто» без модели. Формат правила:
// { "Id", "Ops": ["Generate"], "Any": ["People", "Photo"], "Forbid": ["Inputs"], "Models": ["krea2-turbo"], "Reason" }
public sealed class LocalImageRoutingSource
{
    public const string SectionName = "LocalMedia:Routing";

    // Операции, у которых в таблице обязано быть общее правило: без него «Авто» на них осталось бы без модели
    private static readonly ImageEditOp[] MustCover = [ImageEditOp.Generate, ImageEditOp.Edit, ImageEditOp.Inpaint, ImageEditOp.Outpaint];

    private readonly IConfiguration? _config;
    private readonly IReadOnlyList<ImageEditModelInfo> _catalog;
    private readonly ILogger? _log;
    private volatile IReadOnlyList<RouteRule> _table = LocalImageRouting.Table;

    public LocalImageRoutingSource(IConfiguration? config, IReadOnlyList<ImageEditModelInfo> catalog, ILogger? log = null)
    {
        _config = config;
        _catalog = catalog;
        _log = log;
        if (config is null) return;
        Reload();
        ChangeToken.OnChange(config.GetReloadToken, Reload);
    }

    // Действующая таблица; FromConfig — взята ли она из секции (иначе — из кода)
    public IReadOnlyList<RouteRule> Table => _table;
    public bool FromConfig => !ReferenceEquals(_table, LocalImageRouting.Table);

    private void Reload()
    {
        var section = _config!.GetSection(SectionName);
        if (!section.GetChildren().Any())
        {
            _table = LocalImageRouting.Table;
            return;
        }
        var (table, error) = Parse(section, _catalog);
        if (table is null)
            _log?.LogWarning("Секция {Section} невалидна, «Авто» локальных моделей идёт по таблице из кода: {Reason}",
                SectionName, error);
        _table = table ?? LocalImageRouting.Table;
    }

    // Таблица или текст первой ошибки
    public static (IReadOnlyList<RouteRule>? Table, string? Error) Parse(IConfigurationSection section,
        IReadOnlyList<ImageEditModelInfo> catalog)
    {
        var rules = new List<RouteRule>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in section.GetChildren())
        {
            var where = $"правило {s.Key}";
            var id = s["Id"]?.Trim();
            if (string.IsNullOrEmpty(id)) return (null, $"{where}: нет Id");
            where = $"правило «{id}»";
            if (!ids.Add(id)) return (null, $"{where}: Id повторяется");

            var ops = new List<ImageEditOp>();
            foreach (var v in Values(s, "Ops"))
            {
                if (!Enum.TryParse<ImageEditOp>(v, ignoreCase: true, out var op) || !Enum.IsDefined(op))
                    return (null, $"{where}: неизвестная операция «{v}»");
                ops.Add(op);
            }
            if (ops.Count == 0) return (null, $"{where}: нет операций (Ops)");

            var (any, anyError) = Traits(s, "Any");
            if (anyError is not null) return (null, $"{where}: {anyError}");
            var (forbid, forbidError) = Traits(s, "Forbid");
            if (forbidError is not null) return (null, $"{where}: {forbidError}");

            var models = Values(s, "Models").ToList();
            if (models.Count == 0) return (null, $"{where}: нет моделей (Models)");
            if (models.FirstOrDefault(m => catalog.All(c => c.Id != m)) is { } unknown)
                return (null, $"{where}: модели «{unknown}» нет в каталоге локальных моделей");

            var reason = s["Reason"]?.Trim();
            if (string.IsNullOrEmpty(reason)) return (null, $"{where}: нет причины (Reason)");
            rules.Add(new RouteRule(id, ops, any, forbid, models, reason));
        }
        if (rules.Count == 0) return (null, "секция пуста");

        foreach (var op in MustCover)
        {
            var covered = rules.Any(r => r.Ops.Contains(op) && r.Any == RouteTrait.None && r.Forbid == RouteTrait.None
                && r.Models.Any(m => catalog.Any(c => c.Id == m && c.Caps.Ops.Contains(op))));
            if (!covered) return (null, $"нет общего правила (без Any и Forbid) для операции {op}");
        }
        return (rules, null);
    }

    // Список строк: массив JSON или одна строка через запятую
    private static IEnumerable<string> Values(IConfigurationSection s, string key)
    {
        var sub = s.GetSection(key);
        var raw = sub.GetChildren().Any() ? sub.GetChildren().Select(c => c.Value) : sub.Value?.Split(',') ?? [];
        return raw.Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!);
    }

    private static (RouteTrait Traits, string? Error) Traits(IConfigurationSection s, string key)
    {
        var r = RouteTrait.None;
        foreach (var v in Values(s, key))
        {
            if (!Enum.TryParse<RouteTrait>(v, ignoreCase: true, out var t) || t == RouteTrait.None
                || !Enum.IsDefined(t) || int.TryParse(v, out _))
                return (r, $"неизвестный признак «{v}» в {key}");
            r |= t;
        }
        return (r, null);
    }
}
