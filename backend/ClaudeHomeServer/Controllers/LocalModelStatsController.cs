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
// история + видеокарты) и вкладка «Медиа» (ComfyUI).
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
    // Рубильник «медиа для обычных пользователей» (ADR-030, 2026-10-18): единственная точка
    // чтения секции LocalModelStats (связывание в Program.cs).
    private readonly IOptions<LocalModelStatsMediaOptions> _mediaOptions = mediaOptions;

    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Роль admin: «Текст» — только админ, «Медиа» — любой аутентифицированный.
    private bool IsAdmin => User.IsInRole("admin");

    // GET /api/local-model/stats?window=1m|5m|15m|1h&since=<unix ms>
    // window: по умолчанию 15m; белый список 1m/5m/15m/1h, иное → 400.
    // since: unix-мс, фильтр «только хвост новее этого момента» (без since — всё окно).
    // Вкладка «Текст» — только admin (настройка глобальная, влияет на всех).
    [Authorize(Roles = "admin")]
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
        [FromQuery] string? window,
        [FromQuery] long? since,
        CancellationToken ct)
    {
        if (!TryParseWindow(window, out var windowSpan))
            return BadRequest(new { error = $"Неизвестное окно «{window}» (допустимо 1m, 5m, 15m, 1h)" });

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
        var history = points.Select(p => new HistoryPointDto
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
            PrefillSeconds = p.PrefillSeconds,
            QueueSeconds = p.QueueSeconds,
            PromptTokens = p.PromptTokens,
        }).ToArray();

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

        public KvDto Kv { get; init; } = new();

        // Видеокарты; null — нет nvidia-smi (блок не показывать).
        public GpuDto[]? Gpus { get; init; }

        // Точки кольца: t, genThroughput, waiting, running.
        public HistoryPointDto[] History { get; init; } = [];

        // Чаты на стенде; пока пусто (шаг 19).
        public ChatDto[] Chats { get; init; } = [];

        // Метрики, которых движок не сообщил (отсутствуют в /metrics).
        public IReadOnlyList<string> Unsupported { get; init; } = [];

        // Сводка «Железо» для строки-ссылки и точки на вкладке; только админ (у не-админа поля нет).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HardwareBrief? Hardware { get; init; }
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
}
