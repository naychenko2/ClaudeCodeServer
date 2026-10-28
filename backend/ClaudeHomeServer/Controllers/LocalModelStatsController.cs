using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Controllers;

// REST-панель «Локальные модели»: вкладка «Текст» (снимок состояния локального движка vLLM +
// история + видеокарты), вкладка «Медиа» (ComfyUI) и второй стенд (B3c): GET engines +
// GET stats?engine=<имя> — стенд именованного движка с блоком «Дежурство».
// Права (ADR-030, изменение правила приватности 2026-10-18): «Текст» — только admin (настройка
// глобальная, влияет на всех, образец — LocalActionsAdminController); «Медиа» — любой
// аутентифицированный, но не-админ видит только свои строки (scope "self", gpus null, счётчик
// queueAhead «перед вами N»). Роль снята с класса и стоит на каждом действии: в ASP.NET Core
// несколько [Authorize] складываются по И, поэтому [Authorize] на действии поверх class-level
// Roles="admin" не ослабил бы доступ.
//
// Зависимости — только через интерфейсы вертикали Llm (ILocalModelStatsReader, IGpuSampler).
// Подсистема Llm может быть отключена (Subsystems:llm:Enabled=false) — тогда
// зависимости необязательны (GetService → null) и ответ {configured:false, status:notConnected}
// (200, а не 500). Внутренние типы Llm наружу не отдаём: DTO — отдельные записи ниже.
[ApiController]
[Route("api/local-model")]
public class LocalModelStatsController(IServiceProvider services, IOptions<LocalModelStatsMediaOptions> mediaOptions) : ControllerBase
{
    // Llm-зависимости необязательны: подсистема может быть выключена (GetService → null).
    private readonly ILocalModelStatsReader? _reader = services.GetService<ILocalModelStatsReader>();
    private readonly IGpuSampler? _gpus = services.GetService<IGpuSampler>();
    // Вкладка «Железо» (Б1): отчёт с уровнями на бэкенде. Необязателен (подсистема Llm отключаема).
    private readonly IHardwareReportService? _hardware = services.GetService<IHardwareReportService>();
    // Счётчик хода: живые чаты на движке (chats) и прогоны, которые он не видит (othersCount).
    private readonly ILocalModelTurnMeter? _meter = services.GetService<ILocalModelTurnMeter>();
    // Стенд второй модели (Б3c): справочник дорожек статистики, словарь известных движков
    // (список engines и цепочка duty), клиент состояния арбитра и журнал вызовов мест («кто сейчас
    // на модели»). Всё необязательно: подсистема Llm отключаема (GetService → null) — тогда
    // engines отдаёт {main, configured:false}, а stats?engine= — {configured:false} (200, не 500).
    private readonly ILocalEngineStatsDirectory? _tracks = services.GetService<ILocalEngineStatsDirectory>();
    private readonly NamedLocalEngines? _namedEngines = services.GetService<NamedLocalEngines>();
    private readonly IEngineArbiterStateReader? _arbiter = services.GetService<IEngineArbiterStateReader>();
    private readonly EngineCallLedger? _callLedger = services.GetService<EngineCallLedger>();
    // Рубильник «медиа для обычных пользователей» (ADR-030, 2026-10-18): единственная точка
    // чтения секции LocalModelStats (связывание в Program.cs).
    private readonly IOptions<LocalModelStatsMediaOptions> _mediaOptions = mediaOptions;

    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Роль admin: «Текст» — только админ, «Медиа» — любой аутентифицированный.
    private bool IsAdmin => User.IsInRole("admin");

    // GET /api/local-model/stats?window=1m|5m|15m|1h&since=<unix ms>&engine=<ключ>
    // window: по умолчанию 15m; белый список 1m/5m/15m/1h, иное → 400.
    // since: unix-мс, фильтр «только хвост новее этого момента» (без since — всё окно).
    // Вкладка «Текст» — только admin (настройка глобальная, влияет на всех).
    // engine — только ключ словаря движков (URL из него не строится никогда): без engine или с
    // "main" — основной стенд, поведение ручки и ответ побайтно прежние (флаг здесь не проверялся
    // и не появляется — регрессионная guard Б3c); с другим engine — стенд именованного движка
    // (NamedStatsAsync ниже), и для него флаг local-model-stats проверяется на сервере, как у
    // hardware (без него 404).
    [Authorize(Roles = "admin")]
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
        [FromQuery] string? window,
        [FromQuery] long? since,
        [FromQuery] string? engine,
        CancellationToken ct,
        [FromServices] FeatureFlagService flags)
    {
        // Флаг — только для новой формы ручки (с engine): без engine ответ и коды остаются прежними.
        if (!string.IsNullOrEmpty(engine) && (UserId is not { } uid || !flags.IsEnabled(uid, FeatureFlagKeys.LocalModelStats)))
            return NotFound();

        if (!TryParseWindow(window, out var windowSpan))
            return BadRequest(new { error = $"Неизвестное окно «{window}» (допустимо 1m, 5m, 15m, 1h)" });

        if (!string.IsNullOrEmpty(engine) && !string.Equals(engine, LocalModelStatsPoller.MainTrackName, StringComparison.Ordinal))
            return await NamedStatsAsync(engine, windowSpan, since, ct);

        var current = _reader?.Current;

        // Подсистема Llm отключена (reader null) → notConnected, 200 (не 500).
        if (current is null)
            return Ok(new LocalModelStatsResponse
            {
                Configured = false,
                Status = "notConnected",
                Speed = new SpeedDto(),
                Kv = new KvDto(),
                Gpus = null,
                History = [],
                Chats = [],
                Unsupported = [],
            });

        // gpus — из IGpuSampler: ВСЕ карты хоста (ADR-029 п.7: всё, что видит nvidia-smi, — «текст»).
        // Пустой список/ошибка nvidia-smi → null (без блока).
        GpuDto[]? gpus = null;
        if (_gpus is { } sampler)
        {
            var samples = await sampler.ReadAsync(ct);
            if (samples.Count > 0)
                gpus = samples.Select(MapGpu).ToArray();
        }

        // history — из GetHistory(window, since); при since — только хвост новее момента.
        var points = _reader!.GetHistory(windowSpan, since);
        var history = points.Select(p => MapHistoryPoint(current, p)).ToArray();

        // Чаты на стенде: живые чаты счётчика хода (ILocalModelTurnMeter.ActiveChats).
        // Анонимизация: свой чат (OwnerId == текущий пользователь) — название/id/проект
        // отдаются; чужой — «Чат другого пользователя», id/проект null. Сам OwnerId
        // наружу не уходит (в DTO такого поля нет).
        var active = _meter?.ActiveChats() ?? [];
        var chats = active.Select(c =>
        {
            var own = c.OwnerId == UserId;
            return new ChatDto
            {
                Own = own,
                SessionId = own ? c.SessionId : null,
                ProjectId = own ? c.ProjectId : null,
                Title = own ? c.Title : "Чат другого пользователя",
                Phase = c.Phase,
                GenTps = c.GenTps,
            };
        }).ToArray();

        // othersCount — прогоны на стенде, которые счётчик не видит как чаты (он видит
        // только ходы, прошедшие через шлюз): max(0, running + waiting − живых чатов).
        var othersCount = Math.Max(0, (current.Running ?? 0) + (current.Waiting ?? 0) - active.Count);

        return Ok(new LocalModelStatsResponse
        {
            Configured = current.Configured,
            Engine = current.Engine,
            Model = current.Model,
            TensorParallel = current.TensorParallel,
            Status = current.Status,
            DownSince = current.DownSince?.ToUnixTimeMilliseconds(),
            Running = current.Running,
            Waiting = current.Waiting,
            OthersCount = othersCount,
            // Стабильная база шага декода (мс): 10-й перцентиль времени шага по кольцу
            // точек за час, опубликованный с гистерезисом (ADR-029, v2.5). Не зависит от
            // окна запроса. null — данных ещё нет.
            CleanStepMs = current.CleanStepMs,
            Speed = new SpeedDto
            {
                GenPerStream = current.Kv?.GenPerStream,
                PrefillPerStream = current.Kv?.PrefillPerStream,
                SpeculativeSpeedup = current.SpeculativeSpeedup,
            },
            Kv = new KvDto
            {
                UsagePerc = current.Kv?.UsagePerc,
                RamTierPerc = current.Kv?.RamTierPerc,
                PrefixHitPerc = current.Kv?.PrefixHitPerc,
                RamTierHitPerc = current.Kv?.RamTierHitPerc,
                Preemptions15m = current.Kv?.Preemptions,
            },
            Gpus = gpus,
            History = history,
            Chats = chats,
            Unsupported = current.Unsupported,
            // Строка-ссылка «Видеокарты»: ручка уже admin-only, отдельного опроса нет.
            Hardware = _hardware?.Brief(),
        });
    }

    // GET /api/local-model/engines — список движков для переключателя «Основная / Bonsai»
    // (макет §2). ТОЛЬКО admin (роль на действии, как у /stats) плюс серверный флаг
    // local-model-stats (без него 404, как у hardware).
    // Список НИКОГДА не пуст: "main" присутствует всегда, известные из конфига движки добавляются
    // к нему; без дорожки статистики — с configured:false. Пустой список фронт читает как «ещё не
    // загружен» и держит сохранённый выбор вечно (R-F1, задача e4103736), поэтому «ничего не
    // настроено» обязано отдавать {main, configured:false}, а не [].
    // Title: "main" — «Основная»; именованный движок — его ключ с заглавной первой буквой
    // (подпись сегмента по макету; имена моделей в сегмент не ставим).
    [Authorize(Roles = "admin")]
    [HttpGet("engines")]
    public IActionResult Engines([FromServices] FeatureFlagService flags)
    {
        if (UserId is not { } id || !flags.IsEnabled(id, FeatureFlagKeys.LocalModelStats))
            return NotFound();

        var list = new List<EngineEntryDto>
        {
            new() { Name = LocalModelStatsPoller.MainTrackName, Title = "Основная", Configured = _reader?.Current.Configured == true },
        };
        if (_namedEngines is { } known)
            foreach (var e in known.Engines)
            {
                // main однозначен: движок LocalEngines с именем основной дорожки не получает
                // дорожку (правило поллера) и не дублируется в списке.
                if (string.Equals(e.Name, LocalModelStatsPoller.MainTrackName, StringComparison.Ordinal)) continue;
                list.Add(new()
                {
                    Name = e.Name,
                    Title = TitleOf(e.Name),
                    // configured — есть дорожка статистики (её снимок настроен); без MetricsUrl
                    // движок известен, но не настроен: сегмента переключателя у него нет.
                    Configured = _tracks?.CurrentOf(e.Name)?.Configured == true,
                });
            }
        return Ok(list);
    }

    // Подпись сегмента из ключа словаря: заглавная первая буква ("bonsai" → "Bonsai").
    private static string TitleOf(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    // GET /api/local-model/stats?engine=<имя> — стенд именованного движка (B3c): тот же
    // LocalModelStatsResponse плюс блок «Дежурство» (duty). Без engine или с engine=main —
    // основной стенд выше, его ответ побайтно не меняется.
    // Нет дорожки (неизвестный движок, движок без MetricsUrl или выключенная Llm) →
    // {configured:false, status:notConnected} (200, не 500), без блока duty.
    // Отличия от основного стенда: gpus — карта Video, общая с ComfyUI, из Core-шва IComfyStats
    // (узел /ccs/stats), а не сэмплера хоста (разрез v2 §3); Llm ↛ Images — склейка здесь;
    // chats — пусты (именованный движок обслуживает места, не ходы чатов; «кто сейчас на модели»
    // — duty.callsNow); hardware нет (сводка «Видеокарты» относится к хосту, не к машине Video).
    private async Task<IActionResult> NamedStatsAsync(string name, TimeSpan windowSpan, long? since, CancellationToken ct)
    {
        var current = _tracks?.CurrentOf(name);
        if (current is null)
            return Ok(new LocalModelStatsResponse
            {
                Configured = false,
                Status = "notConnected",
                Speed = new SpeedDto(),
                Kv = new KvDto(),
                Gpus = null,
                History = [],
                Chats = [],
                Unsupported = [],
            });

        // Карта Video — общая с ComfyUI: только из снимка узла IComfyStats. Images выключена
        // (GetService → null) или узел молчит (пустой список) → null («нет данных о карте»).
        var comfy = HttpContext.RequestServices.GetService<IComfyStats>();
        var media = comfy is null ? null : await comfy.GetMediaAsync(ct);
        GpuDto[]? gpus = null;
        if (media is not null && media.Gpus.Count > 0)
            gpus = [.. media.Gpus.Select(MapComfyGpu)];

        var history = _tracks!.HistoryOf(name, windowSpan, since).Select(p => MapHistoryPoint(current, p)).ToArray();

        return Ok(new LocalModelStatsResponse
        {
            Configured = current.Configured,
            Engine = current.Engine,
            Model = current.Model,
            TensorParallel = current.TensorParallel,
            // Статус дорожки, включая "yielded" (Б3b): выгруженная под арбитром модель — не авария,
            // DownSince при этом не ставится (снимок дорожки уже несёт null).
            Status = current.Status,
            DownSince = current.DownSince?.ToUnixTimeMilliseconds(),
            Running = current.Running,
            Waiting = current.Waiting,
            // Счётчик хода не видит вызовов именованного движка (они идут мимо шлюз'а):
            // «прочие» — всё, что сообщает движок.
            OthersCount = Math.Max(0, (current.Running ?? 0) + (current.Waiting ?? 0)),
            CleanStepMs = current.CleanStepMs,
            Speed = new SpeedDto
            {
                GenPerStream = current.Kv?.GenPerStream,
                PrefillPerStream = current.Kv?.PrefillPerStream,
                SpeculativeSpeedup = current.SpeculativeSpeedup,
            },
            Kv = new KvDto
            {
                UsagePerc = current.Kv?.UsagePerc,
                RamTierPerc = current.Kv?.RamTierPerc,
                PrefixHitPerc = current.Kv?.PrefixHitPerc,
                RamTierHitPerc = current.Kv?.RamTierHitPerc,
                Preemptions15m = current.Kv?.Preemptions,
            },
            Gpus = gpus,
            History = history,
            Chats = [],
            Unsupported = current.Unsupported,
            Hardware = null,
            Duty = await BuildDutyAsync(name, comfy, ct),
        });
    }

    // Блок «Дежурство» (разрез v2 §5): клиент арбитра (состояние, причины, аренды) + журнал
    // вызовов мест (ответы за час, «кто сейчас») + типичная цепочка движка. Все источники
    // null-безопасны: без клиента или без StateUrl — arbiter:null («арбитр не подключён»).
    private async Task<DutyDto> BuildDutyAsync(string name, IComfyStats? comfy, CancellationToken ct)
    {
        var snap = _arbiter is null ? null : await _arbiter.GetAsync(name, ct);
        return new DutyDto
        {
            Arbiter = MapArbiter(snap, comfy),
            Chain = [.. ChainFor(name)],
            AnswersLastHour = _callLedger is null ? null : MapAnswers(_callLedger.AnswersLastHour(name)),
            CallsNow = _callLedger is null ? null : [.. _callLedger.CallsNow(name).Select(MapCallNow)],
        };
    }

    // Типичная цепочка движка (не гарантированный путь, разрез v1 §2): собственное звено →
    // страховка (если у движка задан FallbackRoute) → облако. Имя "bonsai" общее для всех
    // LocalEngines:* — фронт не знает имени движка (контракт LocalModelChainLink).
    private List<string> ChainFor(string name)
    {
        var chain = new List<string> { "bonsai" };
        if (!string.IsNullOrWhiteSpace(_namedEngines?.Get(name)?.Spec.FallbackRoute))
            chain.Add("insurance");
        chain.Add("claude");
        return chain;
    }

    // Снимок арбитра → DTO. Часы уже пересчитаны клиентом на нашу ось (B3a); id аренд наружу
    // не отдаются (их создал аноним): только текст и остаток.
    private static ArbiterDto? MapArbiter(EngineArbiterSnapshot? s, IComfyStats? comfy) => s is null
        ? null
        : new ArbiterDto
        {
            Reachable = s.Reachable,
            LastSeenAt = s.SeenAt?.ToUnixTimeMilliseconds(),
            State = s.Bonsai,
            Mode = s.Mode,
            QuietLeftS = s.QuietLeftS,
            SinceAt = s.SinceAt?.ToUnixTimeMilliseconds(),
            Yields = s.Yields,
            Returns = s.Returns,
            LastYieldS = s.LastYieldS,
            LastReturnS = s.LastReturnS,
            Reasons = s.Reasons is null ? null : [.. s.Reasons.Select(r => MapReason(r, comfy))],
            Leases = s.Leases is null ? null : [.. s.Leases.Select(l => new LeaseDto { Reason = l.Reason ?? "", LeftS = l.LeftS })],
        };

    // Строка причины «вид:<текст>» → kind + raw без префикса вида: «comfy» — префикс prompt_id,
    // «lease» — текст анонима (обрезан клиентом до 64), «gpu» — «pid:командная строка»; строка
    // без префикса — неизвестный вид: kind пустой, raw целиком (фронт показывает её как текст).
    // Вид задания «comfy» — по шву IComfyStats (таблица соответствия одна, на бэкенде);
    // known=true только когда prompt_id найден в реестре ЭТОГО инстанса; шов не различает
    // «нет в реестре» и «неоднозначно» — неизвестное остаётся null.
    private static ReasonDto MapReason(string s, IComfyStats? comfy)
    {
        var i = s.IndexOf(':');
        var kind = i < 0 ? "" : s[..i];
        var raw = i < 0 ? s : s[(i + 1)..];
        string? jobKind = null;
        bool? known = null;
        if (kind == "comfy")
        {
            jobKind = comfy?.DescribePromptKind(raw);
            // true — найден в реестре этого инстанса; null — неизвестно (шва нет, нет в реестре
            // или неоднозначно — шов их не различает). false здесь не ставим: «нет в реестре»
            // не равно «не с этого сервера».
            known = jobKind is not null ? true : null;
        }
        return new ReasonDto { Kind = kind, Raw = raw, JobKind = jobKind, Known = known };
    }

    private static AnswersDto MapAnswers(EngineAnswerCounts a) => new()
    {
        Bonsai = a.Bonsai,
        Insurance = a.Insurance,
        Claude = a.Claude,
        Missed = a.Missed,
        SinceAt = a.SinceAt.ToUnixTimeMilliseconds(),
    };

    // Открытый вызов места → строка «кто сейчас»: название места, текущее звено (null — «ждёт
    // в очереди»: вызов уже в журнале, но в цепочку ещё не вошёл — контракт фронта Ф0, Б5 заводит
    // записи без звена до первого Step), длительность с начала вызова. Ни OwnerId, ни промпта.
    private static CallNowDto MapCallNow(EngineCallNow c) => new()
    {
        Place = c.Place,
        Link = c.Link switch
        {
            EngineCallLink.Bonsai => "bonsai",
            EngineCallLink.Insurance => "insurance",
            EngineCallLink.Claude => "claude",
            _ => null,
        },
        DurationS = c.Duration.TotalSeconds,
    };

    // GET /api/local-model/hardware?window=1m|5m|15m|1h
    // Вкладка «Железо»: температуры карт (в т.ч. памяти GDDR6X), процессора, платы, дисков + ряды
    // истории за окно. ТОЛЬКО admin (роль на действии, как у /stats; не-админ → 403) плюс серверный
    // флаг local-model-stats (без него 404, как в MediaHistory). Неизвестное window → 400.
    // Подсистема Llm выключена → 200 пустой отчёт (не 500).
    [Authorize(Roles = "admin")]
    [HttpGet("hardware")]
    public IActionResult Hardware([FromQuery] string? window, [FromServices] FeatureFlagService flags)
    {
        if (UserId is not { } id || !flags.IsEnabled(id, FeatureFlagKeys.LocalModelStats))
            return NotFound();
        if (!TryParseWindow(window, out var windowSpan))
            return BadRequest(new { error = $"Неизвестное окно «{window}» (допустимо 1m, 5m, 15m, 1h)" });

        if (_hardware is null)
            return Ok(new HardwareReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "ok", null, "ok",
                [], [], [], [], (long)HardwareReportService.StepFor(windowSpan).TotalMilliseconds));
        return Ok(_hardware.Build(windowSpan));
    }

    // GET /api/local-model/stats/media?since=<unix ms>
    // Вкладка «Медиа»: статус ComfyUI + слоты + running/queue/lastHour.
    // Админ (scope "all") — всё, как раньше; не-админ (scope "self") — только свои строки
    // (OwnerId == sub, не Foreign), gpus null, счётчик queueAhead «перед вами N».
    // Рубильник LocalModelStats:MediaForUsers=false → не-админ получает 404 (как будто функции нет).
    // IComfyStats — ОПЦИОНАЛЬНАЯ зависимость: подсистема images отключаема, а обязательный
    // параметр от неё вне её самой — дефект по ADR-014. Без неё → 200 {configured:false},
    // а не 500. since — unix-мс, фильтрует lastHour (только завершённые новее момента).
    [Authorize]
    [HttpGet("stats/media")]
    public async Task<IActionResult> Media([FromQuery] long? since, CancellationToken ct)
    {
        var comfy = HttpContext.RequestServices.GetService<IComfyStats>();

        // Подсистема images выключена (IComfyStats null) или снимок null → {configured:false}, 200.
        var snap = comfy is null ? null : await comfy.GetMediaAsync(ct);
        if (snap is null)
            return Ok(new LocalModelMediaResponse { Configured = false, Scope = IsAdmin ? "all" : "self" });

        var myId = UserId;
        var isAdmin = IsAdmin;
        // Рубильник: не-админ без MediaForUsers → 404, как будто функции нет (админ не меняется).
        // fail-closed (ADR-030): не-админ без непустого sub → 404 тоже (та же общая проверка,
        // что и в /history*, через MediaSelfScope).
        if (!isAdmin && (!_mediaOptions.Value.MediaForUsers || !MediaSelfScope.HasOwner(User)))
            return NotFound();

        // fail-closed (ADR-030): не-админ с пустым/пробельным myId — своих строк нет: чужие
        // (Foreign) несут OwnerId "", и совпали бы с пустым myId. Строже gate (IsNullOrEmpty):
        // whitespace myId (" ") проходит gate, но здесь даёт пустые running/queue/lastHour.
        // queueAhead остаётся null (своих задач в очереди нет), gpus null (не-админ).
        if (!isAdmin && string.IsNullOrWhiteSpace(myId))
        {
            return Ok(new LocalModelMediaResponse
            {
                Configured = snap.Configured,
                Scope = "self",
                Comfy = new MediaComfyDto
                {
                    Status = snap.Status,
                    DownSince = snap.DownSince?.ToUnixTimeMilliseconds(),
                    Version = snap.Version,
                },
                Slots = new MediaSlotsDto { Used = snap.SlotsUsed, Limit = snap.SlotsLimit },
                Gpus = null,
                LoadedModels = snap.LoadedModels.Count > 0 ? [.. snap.LoadedModels] : null,
                Running = [],
                Queue = [],
                LastHour = [],
                Unsupported = [.. snap.Unsupported],
            });
        }

        // queueAhead — по ПОЛНОЙ очереди ДО фильтра: сколько задач стоит перед первой своей
        // поставленной в очередь задачей; нет своих в очереди → null.
        int? queueAhead = null;
        var myQueuePositions = snap.Queue
            .Select((r, i) => (row: r, position: i + 1))
            .Where(x => x.row.OwnerId == myId && x.row.Foreign is null)
            .Select(x => x.position)
            .ToList();
        if (myQueuePositions.Count > 0)
            queueAhead = myQueuePositions.Min() - 1;

        // running: админ — все; не-админ — только свои, не Foreign.
        var running = snap.Running
            .Where(r => isAdmin || (r.OwnerId == myId && r.Foreign is null))
            .Select(r => MapRunning(r, myId))
            .ToArray();

        // queue: позиция — в ПОЛНОЙ очереди (не в отфильтрованном списке); не-админ — только свои.
        var queue = snap.Queue
            .Select((r, i) => (row: r, position: i + 1))
            .Where(x => isAdmin || (x.row.OwnerId == myId && x.row.Foreign is null))
            .Select(x => MapQueue(x.row, myId, x.position))
            .ToArray();

        // since — только завершённые новее момента (аналог хвоста в «Текст»);
        // не-админ — только свои (ComfyDoneRow без Foreign: чужие имеют OwnerId "").
        var lastHour = snap.LastHour
            .Where(d => since is null || d.FinishedAt.ToUnixTimeMilliseconds() >= since)
            .Where(d => isAdmin || d.OwnerId == myId)
            .Select(d => MapDone(d, myId))
            .ToArray();

        // gpus — карты узла ComfyUI (маршрут /ccs/stats, шаг 31): для не-админа скрыты (null);
        // подпись «RTX 3090 · медиа» делает фронт. Пустой список → null (узел не сообщает).
        MediaGpuDto[]? gpus = null;
        if (isAdmin && snap.Gpus.Count > 0)
        {
            gpus = snap.Gpus.Select(g => new MediaGpuDto
            {
                Uuid = g.Uuid,
                Index = g.Index,
                Name = g.Name,
                Roles = ["media"],
                UtilPerc = g.UtilPerc,
                MemUsedMb = (long?)g.MemUsedMb,
                MemTotalMb = (long?)g.MemTotalMb,
                TempC = g.TempC,
                PowerW = g.PowerW,
                PowerLimitW = g.PowerLimitW,
            }).ToArray();
        }

        return Ok(new LocalModelMediaResponse
        {
            Configured = snap.Configured,
            Scope = isAdmin ? "all" : "self",
            QueueAhead = queueAhead,
            Comfy = new MediaComfyDto
            {
                Status = snap.Status,
                DownSince = snap.DownSince?.ToUnixTimeMilliseconds(),
                Version = snap.Version,
            },
            Slots = new MediaSlotsDto { Used = snap.SlotsUsed, Limit = snap.SlotsLimit },
            Gpus = gpus,
            // «Загружено сейчас»: имена моделей с узла; null — узел не сообщает (маршрут не установлен)
            LoadedModels = snap.LoadedModels.Count > 0 ? [.. snap.LoadedModels] : null,
            Running = running,
            Queue = queue,
            LastHour = lastHour,
            Unsupported = [.. snap.Unsupported],
            // Только админ (scope "all"): у не-админа поля нет.
            Hardware = isAdmin ? _hardware?.Brief() : null,
        });
    }

    // Прогон (идущий) → DTO. Own — прогон текущего пользователя: sessionId/projectId отдаются,
    // title null (в строке нет названия чата — фронт резолвит по sessionId); чужой —
    // «Чат другого пользователя», id/проект null. OwnerId наружу не уходит.
    // База шага для точки: своя для уровня параллелизма точки, если бэкенд её набрал, иначе общая.
    internal static double? CleanStepFor(LocalModelStatsCurrent current, int? running) =>
        running is { } r && current.CleanStepMsByRunning is { } by && by.TryGetValue(r, out var v) ? v : current.CleanStepMs;

    // Точка кольца → DTO — общая для обоих стендов (main и именованный): t — unix-мс (long),
    // серии кэша и скорости — приращения с прошлой точки (фронт сводит их в интервалы).
    private static HistoryPointDto MapHistoryPoint(LocalModelStatsCurrent current, LocalModelStatsPoint p) => new()
    {
        T = p.Timestamp.ToUnixTimeMilliseconds(),
        GenThroughput = p.GenThroughput,
        Waiting = p.Waiting,
        Running = p.Running,
        KvUsage = p.KvUsage,
        PrefixHits = p.PrefixHits,
        PrefixQueries = p.PrefixQueries,
        RamHits = p.RamHits,
        RamQueries = p.RamQueries,
        Preemptions = p.Preemptions,
        GenTokens = p.GenTokens,
        ItlSeconds = p.ItlSeconds,
        ItlCount = p.ItlCount,
        // База шага декода (мс) для этой точки: по числу потоков в ней (батч-движок:
        // шаг растёт с числом строк, 22.10), иначе общая база стенда (ADR-029, v2.5).
        CleanStepMs = CleanStepFor(current, p.Running),
        PrefillSeconds = p.PrefillSeconds,
        QueueSeconds = p.QueueSeconds,
        PromptTokens = p.PromptTokens,
    };

    private static MediaRunningDto MapRunning(ComfyJobRow r, string? myId)
    {
        var own = r.OwnerId == myId;
        return new MediaRunningDto
        {
            Kind = r.Kind,
            Op = r.Op,
            Heavy = r.Heavy,
            Own = own,
            SessionId = own ? r.SessionId : null,
            ProjectId = own ? r.ProjectId : null,
            Title = own ? null : "Чат другого пользователя",
            Foreign = r.Foreign,
            Steps = r.Steps is { } s ? new MediaStepsDto { Step = s.Step, Total = s.Total, Stage = s.Stage } : null,
            Percent = r.Percent,
            PercentExact = r.PercentExact,
            EtaSeconds = r.EtaSeconds,
            RemainingSeconds = r.RemainingSeconds,
            StartedAt = r.StartedAt?.ToUnixTimeMilliseconds(),
        };
    }

    // Прогон в очереди → DTO. Position — 1-based позиция в очереди. Анонимизация как в MapRunning.
    private static MediaQueueDto MapQueue(ComfyJobRow r, string? myId, int position)
    {
        var own = r.OwnerId == myId;
        return new MediaQueueDto
        {
            Position = position,
            Kind = r.Kind,
            Op = r.Op,
            Heavy = r.Heavy,
            Own = own,
            SessionId = own ? r.SessionId : null,
            ProjectId = own ? r.ProjectId : null,
            Title = own ? null : "Чат другого пользователя",
            Foreign = r.Foreign,
            EtaSeconds = r.EtaSeconds,
            StartInSeconds = r.StartInSeconds,
        };
    }

    // Завершённый за последний час прогон → DTO. Own — прогон текущего пользователя.
    private static MediaDoneDto MapDone(ComfyDoneRow d, string? myId) => new()
    {
        Kind = d.Kind,
        Op = d.Op,
        Own = d.OwnerId == myId,
        Outcome = d.Outcome,
        RunSeconds = d.RunSeconds,
        EtaSeconds = d.EtaSeconds,
        FinishedAt = d.FinishedAt.ToUnixTimeMilliseconds(),
    };

    // GpuSample (Llm) → GpuDto (wire): не отдаём внутренний тип Llm наружу.
    private static GpuDto MapGpu(GpuSample s) => new()
    {
        Index = s.Index,
        Name = s.Name,
        UtilPerc = s.Utilization,
        MemUsedMb = s.MemoryUsed,
        MemTotalMb = s.MemoryTotal,
        TempC = s.Temperature,
        PowerW = s.PowerDraw,
        PowerLimitW = s.PowerLimit,
    };

    // Карта узла ComfyUI → GpuDto (та же форма, что у карт хоста): карта Video общая с ComfyUI
    // (разрез v2 §3); подпись «общая с ComfyUI» делает фронт.
    private static GpuDto MapComfyGpu(ComfyGpuInfo g) => new()
    {
        Index = g.Index,
        Name = g.Name,
        UtilPerc = g.UtilPerc,
        MemUsedMb = (long?)g.MemUsedMb,
        MemTotalMb = (long?)g.MemTotalMb,
        TempC = g.TempC,
        PowerW = g.PowerW,
        PowerLimitW = g.PowerLimitW,
    };

    // window: null/пусто → 15m (дефолт); белый список 1m/5m/15m/1h (ровно как в сообщении 400);
    // иное → false (400).
    private static bool TryParseWindow(string? raw, out TimeSpan window)
    {
        window = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw))
        {
            window = TimeSpan.FromMinutes(15);
            return true;
        }
        switch (raw.Trim())
        {
            case "1m":
                window = TimeSpan.FromMinutes(1);
                return true;
            case "5m":
                window = TimeSpan.FromMinutes(5);
                return true;
            case "15m":
                window = TimeSpan.FromMinutes(15);
                return true;
            case "1h":
                window = TimeSpan.FromHours(1);
                return true;
            default:
                return false;
        }
    }

    // ─── DTO ответа (camelCase на wire через дефолт ASP.NET Core) ─────────────────────

    // Корневой снимок панели «Текст».
    public sealed record LocalModelStatsResponse
    {
        // true — локальный провайдер с секцией Stats найден; false — notConnected.
        public bool Configured { get; init; }

        // Тип движка ("vllm" | "llama-server" | "ollama"); null — не задан.
        public string? Engine { get; init; }

        // Идентификатор модели стенда; null — неизвестен.
        public string? Model { get; init; }

        // Число карт тензор-параллелизма из секции Stats; null — не задано (фронт покажет число карт).
        public int? TensorParallel { get; init; }

        // working | busy | down | notConnected.
        public string Status { get; init; } = "notConnected";

        // Момент ПЕРВОГО сбоя (unix-мс, long); null — движок не мёртв.
        public long? DownSince { get; init; }

        public int? Running { get; init; }
        public int? Waiting { get; init; }

        // Прогоны на стенде, не видные как чаты счётчика хода; null — движок не подключён.
        public int? OthersCount { get; init; }

        public SpeedDto Speed { get; init; } = new();

        // Стабильная база шага декода (мс): 10-й перцентиль времени шага (ΔitlSeconds/ΔitlCount)
        // по кольцу точек за час, опубликованный с гистерезисом (ADR-029, v2.5). Одно число на
        // все окна (1 мин / 5 мин / 15 мин / 1 ч) — база не выбирается из окна запроса.
        // null — данных ещё нет (клиент показывает долю «генерирует» без разделения).
        public double? CleanStepMs { get; init; }

        public KvDto Kv { get; init; } = new();

        // Видеокарты; null — нет nvidia-smi (блок не показывать).
        public GpuDto[]? Gpus { get; init; }

        // Точки кольца: t, genThroughput, waiting, running.
        public HistoryPointDto[] History { get; init; } = [];

        // Чаты на стенде; пока пусто (шаг 19).
        public ChatDto[] Chats { get; init; } = [];

        // Метрики, которых движок не сообщил (отсутствуют в /metrics). Единый словарь сокрытия
        // блоков — ИМЕНА ПОЛЕЙ снимка EngineSnapshot (KvUsagePerc, RamTierPerc, Preemptions,
        // QueueTimeSum, …): по ним фронт прячет блоки «Кэш», «Вытеснения», «Куда уходит время
        // агентов» целиком. Для именованных движков (llama-server) источник отдаёт ровно эти
        // имена (LlamaServerMetricsSource.UnsupportedBlocks). Основной стенд (vLLM) сохраняет
        // имена метрик движка: существующее поведение, его ответ побайтно не менялся (regression
        // guard Б3c); по этим именам фронт ничего не скрывает (хвост, зафиксированный ревью Б2a).
        public IReadOnlyList<string> Unsupported { get; init; } = [];

        // Сводка «Железо» для строки-ссылки и точки на вкладке; только админ (у не-админа поля нет).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HardwareBrief? Hardware { get; init; }

        // Блок «Дежурство» — только у стенда именованного движка (B3c). У основного стенда поля
        // нет вовсе (ответ побайтно прежний): WhenWritingNull, как у Hardware.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DutyDto? Duty { get; init; }
    }

    // Скорость на поток: ток/с, Δсчётчика/Δсуммы времени. GenPerStream/PrefillPerStream —
    // за окно ~60 с (как чувствует отдельный чат). Среднее/пик за окно графика — у графика,
    // не в DTO (дубль убран: пик был из чужой величины — genThroughput суммарно, а среднее — на поток).
    public sealed record SpeedDto
    {
        public double? GenPerStream { get; init; }
        public double? PrefillPerStream { get; init; }
        // «Ускорение угадыванием» (ADR-029, v2.3): Δgeneration_tokens/Δinter_token_latency_count за окно
        // ~60 с — во сколько раз спекулятивный декод ускоряет генерацию против выдачи по одному токену.
        public double? SpeculativeSpeedup { get; init; }
    }

    // Блок KV-кэша: доли 0..1 и вытеснения за 15 мин.
    public sealed record KvDto
    {
        public double? UsagePerc { get; init; }
        public double? RamTierPerc { get; init; }
        public double? PrefixHitPerc { get; init; }
        public double? RamTierHitPerc { get; init; }
        public long? Preemptions15m { get; init; }
    }

    // Одна видеокарта хоста (nvidia-smi); числовые поля nullable — [N/A] → null.
    public sealed record GpuDto
    {
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public int? UtilPerc { get; init; }
        public long? MemUsedMb { get; init; }
        public long? MemTotalMb { get; init; }
        public int? TempC { get; init; }
        public double? PowerW { get; init; }
        public double? PowerLimitW { get; init; }
    }

    // Точка кольца истории. T — unix-мс (long): фронт ждёт число (api.ts: t: number).
    // Серии кэша (v2.1): kvUsage — доля 0..1 (максимум по опросам с прошлой точки);
    // prefixHits/prefixQueries/ramHits/ramQueries/preemptions — приращения счётчиков
    // с прошлой точки (фронт сводит в интервалы и считает долю из сумм). null — нет данных.
    // Скорость на поток (вторая линия графика): genTokens/itlSeconds — приращения счётчиков
    // с прошлой точки; фронт сводит в интервалы и делит ΣgenTokens/ΣitlSeconds (ток/с).
    // itlCount — приращение числа шагов декодирования: фронт считает из него время шага,
    // токенов за шаг и агентов в генерации (график «Шаг модели»).
    public sealed record HistoryPointDto
    {
        public long T { get; init; }
        public double? GenThroughput { get; init; }
        public int? Waiting { get; init; }
        public int? Running { get; init; }
        public double? KvUsage { get; init; }
        public long? PrefixHits { get; init; }
        public long? PrefixQueries { get; init; }
        public long? RamHits { get; init; }
        public long? RamQueries { get; init; }
        public long? Preemptions { get; init; }
        public long? GenTokens { get; init; }
        public double? ItlSeconds { get; init; }
        public long? ItlCount { get; init; }
        // База шага декода (мс) — глобальная величина, одинаковая во всех точках
        // (ADR-029, v2.5): клиенту не нужно выбирать базу из интервалов своего окна.
        public double? CleanStepMs { get; init; }
        // Полоса «Куда уходит время агентов» (ADR-029, v2.3): prefill/очередь (сек) и
        // прочитанный промпт (токены) — приращения с прошлой точки; null — нет данных/рестарт.
        public double? PrefillSeconds { get; init; }
        public double? QueueSeconds { get; init; }
        public long? PromptTokens { get; init; }
    }

    // Чат на стенде: живой ход на локальном движке. Own — чат текущего пользователя
    // (id/проект/название отдаются); чужой — только «Чат другого пользователя» (id/проект
    // null, ownerId наружу не уходит). Phase — required string (фронт: phase: string);
    // без данных → "unknown".
    public sealed record ChatDto
    {
        public bool Own { get; init; }
        public string? SessionId { get; init; }
        public string? ProjectId { get; init; }
        public string? Title { get; init; }
        public string Phase { get; init; } = "unknown";
        public double? GenTps { get; init; }
    }

    // ─── DTO ответа «Медиа» (вкладка «Медиа», GET /api/local-model/stats/media) ──────

    // Корневой снимок панели «Медиа»: статус ComfyUI + слоты + running/queue/lastHour.
    // Gpus/LoadedModels — данные узла ComfyUI (маршрут /ccs/stats, шаг 31), не хоста.
    public sealed record LocalModelMediaResponse
    {
        // true — подсистема images/ComfyUI настроена; false — стенд выключен (status:disabled).
        public bool Configured { get; init; }

        // Охват (ADR-030, 2026-10-18): "all" — админ (все строки), "self" — не-админ (только свои).
        public string Scope { get; init; } = "all";

        // «Перед вами N»: сколько задач в ПОЛНОЙ очереди стоит перед первой своей поставленной
        // в очередь задачей (позиция — до фильтра); нет своих в очереди → null.
        public int? QueueAhead { get; init; }

        public MediaComfyDto Comfy { get; init; } = new();

        public MediaSlotsDto Slots { get; init; } = new();

        // Видеокарта медиа (узел ComfyUI, роль media); null — узел не сообщает (маршрут не установлен).
        public MediaGpuDto[]? Gpus { get; init; }

        // «Загружено сейчас»: имена моделей в памяти узла; null — узел не сообщает.
        public string[]? LoadedModels { get; init; }

        public MediaRunningDto[] Running { get; init; } = [];

        public MediaQueueDto[] Queue { get; init; } = [];

        public MediaDoneDto[] LastHour { get; init; } = [];

        public IReadOnlyList<string> Unsupported { get; init; } = [];

        // Сводка «Железо»: только админ (scope "all"); у не-админа поля нет в ответе.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HardwareBrief? Hardware { get; init; }
    }

    // Блок ComfyUI: статус (working|busy|down|disabled), момент первого сбоя, версия.
    public sealed record MediaComfyDto
    {
        public string Status { get; init; } = "disabled";
        public long? DownSince { get; init; }
        public string? Version { get; init; }
    }

    // Слоты очереди ComfyUI: занято/лимит («N из 4 мест»).
    public sealed record MediaSlotsDto
    {
        public int Used { get; init; }
        public int Limit { get; init; }
    }

    // Видеокарта медиа (ComfyUI): uuid, индекс, имя, роли, утилизация, память, температура, мощность.
    public sealed record MediaGpuDto
    {
        public string Uuid { get; init; } = "";
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public string[] Roles { get; init; } = [];
        public int? UtilPerc { get; init; }
        public long? MemUsedMb { get; init; }
        public long? MemTotalMb { get; init; }
        public int? TempC { get; init; }
        public double? PowerW { get; init; }
        public double? PowerLimitW { get; init; }
        public long? MediaMemMb { get; init; }
    }

    // Прогресс семплера: текущий шаг, всего шагов, текущая стадия.
    public sealed record MediaStepsDto
    {
        public int Step { get; init; }
        public int Total { get; init; }
        public int? Stage { get; init; }
    }

    // Идущий прогон ComfyUI. Own — прогон текущего пользователя (sessionId/projectId отдаются,
    // title null — фронт резолвит по sessionId); чужой — «Чат другого пользователя», id/проект null.
    // OwnerId наружу не уходит. Foreign — чужой источник (instance|transcription|external).
    public sealed record MediaRunningDto
    {
        public string Kind { get; init; } = "";
        public string Op { get; init; } = "";
        public bool Heavy { get; init; }
        public bool Own { get; init; }
        public string? SessionId { get; init; }
        public string? ProjectId { get; init; }
        public string? Title { get; init; }
        public string? Foreign { get; init; }
        public MediaStepsDto? Steps { get; init; }
        public int? Percent { get; init; }
        public bool PercentExact { get; init; }
        public int? EtaSeconds { get; init; }
        public int? RemainingSeconds { get; init; }
        // unix-мс (long) момента начала; null — ещё не стартовал.
        public long? StartedAt { get; init; }
    }

    // Прогон в очереди ComfyUI. Position — 1-based позиция. Анонимизация как в MediaRunningDto.
    public sealed record MediaQueueDto
    {
        public int Position { get; init; }
        public string Kind { get; init; } = "";
        public string Op { get; init; } = "";
        public bool Heavy { get; init; }
        public bool Own { get; init; }
        public string? SessionId { get; init; }
        public string? ProjectId { get; init; }
        public string? Title { get; init; }
        public string? Foreign { get; init; }
        public int? EtaSeconds { get; init; }
        public int? StartInSeconds { get; init; }
    }

    // Завершённый за последний час прогон. Outcome — completed|failed|cancelled.
    public sealed record MediaDoneDto
    {
        public string Kind { get; init; } = "";
        public string Op { get; init; } = "";
        public bool Own { get; init; }
        public string Outcome { get; init; } = "";
        public int? RunSeconds { get; init; }
        public int? EtaSeconds { get; init; }
        // unix-мс (long) момента завершения.
        public long FinishedAt { get; init; }
    }

    // ─── DTO второго стенда (B3c): GET /api/local-model/engines и блок duty ───────────

    // Одна строка списка движков для переключателя «Основная / Bonsai» (макет §2).
    public sealed record EngineEntryDto
    {
        // Ключ словаря: "main" или имя движка LocalEngines. URL из него не строится никогда.
        public string Name { get; init; } = "";

        // Подпись сегмента переключателя («Основная», «Bonsai»); имена моделей не ставятся.
        public string Title { get; init; } = "";

        // false — движок известен, но не настроен (нет дорожки статистики): сегмента у него нет.
        public bool Configured { get; init; }
    }

    // Блок «Дежурство» (только у стенда именованного движка). Контракт фронта — LocalModelDuty.
    public sealed record DutyDto
    {
        // null — у движка нет StateUrl: «Дежурство» показывает «арбитр не подключён».
        public ArbiterDto? Arbiter { get; init; }

        // Типичная цепочка: "bonsai" → "insurance" (если у движка FallbackRoute) → "claude".
        public string[] Chain { get; init; } = [];

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AnswersDto? AnswersLastHour { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CallNowDto[]? CallsNow { get; init; }
    }

    // Ответ арбитра о дежурстве — из недоверенного /state, разобранный клиентом (B3a: потолки,
    // неизвестное → unknown/null, часы пересчитаны на нашу ось). При reachable:false — поля
    // последнего удачного снимка; до первого успеха — null.
    public sealed record ArbiterDto
    {
        public bool Reachable { get; init; }

        // unix-мс последнего удачного ответа арбитра; null — удачного не было.
        public long? LastSeenAt { get; init; }

        // ready | yielded | starting | waiting-vram | unknown (всё неизвестное — unknown).
        public string? State { get; init; }

        // exclusive | shared; всё прочее — null.
        public string? Mode { get; init; }

        // Секунды до возврата модели; null — пока есть причины или модель уже активна.
        public double? QuietLeftS { get; init; }

        // unix-мс начала периода счётчиков на нашей оси (perесчёт по ts арбитра — B3a).
        public long? SinceAt { get; init; }

        public int? Yields { get; init; }
        public int? Returns { get; init; }
        public double? LastYieldS { get; init; }
        public double? LastReturnS { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ReasonDto[]? Reasons { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LeaseDto[]? Leases { get; init; }
    }

    // Причина уступки: kind — вид строки ("comfy" | "lease" | "gpu"; без префикса — ""),
    // raw — текст без префикса вида ("gpu" — «pid:командная строка», «comfy» — префикс prompt_id,
    // «lease» — текст причины, обрезанный до 64).
    public sealed record ReasonDto
    {
        public string Kind { get; init; } = "";
        public string Raw { get; init; } = "";

        // Только для "comfy": вид задания image|video|audio|lora (таблица соответствия одна,
        // на бэкенде, разрез v1 §1); null — не опознан.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? JobKind { get; init; }

        // Только для "comfy": true — prompt_id в реестре ЭТОГО инстанса ComfyUI (ComfyUI общий
        // у прода и дева). null — неизвестно (шов не различает «нет» и «неоднозначно»).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Known { get; init; }
    }

    // Активная аренда карты: id наружу не отдаётся (его создал анонимный POST /lease).
    public sealed record LeaseDto
    {
        public string Reason { get; init; } = "";
        public double? LeftS { get; init; }
    }

    // Ответы мест за последний час (журнал EngineCallLedger, разрез v2 §4): корзины — звено,
    // которое ответило; missed — цепочка вернула пусто. Нули — не «нет данных»: нет данных —
    // весь блок отсутствует.
    public sealed record AnswersDto
    {
        public int Bonsai { get; init; }
        public int Insurance { get; init; }
        public int Claude { get; init; }
        public int Missed { get; init; }

        // unix-мс начала окна = max(now − 1 ч, старт процесса): честный знаменатель доли.
        public long SinceAt { get; init; }
    }

    // Вызов места прямо сейчас («кто сейчас на модели» у именованного движка): место —
    // системное действие, без владельца и промпта (строка не кликабельна).
    public sealed record CallNowDto
    {
        // Название места из каталога.
        public string Place { get; init; } = "";

        // Текущее звено: bonsai | insurance | claude. null — «ждёт в очереди»: вызов уже в
        // журнале, но в цепочку ещё не вошёл (проба готовности или слот допуска). Контракт
        // зафиксирован фронтом (Ф0): именно null, не отсутствие поля и не строка.
        public string? Link { get; init; }

        // Секунды с начала вызова.
        public double? DurationS { get; init; }
    }
}
