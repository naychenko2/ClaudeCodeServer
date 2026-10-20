// Контракт вкладки «Железо»: DTO бэкенда (HardwareReport.cs, System.Text.Json camelCase).
// Источники правды — фактический код бэкенда, не спецификация:
//   backend/ClaudeHomeServer.Llm/LocalStats/HardwareReport.cs
//
// Уровни и пороги считает БЕКЕНД (поле level): фронт только красит по level,
// свои пороги не дублирует. 'none' — значения нет (датчик не ответил / нет датчика).

// Уровень узла/карты: 'ok' | 'warn' | 'danger' | 'none' (none — значения нет).
export type HwLevel = 'ok' | 'warn' | 'danger' | 'none';

// Роли карты: 'text' (vLLM) / 'media' (ComfyUI); одна карта может держать обе.
export type HwRole = 'text' | 'media';

// Семейство ряда графика «Температуры».
export type HwFamily = 'mem' | 'core' | 'hotspot' | 'cpu' | 'chipset' | 'disk';

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

// Узел стенда. kind — тип датчика (определяет иконку и подписи);
// name — человекочитаемое имя («Карта 1», «Процессор», «Диск кэша»…).
export interface HardwareNode {
  key: string;
  kind: 'gpu' | 'cpu' | 'vrm' | 'chipset' | 'disk' | 'ram';
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
    // Предел диска; null — не сообщает (пороги 70/80). Поле пишется всегда, в т.ч. null.
    critC?: number | null;
    usedGb?: number;
    totalGb?: number;
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

// Снимок стенда: GET /api/local-model/hardware?window=1m|5m|15m|1h (только админ).
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
export interface HardwareBrief {
  level: HwLevel;
  gpus: HardwareBriefGpu[];
}
