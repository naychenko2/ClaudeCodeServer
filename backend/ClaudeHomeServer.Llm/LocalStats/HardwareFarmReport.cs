namespace ClaudeHomeServer.Services.Llm;

// ─── Wire-контракт сводки фермы (карточка «Ферма», макет А): GET …/hardware/farm — Ш3b ───
// Уровень узла и фермы считается тем же кодом, что и в отчёте узла (HardwareThresholds).
// Красным красится только измеренное: у узла «down»/«stale»/«incompatible» уровень «ok»,
// о нём говорят Status и Since (момент начала нештатности).

// Самый горячий измеренный показатель узла: подпись — имя его плитки.
public sealed record HardwareFarmHottestDto(string Label, int? TempC, string Level);

// Узел сводки: Key/Title — из реестра HardwareNodes (Source наружу не отдаётся),
// Roles/Engines/Media — что на нём работает, дальше — измеренное.
public sealed record HardwareFarmNodeDto(
    string Key, string Title, string[] Roles, string[] Engines, bool Media,
    string Status, long? Since, string Level,
    HardwareFarmHottestDto? Hottest, HardwareBriefGpu[] Gpus,
    long? UptimeS, int? CpuLoadPerc, int? RamUsedPerc);

// Порядок FarmNodes — как в конфиге (порядок карточек на фронте).
public sealed record HardwareFarmReport(long At, string Level, HardwareFarmNodeDto[] FarmNodes);
