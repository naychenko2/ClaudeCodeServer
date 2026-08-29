using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services;

/// <summary>Почему запрос на поддомен не обслужен. Наружу коды не раскрываем — только в лог.</summary>
public enum ExternalPreviewDenial
{
    None = 0,
    /// <summary>Фича выключена рубильником — включая уже открытые сессии.</summary>
    Disabled,
    /// <summary>Включена, но PublicBaseUrl не разобрался: работать не с чем.</summary>
    NotConfigured,
    /// <summary>Токена нет, он испорчен, протух или отозван выходом из аккаунта.</summary>
    BadToken,
    /// <summary>Подпись цела, но ссылки нет в реестре — её отозвали.</summary>
    Revoked,
    /// <summary>Проект исчез или сменил владельца.</summary>
    Forbidden,
    /// <summary>Сервис пропал из конфигурации проекта или лишился порта.</summary>
    ServiceGone,
    /// <summary>Порт есть, но на нём никто не слушает — дев-сервер погашен.</summary>
    NotListening,
}

/// <summary>Куда форвардить запрос поддомена.</summary>
public sealed record ExternalPreviewTarget(string BaseUrl, int Port, ExternalPreviewLink Link);

/// <summary>
/// Решает, обслуживать ли запрос, пришедший на поддомен внешнего доступа, и куда его слать.
///
/// Логика вынесена из middleware намеренно: проверок здесь пять, каждая — про безопасность,
/// и в виде лапши внутри Program.cs они не покрываются юнит-тестами.
/// </summary>
public sealed class ExternalPreviewRouter(
    IOptions<ExternalPreviewOptions> options,
    JwtService jwt,
    ExternalPreviewStore store,
    ProjectManager projects,
    ProjectServiceDiscovery discovery,
    DevServerService devServer,
    DevServerPortMemory portMemory)
{
    /// <summary>Путь обмена токена на куку. Общий для выдающего эндпоинта и middleware.</summary>
    public const string AuthPath = "/__preview-auth";

    /// <summary>Имя куки. Своё, не cc_preview: одинаковые имена на соседних хостах путают при разборе.</summary>
    public const string CookieName = "cc_extpreview";

    /// <summary>
    /// Порт помним на сессию ссылки, а не резолвим на каждый запрос. Причина: DiscoverAsync
    /// кэширует всего 2 секунды и не защищён от параллельных промахов, а SPA стреляет сотней
    /// запросов залпом — вышел бы залп полных обходов файловой системы проекта на каждый пакет
    /// чанков. Владельца при этом проверяем КАЖДЫЙ раз: это дёшево и это про безопасность.
    /// </summary>
    private static readonly TimeSpan PortMemoTtl = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, (DateTime At, int Port)> _portMemo = new();

    public ExternalPreviewOptions Options => options.Value;

    /// <summary>Наш ли это хост. Сравнение без порта — Host.Host его и не содержит.</summary>
    public bool IsOwnHost(string? host) =>
        Options.Host is { } own && string.Equals(host, own, StringComparison.OrdinalIgnoreCase);

    /// <summary>Готовая ссылка для человека.</summary>
    public string BuildLinkUrl(string token) => $"{Options.PublicBaseUrl.TrimEnd('/')}{AuthPath}?t={Uri.EscapeDataString(token)}";

    /// <summary>
    /// Порт сервиса для внешнего доступа. Источника два, и порядок между ними важен:
    ///
    /// 1) фактический порт ЖИВОГО процесса — конфигурация его может не знать вовсе
    ///    (автопорт, порт выловлен из вывода дев-сервера);
    /// 2) порт из конфигурации — для сервисов, поднятых вне продукта: своего процесса
    ///    у них нет, и спросить о порте больше некого;
    /// 3) последний известный порт из памяти — после перезапуска продукта реестр процессов
    ///    пуст, а дев-сервер жив, и это единственный способ узнать, где он слушает.
    ///
    /// У составной конфигурации своего порта нет: идём по участникам и берём первого,
    /// которому есть что показать, — тем же правилом панель считает порт группе.
    /// </summary>
    public async Task<int?> ResolveServicePortAsync(Project project, string serviceId, string userId)
    {
        var known = await discovery.DiscoverAsync(project);
        var svc = known.FirstOrDefault(s => s.Id == serviceId);
        if (svc is null) return null;

        // У составной конфигурации берём ПОСЛЕДНЕГО участника с портом. В multilaunch
        // зависимости поднимаются первыми, а приложение-агрегатор ждёт их (waitPortOpened)
        // и потому стоит в конце: смотреть надо именно его. Правило «первый с портом»
        // показывало вместо витрины её первый модуль-панель.
        var ids = svc.Members is { Length: > 0 } ? svc.Members.Reverse() : [svc.Id];
        var byId = known.ToDictionary(s => s.Id);
        foreach (var id in ids)
        {
            if (devServer.GetRunningPort(project.Id, id, userId) is > 0 and var running) return running;
            if (byId.TryGetValue(id, out var member) && member.SuggestedPort is > 0) return member.SuggestedPort;
            if (portMemory.Get(project.Id, id) is > 0 and var remembered) return remembered;
        }
        return null;
    }

    /// <summary>
    /// Полная проверка запроса на поддомене. Порядок проверок — от самой дешёвой и грубой
    /// к самой дорогой, чтобы отказ не стоил обхода файловой системы.
    /// </summary>
    public async Task<(ExternalPreviewTarget? Target, ExternalPreviewDenial Denial)> ResolveAsync(string? token)
    {
        // Рубильник закрывает и уже открытые сессии — иначе «выключено» не выключало бы доступ
        if (!Options.Enabled) return (null, ExternalPreviewDenial.Disabled);
        if (!Options.IsConfigured) return (null, ExternalPreviewDenial.NotConfigured);

        var claims = jwt.ValidatePreviewToken(token);
        if (claims is null) return (null, ExternalPreviewDenial.BadToken);

        // Отзыв: подпись цела, но записи в реестре больше нет
        var link = store.Get(claims.Value.Jti);
        if (link is null) return (null, ExternalPreviewDenial.Revoked);

        // Владелец — каждый запрос: проект могли удалить или передать после выдачи ссылки
        var project = projects.GetById(link.ProjectId);
        if (project is null || project.OwnerId != link.UserId) return (null, ExternalPreviewDenial.Forbidden);

        var port = await PortForAsync(project, link);
        if (port is null) return (null, ExternalPreviewDenial.ServiceGone);

        var baseUrl = await LoopbackResolver.ResolveBaseAsync(port.Value);
        if (baseUrl is null)
        {
            // Мёртвый порт мог смениться живым по другой семье адресов — не держим выбор
            _portMemo.TryRemove(link.Jti, out _);
            return (null, ExternalPreviewDenial.NotListening);
        }

        return (new ExternalPreviewTarget(baseUrl, port.Value, link), ExternalPreviewDenial.None);
    }

    /// <summary>
    /// Забыть и запомненный порт ссылки, и выбранную для него семью loopback-адресов.
    /// Зовётся, когда форвард не дошёл: смениться мог и сервис в конфигурации, и сам
    /// слушающий процесс — держать протухший выбор до конца TTL незачем.
    /// </summary>
    public void ForgetPort(string jti, int port)
    {
        _portMemo.TryRemove(jti, out _);
        LoopbackResolver.Invalidate(port);
    }

    private async Task<int?> PortForAsync(Project project, ExternalPreviewLink link)
    {
        if (_portMemo.TryGetValue(link.Jti, out var memo) && DateTime.UtcNow - memo.At < PortMemoTtl)
            return memo.Port;

        var port = await ResolveServicePortAsync(project, link.ServiceId, link.UserId);
        if (port is not > 0) return null;
        _portMemo[link.Jti] = (DateTime.UtcNow, port.Value);
        return port;
    }
}
