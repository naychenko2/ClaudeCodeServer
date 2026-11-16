using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Llm;

// Один узел реестра HardwareNodes (ADR-029, «Узлы фермы» → «Реестр HardwareNodes»).
public sealed class HardwareNodeDef
{
    // Идентификатор узла: он же ?node= в API. ^[a-z][a-z0-9-]{0,31}\z, уникален.
    public string Key { get; set; } = "";
    // Подпись в интерфейсе; пусто — показывается Key.
    public string Title { get; set; } = "";
    // "local" — существующие читатели в процессе CCS (не больше одного такого узла),
    // либо базовый URL агента http:// или https:// без пути (бэк дописывает /v1/slice).
    public string Source { get; set; } = "";
    // Имена движков этого узла (из LocalEngines/основного провайдера). Проверка имён — на вызывающем.
    public List<string> Engines { get; set; } = [];
    // true — на узле ComfyUI: карты получают роль media.
    public bool Media { get; set; }
}

// Секция машинного конфига «HardwareNodes» (класс опций рядом с LocalModelStatsOptions;
// регистрация в DI — Ш2c). Форма секции — массив, как в ADR-029:
// "HardwareNodes": [ { "Key": "worker", ... }, ... ].
public sealed class HardwareNodesOptions
{
    public const string Section = "HardwareNodes";

    // Ключ элемента: [a-z] затем [a-z0-9-], всего до 32 символов.
    // Якорь — \z (абсолютный конец строки): $ в .NET совпадает и перед хвостовым \n,
    // и ключ "ai\n" прошёл бы проверку — а он идёт в ?node= и в лог.
    private static readonly Regex KeyPattern = new(@"^[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant);

    public List<HardwareNodeDef> Nodes { get; set; } = [];

    // Читает секцию из конфига: массив элементов (позиции "0", "1", …) или объект с "Nodes".
    // Нечитаемые формы (скаляр, объект без "Nodes") — пустой список: дальше работает фолбэк EffectiveNodes.
    public static HardwareNodesOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var children = section.GetChildren();
        var positional = true;
        foreach (var child in children)
            if (!int.TryParse(child.Key, out _))
            {
                positional = false;
                break;
            }
        var nodes = positional
            ? children.Select(c => c.Get<HardwareNodeDef>() ?? new HardwareNodeDef()).ToList()
            : section.GetSection("Nodes").Get<List<HardwareNodeDef>>() ?? [];
        return new HardwareNodesOptions { Nodes = nodes };
    }

    // Узлы после проверки. Неверный элемент (неверный или дублирующийся Key, второй local,
    // невалидный URL) пропускается — хост не падает (лог ведёт вызывающий, Ш2c).
    // Пустая или не оставшаяся после проверки секция — ровно один узел worker/local:
    // поведение побайтно сегодняшнее. Порядок — как в конфиге.
    public IReadOnlyList<HardwareNodeDef> EffectiveNodes()
    {
        var result = new List<HardwareNodeDef>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var localTaken = false;
        foreach (var node in Nodes)
        {
            if (node is null || !IsValid(node, keys, localTaken))
                continue;
            keys.Add(node.Key);
            if (node.Source == "local")
                localTaken = true;
            result.Add(node);
        }
        if (result.Count == 0)
            result.Add(new HardwareNodeDef { Key = "worker", Title = "worker", Source = "local" });
        return result;
    }

    private static bool IsValid(HardwareNodeDef node, HashSet<string> keys, bool localTaken) =>
        !string.IsNullOrEmpty(node.Key)
        && KeyPattern.IsMatch(node.Key)
        && !keys.Contains(node.Key)
        && (node.Source == "local" ? !localTaken : IsAgentUrl(node.Source));

    // Базовый URL агента: http/https, абсолютный, без пути (бэк сам дописывает /v1/slice).
    private static bool IsAgentUrl(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && (uri.AbsolutePath is "" or "/");
}
