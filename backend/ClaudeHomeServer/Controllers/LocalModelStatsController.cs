using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// REST-панель «Локальные модели», вкладка «Текст»: снимок состояния локального
// движка (vLLM) + история + видеокарты. Только для админов — настройка глобальная
// (влияет на все пользователи), образец — LocalActionsAdminController.
//
// Зависимости — только через интерфейсы вертикали Llm (ILocalModelStatsReader, IGpuSampler).
// Подсистема Llm может быть отключена (Subsystems:llm:Enabled=false) — тогда
// зависимости необязательны (GetService → null) и ответ {configured:false, status:notConnected}
// (200, а не 500). Внутренние типы Llm наружу не отдаём: DTO — отдельные записи ниже.
[ApiController]
[Route("api/local-model")]
[Authorize(Roles = "admin")]
public class LocalModelStatsController(IServiceProvider services) : ControllerBase
{
    // Llm-зависимости необязательны: подсистема может быть выключена (GetService → null).
    private readonly ILocalModelStatsReader? _reader = services.GetService<ILocalModelStatsReader>();
    private readonly IGpuSampler? _gpus = services.GetService<IGpuSampler>();
    // Счётчик хода: живые чаты на движке (chats) и прогоны, которые он не видит (othersCount).
    private readonly ILocalModelTurnMeter? _meter = services.GetService<ILocalModelTurnMeter>();

    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // GET /api/local-model/stats?window=15m|1h&since=<unix ms>
    // window: по умолчанию 15m; неизвестное значение → 400. since: unix-мс, фильтр
    // «только хвост новее этого момента» (без since — всё окно).
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
        [FromQuery] string? window,
        [FromQuery] long? since,
        CancellationToken ct)
    {
        if (!TryParseWindow(window, out var windowSpan))
            return BadRequest(new { error = $"Неизвестное окно «{window}» (допустимо 15m, 1h)" });

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

        // Средняя скорость на поток за окно графика (15m/1h): Δсчётчика/Δсуммы времени.
        // Считается по окну запроса (не в Current, который считается один раз на опрос):
        // простой (движок молчит) в среднее не входит, нет данных/рестарт → null.
        var avg = _reader.GetAverageSpeed(windowSpan);

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
                AvgGenTps = avg.GenTps,
                AvgPromptTps = avg.PromptTps,
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
        });
    }

    // GET /api/local-model/stats/media?since=<unix ms>
    // Вкладка «Медиа»: статус ComfyUI + слоты + running/queue/lastHour. Только admin.
    // IComfyStats — ОПЦИОНАЛЬНАЯ зависимость: подсистема images отключаема, а обязательный
    // параметр от неё вне её самой — дефект по ADR-014. Без неё → 200 {configured:false},
    // а не 500. since — unix-мс, фильтрует lastHour (только завершённые новее момента).
    [HttpGet("stats/media")]
    public async Task<IActionResult> Media([FromQuery] long? since, CancellationToken ct)
    {
        var comfy = HttpContext.RequestServices.GetService<IComfyStats>();

        // Подсистема images выключена (IComfyStats null) или снимок null → {configured:false}, 200.
        var snap = comfy is null ? null : await comfy.GetMediaAsync(ct);
        if (snap is null)
            return Ok(new LocalModelMediaResponse { Configured = false });

        var myId = UserId;
        var running = snap.Running.Select(r => MapRunning(r, myId)).ToArray();
        var queue = snap.Queue.Select((r, i) => MapQueue(r, myId, i + 1)).ToArray();
        // since — только завершённые новее момента (аналог хвоста в «Текст»).
        var lastHour = snap.LastHour
            .Where(d => since is null || d.FinishedAt.ToUnixTimeMilliseconds() >= since)
            .Select(d => MapDone(d, myId))
            .ToArray();

        // gpus — карты узла ComfyUI (маршрут /ccs/stats, шаг 31): роль media, как есть с узла;
        // подпись «RTX 3090 · медиа» делает фронт. Пустой список → null (узел не сообщает).
        var gpus = snap.Gpus.Count > 0
            ? snap.Gpus.Select(g => new MediaGpuDto
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
            }).ToArray()
            : null;

        return Ok(new LocalModelMediaResponse
        {
            Configured = snap.Configured,
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

    // window: null/пусто → 15m (дефолт); "<n>m" → n минут; "<n>h" → n часов; иначе false (400).
    private static bool TryParseWindow(string? raw, out TimeSpan window)
    {
        window = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw))
        {
            window = TimeSpan.FromMinutes(15);
            return true;
        }
        var s = raw.Trim();
        if (s.Length < 2) return false;
        var unit = s[^1];
        var numPart = s[..^1];
        if (!int.TryParse(numPart, out var n) || n <= 0) return false;
        window = unit switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            _ => TimeSpan.Zero,
        };
        return window != TimeSpan.Zero;
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
    }

    // Скорость на поток: ток/с, Δсчётчика/Δсуммы времени.
    // GenPerStream/PrefillPerStream — за окно ~60 с (как чувствует чат);
    // AvgGenTps/AvgPromptTps — за окно графика (15m/1h), только время активной генерации.
    public sealed record SpeedDto
    {
        public double? GenPerStream { get; init; }
        public double? PrefillPerStream { get; init; }
        // Средняя скорость генерации на поток (ток/с) за окно графика: Δgeneration_tokens/Δdecode_time.
        public double? AvgGenTps { get; init; }
        // Средняя скорость чтения промпта (ток/с) за окно графика: Δprefill_kv_computed_tokens/Δprefill_time.
        public double? AvgPromptTps { get; init; }
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
    public sealed record HistoryPointDto
    {
        public long T { get; init; }
        public double? GenThroughput { get; init; }
        public int? Waiting { get; init; }
        public int? Running { get; init; }
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
