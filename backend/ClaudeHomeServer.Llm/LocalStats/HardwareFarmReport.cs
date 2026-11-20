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

// ИБП фермы (upsd). Status — "ok"|"stale"|"down": нештатный ИБП, как и узел, не красится.
// Power — "line" | "battery" | "lowBattery" по флагам ups.status; null — флагов нет.
// PowerLevel/LoadLevel — уровни плиток «Питание» и «Нагрузка», Level — худший из них.
// Spark — 15 мин, 90 точек по 10 с, максимум в бакете; BatteryDeciVSpark15m — в десятых вольта.
public sealed record HardwareUpsDto(
    string Title, string Status, long? Since, string Level, string? Power, string PowerLevel, string LoadLevel,
    int? LoadPerc, int? LoadW, int? ChargePerc, double? BatteryV, double? BatteryNominalV,
    double? InputV, double? InputHz, double? OutputV, int? TempC,
    int?[] LoadSpark15m, int?[] ChargeSpark15m, int?[] InputVSpark15m, int?[] BatteryDeciVSpark15m);

// Порядок FarmNodes — как в конфиге (порядок карточек на фронте). Ups — null, если ИБП не настроен.
public sealed record HardwareFarmReport(long At, string Level, HardwareFarmNodeDto[] FarmNodes, HardwareUpsDto? Ups = null);
