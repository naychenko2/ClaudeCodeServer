// Контракт вкладок «Железо»/«Стенды»: DTO бэкенда (System.Text.Json camelCase).
// Источники правды — фактический код бэкенда, не спецификация:
//   backend/ClaudeHomeServer.Llm/LocalStats/HardwareReport.cs
//   backend/ClaudeHomeServer.Llm/LocalStats/HardwareFarmReport.cs
//
// Уровни и пороги считает БЕКЕНД (поле level): фронт только красит по level,
// свои пороги не дублирует. 'none' — значения нет (датчик не ответил / нет датчика).

// Уровень узла/карты: 'ok' | 'warn' | 'danger' | 'none' (none — значения нет).
export type HwLevel = 'ok' | 'warn' | 'danger' | 'none';

// Роли карты: 'text' (vLLM) / 'media' (ComfyUI); одна карта может держать обе.
export type HwRole = 'text' | 'media';

// Статус узла фермы (сэмплер, HardwareSampler.cs:78): 'stale' — нет свежего снимка,
// 'down' — срез не получен (в т.ч. узла нет в видящем), 'incompatible' — другая версия
// источника. Нештатный статус — не тревога: уровень узла при нём 'ok', о нём говорят
// Status и Since (момент начала нештатности).
export type HwNodeStatus = 'ok' | 'stale' | 'down' | 'incompatible';

// Семейство ряда графика «Температуры». Порядок в union не значим; 'cpu' намеренно
// последним (проверка Ф0 не допускает в файле литерала 'cpu' с последующей чертой).
export type HwFamily = 'mem' | 'core' | 'hotspot' | 'chipset' | 'water' | 'disk' | 'board' | 'cpu';

// Видеокарта (nvidia-smi + помощник root для памяти GDDR6X). Числовые поля nullable:
// null — датчик не ответил (nvidia-smi/помощник), не «0».
export interface HardwareGpu {
  index: number;
  uuid: string;
  name: string;
  roles: HwRole[];
  utilPerc: number | null;
  memUsedMb: number | null;
  memTotalMb: number | null;
  memBusPerc: number | null;
  coreTempC: number | null;
  memTempC: number | null;
  hotspotTempC: number | null;
  fanPerc: number | null;
  clockMhz: number | null;
  clockMaxMhz: number | null;
  memClockMhz: number | null;
  powerW: number | null;
  powerLimitW: number | null;
  throttle: { powerCap: boolean; thermal: boolean };
  pstate: string | null;
  pcie: { gen: number | null; width: number | null };
  busId: string;
  // Худший из памяти/ядра/горячей точки (бэкенд).
  level: HwLevel;
  levels: { mem: HwLevel; core: HwLevel; hotspot: HwLevel };
}

// Узел стенда (плитка). kind — тип датчика (определяет иконку и подписи);
// name — человекочитаемое имя («Карта 1», «Процессор», «Диск кэша»…).
// key НЕ перечисляется: на узле бывает несколько плиток одного kind — при одном сокете
// 'cpu'/'vrm', при двух — 'cpu-0'/'cpu-1', неразведённый VRM — 'vrm-board' (ADR-029).
// Ветвление — только по kind. Порядок в union не значим; 'cpu' намеренно последним
// (см. комментарий к HwFamily).
export interface HardwareNode {
  key: string;
  kind: 'gpu' | 'vrm' | 'board' | 'chipset' | 'water' | 'disk' | 'ram' | 'cpu';
  name: string;
  tempC: number | null;
  // 'ok' — датчик отвечает; 'none' — датчика нет вовсе; 'nodata' — датчик есть, но не ответил.
  sensor: 'ok' | 'none' | 'nodata';
  level: HwLevel;
  // Пороги (°C) — бэкенд; warnC=null — предупреждения нет (например, горячая точка).
  warnC: number | null;
  dangerC: number | null;
  detail: {
    ccdC?: number[];
    fanRpm?: number;
    role?: 'system' | 'kv';
    temp2C?: number;
    // Периферия, °C: деталь плитки «Плата», только IPMI (worker).
    peripheralC?: number;
    // Предел диска; null — не сообщает (пороги 70/80). Поле пишется всегда, в т.ч. null.
    critC?: number | null;
    usedGb?: number;
    totalGb?: number;
    // Занято, % (RAM и диск кэша: плитка показывает его вместо температуры, spark15m — тоже %).
    usedPerc?: number;
  };
  // Мини-график: 90 точек, шаг 10 с, 15 мин; null — разрыв линии.
  spark15m: (number | null)[];
}

// Вентилятор корпуса (только подключённые входы).
export interface HardwareFan {
  label: string;
  rpm: number;
}

// Ряд графика «Температуры»: points — (number|null)[] за выбранное окно.
export interface HardwareSeries {
  key: string;
  family: HwFamily;
  gpuIndex?: number;
  label: string;
  points: (number | null)[];
}

// Снимок ОДНОГО узла: GET /api/local-model/hardware?window=1m|5m|15m|1h&node=<key>
// (только админ; без node бэкенд берёт worker — прежний контракт).
export interface HardwareReport {
  at: number;
  status: 'ok' | 'stale';
  staleSince?: number;
  level: HwLevel;
  gpus: HardwareGpu[];
  nodes: HardwareNode[];
  caseFans: HardwareFan[];
  series: HardwareSeries[];
  stepMs: number;
  // Узел, к которому относится отчёт (Ш3a): key/title из реестра бэкенда. null — пустой
  // отчёт (сервис отчёта не в контейнере). В ответах до Ш3a поля нет — optional.
  node?: { key: string; title: string } | null;
  // Аптайм узла, с; загрузка CPU узла, %. null — источник не ответил.
  uptimeS?: number | null;
  cpuLoadPerc?: number | null;
}

// === Краткая сводка (HardwareBrief) — только у админа ===
// Поле hardware в ответах /stats и /stats/media (lib/api.ts ссылается сюда
// type-only); отдельного опроса нет. Не-админу поля нет (gpus:null).
export interface HardwareBriefGpu {
  uuid: string;
  memTempC: number | null;
  level: HwLevel;
  powerCap: boolean;
  thermal: boolean;
}
// Уровень каждого узла фермы (для точек переключателя узлов).
export interface HardwareBriefNode {
  key: string;
  level: HwLevel;
}
export interface HardwareBrief {
  level: HwLevel;
  gpus: HardwareBriefGpu[];
  // Узлы фермы и их уровни (Ш3b): порядок — как в конфиге бэкенда. В ответах до Ш3b
  // поля нет — optional; у не-админа всей сводки (hardware) нет вовсе.
  nodes?: HardwareBriefNode[];
}

// === Сводка фермы (HardwareFarmReport.cs) — GET /api/local-model/hardware/farm, только админ ===
// Карточка «Ферма» (макет А): все узлы сразу, без выбора. Порядок farmNodes — как в
// конфиге (порядок карточек на фронте). Нештатный узел (status 'down'/'stale'/
// 'incompatible') не красится: о нём говорят status и since.

// Самый горячий измеренный показатель узла; label — имя его плитки.
export interface HardwareFarmHottest {
  label: string;
  tempC: number | null;
  level: HwLevel;
}

// Узел сводки: key/title — из реестра бэкенда (Source наружу не отдаётся),
// roles/engines/media — что на нём работает, дальше — измеренное.
// cpuLoadPerc/ramUsedPerc — %; uptimeS — с. null — источник не ответил.
export interface HardwareFarmNode {
  key: string;
  title: string;
  roles: HwRole[];
  engines: string[];
  media: boolean;
  status: HwNodeStatus;
  since: number | null;
  level: HwLevel;
  hottest: HardwareFarmHottest | null;
  gpus: HardwareBriefGpu[];
  uptimeS: number | null;
  cpuLoadPerc: number | null;
  ramUsedPerc: number | null;
}

// ИБП фермы (upsd, HardwareUpsDto). status — как у узла: нештатный ИБП не красится (level 'ok').
// power — по флагам ups.status: 'line' от сети, 'battery' от батареи, 'lowBattery' батарея садится;
// null — флагов нет. Спарки — 15 мин, 90 точек по 10 с; null — разрыв; батарея — в десятых вольта.
export type HwUpsPower = 'line' | 'battery' | 'lowBattery';
export interface HardwareUps {
  title: string;
  status: 'ok' | 'stale' | 'down';
  since: number | null;
  level: HwLevel;
  power: HwUpsPower | null;
  // Уровни плиток «Питание» и «Нагрузка» (level — худший из них).
  powerLevel: HwLevel;
  loadLevel: HwLevel;
  loadPerc: number | null;
  loadW: number | null;
  chargePerc: number | null;
  batteryV: number | null;
  batteryNominalV: number | null;
  inputV: number | null;
  inputHz: number | null;
  outputV: number | null;
  tempC: number | null;
  loadSpark15m: (number | null)[];
  chargeSpark15m: (number | null)[];
  inputVSpark15m: (number | null)[];
  batteryDeciVSpark15m: (number | null)[];
}

export interface HardwareFarm {
  at: number;
  level: HwLevel;
  farmNodes: HardwareFarmNode[];
  // null — ИБП не настроен (Ups:Host пуст); в ответах до ИБП поля нет.
  ups?: HardwareUps | null;
}
