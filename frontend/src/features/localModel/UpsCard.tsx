// Карточка «ИБП» вкладки «Стенды»: под карточкой «Узлы», если ИБП настроен (farm.ups).
// Шапка: имя ИБП и плашка питания («от сети» / «от батареи» / «батарея садится»);
// строка-сводка; плитки HwTile: нагрузка, заряд, сеть, батарея (мини-графики — 15 мин).
// Окраска — по уровням бэкенда (powerLevel, loadLevel); свои пороги фронт не держит.
// ИБП молчит (status stale/down) — как молчащий узел: плашка «ИБП молчит», цифры приглушены.

import { C, FS, R, SP } from 'aihome_shell/kit';
import type { HardwareUps, HwLevel } from './hardwareTypes';
import type { HwTileView } from './hardwareFormat';
import { ruNum } from './hardwareFormat';
import { HwTile } from './HwTile';

// «HH:MM:SS» из unix-мс — локальное время браузера.
function formatHms(ms: number | null): string {
  if (ms == null || !Number.isFinite(ms)) return '';
  const d = new Date(ms);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

export function upsSilent(u: HardwareUps): boolean {
  return u.status !== 'ok';
}

// Плашка: молчит; иначе — питание, а при питании от сети и высокой нагрузке — нагрузка.
export function upsPill(u: HardwareUps): { label: string; level: HwLevel } {
  if (upsSilent(u)) return { label: 'ИБП молчит', level: 'warn' };
  if (u.power === 'lowBattery') return { label: 'батарея садится', level: u.powerLevel };
  if (u.power === 'battery') return { label: 'от батареи', level: u.powerLevel };
  if (u.loadLevel === 'danger') return { label: 'перегрузка', level: 'danger' };
  if (u.loadLevel === 'warn') return { label: 'высокая нагрузка', level: 'warn' };
  if (u.power === 'line') return { label: 'от сети', level: 'ok' };
  return { label: 'нет данных', level: 'none' };
}

// Сводка: «внутри 28 °C», выход — только если отличается от входа больше чем на 5 В
// (ватты — в детали плитки «Нагрузка»); молчит — «не отвечает с HH:MM:SS».
export function upsSummary(u: HardwareUps): string {
  if (upsSilent(u)) {
    const t = formatHms(u.since);
    return t ? `не отвечает с ${t}, показаны последние данные` : 'не отвечает';
  }
  const parts: string[] = [];
  if (u.outputV != null && (u.inputV == null || Math.abs(u.outputV - u.inputV) > 5))
    parts.push(`выход ${Math.round(u.outputV)} В`);
  if (u.tempC != null) parts.push(`внутри ${u.tempC} °C`);
  return parts.join(' · ');
}

const sensor = (v: number | null): HwTileView['sensor'] => (v == null ? 'nodata' : 'ok');

// Окраска: «Нагрузка» — loadLevel; «Сеть» — powerLevel, когда ИБП ушёл с сети; «Заряд» — только
// когда батарея садится (жёлтый заряд при 100 % читался бы как «плохая батарея»).
export function upsTiles(u: HardwareUps): HwTileView[] {
  const base = { dec: false, thresholdTitle: '', sensorLabel: '' };
  const offLine = u.power === 'battery' || u.power === 'lowBattery';
  return [
    {
      ...base, key: 'ups-load', icon: 'Gauge', name: 'Нагрузка', unit: '%',
      value: u.loadPerc, level: u.loadLevel, sensor: sensor(u.loadPerc),
      detail: u.loadW != null ? `≈ ${u.loadW} Вт` : '', spark: u.loadSpark15m,
    },
    {
      ...base, key: 'ups-charge', icon: 'BatteryMedium', name: 'Заряд', unit: '%',
      value: u.chargePerc, level: u.power === 'lowBattery' ? u.powerLevel : 'ok', sensor: sensor(u.chargePerc),
      detail: 'по напряжению', spark: u.chargeSpark15m,
    },
    {
      ...base, key: 'ups-input', icon: 'PlugZap', name: 'Сеть', unit: 'В',
      value: u.inputV, level: offLine ? u.powerLevel : 'ok', sensor: sensor(u.inputV),
      detail: u.inputHz != null ? `${ruNum(u.inputHz)} Гц` : '', spark: u.inputVSpark15m,
    },
    {
      ...base, key: 'ups-battery', icon: 'Battery', name: 'Батарея', unit: 'В', dec: true,
      value: u.batteryV, level: 'ok', sensor: sensor(u.batteryV),
      detail: u.batteryNominalV != null ? `номинал ${ruNum(u.batteryNominalV)} В` : '', spark: u.batteryDeciVSpark15m,
    },
  ];
}

// Плашка — role="status": переход «от сети» → «от батареи» озвучивается скринридером.
function Pill({ label, level }: { label: string; level: HwLevel }) {
  const bg = level === 'danger' ? C.dangerBg : level === 'warn' ? C.warningBg : C.bgInset;
  const fg = level === 'danger' ? C.dangerText : level === 'warn' ? C.warningText : C.textSecondary;
  return (
    <span role="status" style={{
      display: 'inline-flex', alignItems: 'center', background: bg, color: fg,
      borderRadius: R.max, padding: `1px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
    }}>{label}</span>
  );
}

export function UpsCard({ ups, isMobile }: { ups: HardwareUps; isMobile: boolean }) {
  const silent = upsSilent(ups);
  const pill = upsPill(ups);
  const summary = upsSummary(ups);
  const gridCols = isMobile ? 'repeat(2, 1fr)' : 'repeat(auto-fit, minmax(130px, 1fr))';
  return (
    <section
      aria-labelledby="ups-card-title"
      style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 id="ups-card-title" title={ups.title} style={{ margin: 0, fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: C.textSecondary, flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {`ИБП · ${ups.title}`}
        </h3>
        <Pill label={pill.label} level={pill.level} />
      </div>
      {summary && <div style={{ fontSize: FS.sm, color: C.textSecondary, margin: `-2px 0 ${SP.sm}px` }}>{summary}</div>}
      <div style={{ display: 'grid', gridTemplateColumns: gridCols, gap: SP.sm }}>
        {upsTiles(ups).map(v => <HwTile key={v.key} view={v} stale={silent} />)}
      </div>
    </section>
  );
}
