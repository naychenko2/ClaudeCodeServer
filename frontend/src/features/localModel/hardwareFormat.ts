// Чистые функции вкладки «Железо»: форматирование значений, подписи, выбор
// «самой горячей» точки, сводка, модель представления плитки. Юнит-тесты без React/DOM.
//
// Уровень (level) и пороги считает БЕКЕНД (HardwareReport.cs); фронт только
// красит по level и не дублирует пороги. Здесь — только текст и выбор.

import type { HardwareBrief, HardwareGpu, HardwareNode, HardwareReport, HwLevel } from './hardwareTypes';

// === Числа ===

// Десятичное число по-русски: «82,8»; целое — без хвоста: «84».
export function ruNum(n: number): string {
  const r = Math.round(n * 10) / 10;
  return Number.isInteger(r) ? String(r) : r.toFixed(1).replace('.', ',');
}

// Температура: «82,8 °C» (dec) / «84 °C» (целая); null — «нет данных».
export function hwTemp(v: number | null, dec = false): string {
  if (v == null || !Number.isFinite(v)) return 'нет данных';
  return `${dec ? ruNum(v) : Math.round(v)} °C`;
}

// === Иконки узлов (lucide-имя) ===
// kind узла → иконка lucide. Порядок и состав — фиксированный (спецификация).
export function hwIcon(kind: HardwareNode['kind']): string {
  switch (kind) {
    case 'gpu': return 'Gpu';
    case 'cpu': return 'Cpu';
    case 'vrm': return 'Zap';
    case 'chipset': return 'CircuitBoard';
    case 'disk': return 'HardDrive';
    case 'ram': return 'MemoryStick';
    default: return 'Cpu';
  }
}

// === Модель представления плитки ===
// Плитка чисто презентационна: все тексты/цвета/граф — из этой модели.
// value=null → «нет данных»/«датчика нет» (по sensor); spark — 90 точек.
export interface HwTileView {
  key: string;
  icon: string;
  name: string;
  value: number | null;
  dec: boolean;
  level: HwLevel;
  sensor: 'ok' | 'none' | 'nodata';
  sensorLabel: string;
  detail: string;
  spark: (number | null)[];
  thresholdTitle: string;
}

// Строит модели плиток из снимка: порядок узлов фиксированный (как в бэкенде),
// по тревоге не сортируем. Для gpu-узла detail — «ядро N · гор. точка M» (из карты).
export function hwTileView(report: HardwareReport): HwTileView[] {
  return report.nodes.map(node => {
    let sensorLabel = '';
    let detail = '';
    let dec = false;
    if (node.kind === 'gpu') {
      const g = report.gpus.find(x => x.index === parseInt(node.key.slice(3), 10));
      const memOk = g?.memTempC != null;
      sensorLabel = memOk ? 'память' : 'ядро · память: нет данных';
      if (g) {
        detail = `ядро ${g.coreTempC != null ? Math.round(g.coreTempC) : '—'} · гор. точка ${g.hotspotTempC != null ? Math.round(g.hotspotTempC) : '—'}`;
      }
    } else if (node.kind === 'cpu') {
      sensorLabel = 'общая';
      const ccd = node.detail.ccdC ?? [];
      if (ccd.length) detail = `кристаллы ${ccd.map(v => Math.round(v)).join(' и ')}`;
    } else if (node.kind === 'vrm') {
      sensorLabel = 'цепи питания на плате';
    } else if (node.kind === 'chipset') {
      if (node.detail.fanRpm != null) detail = `вентилятор ${node.detail.fanRpm} об/мин`;
    } else if (node.kind === 'disk') {
      dec = true;
      if (node.detail.role === 'kv') {
        sensorLabel = 'самый горячий датчик';
        const crit = node.detail.critC;
        if (crit != null && node.tempC != null) {
          const margin = crit - node.tempC;
          const used = node.detail.usedGb != null && node.detail.totalGb != null
            ? ` · занято ${node.detail.usedGb} из ${node.detail.totalGb} ГБ` : '';
          detail = `до предела ${ruNum(margin)} °C${used}`;
        } else if (node.detail.usedGb != null && node.detail.totalGb != null) {
          detail = `занято ${node.detail.usedGb} из ${node.detail.totalGb} ГБ`;
        }
      } else if (node.detail.critC == null) {
        detail = 'предел не сообщает — пороги 70 / 80';
      }
    } else if (node.kind === 'ram') {
      if (node.detail.usedGb != null && node.detail.totalGb != null) {
        detail = `занято ${node.detail.usedGb} из ${node.detail.totalGb} ГБ`;
      }
    }
    return {
      key: node.key,
      icon: hwIcon(node.kind),
      name: node.name,
      value: node.tempC,
      dec,
      level: node.level,
      sensor: node.sensor,
      sensorLabel,
      detail,
      spark: node.spark15m,
      thresholdTitle: hwThresholdTitle(node),
    };
  });
}

// === Плитка: «датчика нет» / «нет данных» (курсив C.textMuted) ===
export function hwNoData(node: HardwareNode): boolean {
  return node.tempC == null;
}
export function hwNoDataLabel(node: HardwareNode): string {
  return node.sensor === 'none' ? 'датчика нет' : 'нет данных';
}

// === Плитка: title «Предупреждение с N °C, опасность с M °C» ===
export function hwThresholdTitle(node: HardwareNode): string {
  const w = node.warnC;
  const d = node.dangerC;
  if (d == null) return '';
  const warnPart = w != null ? `Предупреждение с ${w} °C, ` : '';
  return `${warnPart}опасность с ${d} °C`;
}

// === Вентиляторы корпуса: «Вентиляторы корпуса: N и M об/мин» ===
export function hwFansLine(fans: { label: string; rpm: number }[]): string {
  if (fans.length === 0) return '';
  return `Вентиляторы корпуса: ${fans.map(f => f.rpm).join(' и ')} об/мин`;
}

// === Сводка «Стенд» ===
const RANK: Record<HwLevel, number> = { none: -1, ok: 0, warn: 1, danger: 2 };

// Худший уровень списка (none не считается).
export function hwWorst(levels: HwLevel[]): HwLevel {
  let w: HwLevel = 'ok';
  for (const l of levels) {
    if (l === 'danger') return 'danger';
    if (l === 'warn') w = 'warn';
  }
  return w;
}

// «Самая горячая» точка: худший уровень, при равенстве — меньший запас до danger.
export function hwClosest(nodes: HardwareNode[]): HardwareNode | null {
  const c = nodes.filter(n => n.tempC != null && n.dangerC != null);
  if (c.length === 0) return null;
  c.sort((a, b) => {
    const r = RANK[b.level] - RANK[a.level];
    if (r !== 0) return r;
    return (a.dangerC! - (a.tempC ?? 0)) - (b.dangerC! - (b.tempC ?? 0));
  });
  return c[0];
}

// Строка-сводка (без разметки): по уровню стенда.
export function hwSummary(report: { level: HwLevel; status: string; nodes: HardwareNode[] }): string {
  if (report.status === 'stale') return 'нет свежих данных';
  const c = hwClosest(report.nodes);
  if (!c || c.tempC == null) return '';
  const name = c.name.toLowerCase();
  const val = hwTemp(c.tempC, c.kind === 'disk');
  if (report.level === 'ok') return `Горячее всех — ${name}: ${val}`;
  const danger = c.dangerC != null ? `, опасно с ${c.dangerC} °C` : '';
  return `${report.level === 'danger' ? 'Ближе всех к пределу' : 'Горячо'} — ${name}: ${val}${danger}`;
}

// Плашка уровня стенда: «всё в норме» / «есть горячие узлы» / «есть опасность».
export function hwLevelLabel(level: HwLevel): string {
  switch (level) {
    case 'danger': return 'есть опасность';
    case 'warn': return 'есть горячие узлы';
    case 'ok': return 'всё в норме';
    default: return '';
  }
}

// === Карточка карты: подпись ролей «текст» / «медиа» / «текст · медиа» ===
const ROLE_LABEL: Record<string, string> = { text: 'текст', media: 'медиа' };
export function hwRolesLabel(roles: string[]): string {
  return roles.map(r => ROLE_LABEL[r] ?? r).join(' · ');
}

// «карта N из M»
export function hwGpuOrdinal(index: number, total: number): string {
  return `карта ${index + 1} из ${total}`;
}

// === Строка-ссылка «Видеокарты» (LLM/Медиа, только админ) ===
// «память 68° · 81°»
export function hwBriefMem(brief: HardwareBrief): string {
  const parts = brief.gpus.map(g =>
    g.memTempC == null ? 'нет данных' : `${Math.round(g.memTempC)}°`);
  return `память ${parts.join(' · ')}`;
}

// Худший уровень по сводке (для точки на вкладке).
export function hwBriefLevel(brief: HardwareBrief): HwLevel {
  return brief.level;
}

// «перегрев» (thermal) / «в лимите мощности» (powerCap) по сводке.
export function hwBriefFlags(brief: HardwareBrief): { thermal: boolean; powerCap: boolean } {
  return {
    thermal: brief.gpus.some(g => g.thermal),
    powerCap: brief.gpus.some(g => g.powerCap),
  };
}

// === Чипы семейств графика «Температуры»: лимит трёх семейств ===
// Чип выключен, если семейство не выбрано И уже выбрано максимум (3) — иначе
// линии не читаются. isOn — семейство в выборке; selectedCount — сколько выбрано.
export function familyChipDisabled(isOn: boolean, selectedCount: number, max = 3): boolean {
  return !isOn && selectedCount >= max;
}

// === Карточка карты: запас до замедления памяти ===
// Память ≥ 95 °C — «память замедляется»; иначе «запас до замедления N °C».
export function hwMemMargin(memTempC: number | null): string {
  if (memTempC == null) return 'датчик памяти не ответил';
  const margin = 95 - memTempC;
  return margin > 0 ? `запас до замедления ${Math.round(margin)} °C` : 'память замедляется';
}

// Позиция на шкале 30…110 °C (0..100 %).
export function hwScalePos(v: number): number {
  return Math.max(0, Math.min(100, ((v - 30) / (110 - 30)) * 100));
}

// Уровень карты по памяти (для окраски значения памяти).
export function hwGpuMemLevel(gpu: HardwareGpu): HwLevel {
  return gpu.levels.mem;
}
