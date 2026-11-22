// Секция «Другие датчики» вкладки «Стенды» (макет v4, §«Уровень 3»): корневой
// div id="other-sensors" со штатным управляемым DisclosureSection; тело — компактные
// строки «имя — значение / деталь — состояние» VRM, платы, чипсета, воды, обоих видов
// дисков, вентиляторов корпуса и аптайма. Спарк остаётся только у диска кэша (его
// ряда в TempChart нет). Тревоги не исчезают при свёртке: имена и значения тревожных
// датчиков остаются в meta закрытой секции. Состояние раскрытия держит будущий
// HardwareTab (ST4): open/onToggle — пропсы, своего useState нет.

import { type CSSProperties } from 'react';
import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import type { HardwareFan, HardwareReport, HwLevel } from './hardwareTypes';
import { hwMetricGroups, hwNoDataCountLabel, ruNum } from './hardwareFormat';
import { DisclosureSection } from '../../components/ui/DisclosureSection';
import { Sparkline } from './Sparkline';

// === Модели строк и тексты ===

// Строка секции: из группы details (HwTileView) или из fans/uptime отчёта.
// Число печатается с unit; строка — как есть; null — состояние (нет данных / датчика нет).
export interface SensorRow {
  key: string;
  name: string;
  value: number | string | null;
  unit: string;
  dec: boolean;
  level: HwLevel;
  sensor: 'ok' | 'none' | 'nodata';
  detail: string;
  spark: (number | null)[];
}

// Аптайм: «3 д 19 ч», «19 ч 45 мин», «45 мин»; null — null (в рендере «нет данных»).
export function uptimeLabel(s: number | null | undefined): string | null {
  if (s == null || !Number.isFinite(s) || s < 0) return null;
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d} д ${h} ч`;
  if (h > 0) return `${h} ч ${m} мин`;
  return `${m} мин`;
}

// Вентиляторы корпуса: «820 и 760 об/мин»; пусто — null (строку не выводим).
export function fansValue(fans: HardwareFan[]): string | null {
  if (fans.length === 0) return null;
  return `${fans.map(f => f.rpm).join(' и ')} об/мин`;
}

// Модели строк секции по отчёту: порядок details — порядок report.nodes (не сортируем),
// затем вентиляторы корпуса (если подключены) и аптайм. Спарк — как в группе (только
// диск кэша).
export function sensorRows(report: HardwareReport): SensorRow[] {
  const groups = hwMetricGroups(report);
  const rows: SensorRow[] = groups.details.map(v => ({
    key: v.key, name: v.name, value: v.value, unit: v.unit, dec: v.dec,
    level: v.level, sensor: v.sensor, detail: v.detail, spark: v.spark,
  }));
  const fans = fansValue(report.caseFans);
  if (fans != null) rows.push({
    key: 'case-fans', name: 'Вентиляторы корпуса', value: fans, unit: '', dec: false,
    level: 'none', sensor: 'ok', detail: report.caseFans.map(f => f.label).join(' и '), spark: [],
  });
  rows.push({
    key: 'uptime', name: 'Аптайм', value: uptimeLabel(report.uptimeS), unit: '', dec: false,
    level: 'none', sensor: report.uptimeS == null ? 'nodata' : 'ok', detail: 'с последней перезагрузки', spark: [],
  });
  return rows;
}

// Текст значения строки: число с unit / строка как есть; null — null (состояние).
export function rowValueText(r: SensorRow): string | null {
  if (r.value == null) return null;
  return typeof r.value === 'number'
    ? `${r.dec ? ruNum(r.value) : Math.round(r.value)} ${r.unit}`.trim()
    : r.value;
}

// Мета закрытой секции: тревожные датчики именами и значениями, затем счётчик без
// данных; без тревог — «N показателей · M без данных». stale: уровни не окрашены —
// мета «последние данные · уровни не окрашены» (макет v4).
export function sensorsMeta(rows: SensorRow[], stale: boolean): string {
  if (stale) return 'последние данные · уровни не окрашены';
  const alarms = rows.filter(r => r.level === 'warn' || r.level === 'danger');
  const nodata = rows.filter(r => r.sensor === 'nodata').length;
  if (alarms.length > 0) {
    const alarmText = alarms.map(r => `${r.name} ${rowValueText(r)}`).join(' · ');
    return nodata > 0 ? `${alarmText} · ${nodata} без данных` : alarmText;
  }
  const total = rows.length;
  if (total === 0) return '';
  const m10 = total % 10;
  const m100 = total % 100;
  const word = m10 === 1 && m100 !== 11 ? 'показатель'
    : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? 'показателя' : 'показателей';
  return `${total} ${word}${nodata > 0 ? ` · ${nodata} без данных` : ''}`;
}

// Оболочка секции: div id="other-sensors" вокруг DisclosureSection — цель для
// ui_probe ST4 (c5) и якорь переходов.
const SHELL: CSSProperties = { minWidth: 0 };

// Строка датчика (макет .sensor-row): grid «имя+деталь | значение+состояние».
const ROW_GRID: CSSProperties = {
  display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: `1px ${SP.sm}px`, minWidth: 0,
  padding: `${SP.sm}px 0`, borderBottom: `1px solid ${C.borderLight}`,
};

// Имя датчика: FS.xs C.textSecondary; значение — моно 12px 600.
const ROW_NAME: CSSProperties = { display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, fontSize: FS.xs, color: C.textSecondary };

// Значение: моно 12 px 600; warn/danger красят значение.
function rowValueStyle(level: HwLevel, noData: boolean, stale: boolean): CSSProperties {
  if (noData) return { fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600, textAlign: 'right', whiteSpace: 'nowrap', color: C.textMuted, fontStyle: 'italic' };
  if (stale) return { fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600, textAlign: 'right', whiteSpace: 'nowrap', color: C.textMuted };
  return {
    fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600, textAlign: 'right', whiteSpace: 'nowrap',
    color: level === 'danger' ? C.dangerText : level === 'warn' ? C.warningText : C.textHeading,
  };
}

// Состояние справа под значением: «норма/горячо/опасность», stale — «последнее значение»,
// нет данных — «нет данных»/«датчика нет» (состояния различаются).
function rowState(r: SensorRow, stale: boolean): string {
  if (r.value == null) return r.sensor === 'none' ? 'датчика нет' : 'нет данных';
  if (stale) return 'последнее значение';
  switch (r.level) {
    case 'danger': return 'опасность';
    case 'warn': return 'горячо';
    default: return 'норма';
  }
}

// Точка уровня строки: stale и нет данных — C.track; иначе по level (макет .sensor-name dot).
function rowDot(level: HwLevel, noData: boolean, stale: boolean): string {
  if (stale || noData) return C.track;
  switch (level) {
    case 'danger': return C.danger;
    case 'warn': return C.warning;
    default: return C.success;
  }
}

// Строка датчика: имя (+точка уровня), значение, деталь, состояние; у диска кэша —
// спарк заполненности C.info на всю ширину строки.
function SensorRowView({ row, stale }: { row: SensorRow; stale: boolean }) {
  const noData = row.value == null;
  return (
    <div style={ROW_GRID}>
      <div style={ROW_NAME}>
        {row.level !== 'none' && !stale && (
          <span style={{ width: 8, height: 8, borderRadius: R.full, background: rowDot(row.level, noData, stale), flex: 'none' }} aria-hidden />
        )}
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{row.name}</span>
      </div>
      <div style={rowValueStyle(row.level, noData, stale)}>
        {noData ? (row.sensor === 'none' ? 'датчика нет' : 'нет данных') : rowValueText(row)}
      </div>
      <div style={{ minWidth: 0, fontSize: 10, color: C.textMuted, overflowWrap: 'anywhere' }}>{row.detail}</div>
      <div style={{ minWidth: 0, fontSize: 10, color: rowLevelText(row.level, noData, stale), textAlign: 'right' }}>
        {stale && row.value != null ? 'последнее значение' : rowState(row, stale)}
      </div>
      {row.spark.length > 0 && !noData && (
        <div style={{ gridColumn: '1 / span 2' }}>
          <Sparkline points={row.spark} color={stale ? C.textMuted : C.info} ariaLabel="Заполненность диска кэша за 15 минут" />
        </div>
      )}
    </div>
  );
}

// Цвет состояния строки: stale/noData — muted; warn/danger — тревожные тексты.
function rowLevelText(level: HwLevel, noData: boolean, stale: boolean): string {
  if (stale || noData) return C.textMuted;
  return level === 'danger' ? C.dangerText : level === 'warn' ? C.warningText : C.textMuted;
}

// === Секция ===

// Управляемая секция: состояние раскрытия — у потребителя (ST4), своего useState нет.
export function OtherSensorsSection({ report, open, onToggle, isMobile }: {
  report: HardwareReport;
  open: boolean;
  onToggle: () => void;
  isMobile: boolean;
}) {
  const stale = report.status === 'stale';
  const rows = sensorRows(report);
  const nodata = rows.filter(r => r.sensor === 'nodata').length;
  return (
    <div id="other-sensors" style={SHELL}>
      <DisclosureSection
        title="Другие датчики"
        meta={sensorsMeta(rows, stale)}
        open={open}
        onToggle={onToggle}
        isMobile={isMobile}
      >
        {rows.map(row => <SensorRowView key={row.key} row={row} stale={stale} />)}
        {nodata > 0 && (
          <div style={{ paddingTop: SP.sm, fontSize: FS.xs, fontStyle: 'italic', color: C.textMuted }}>
            {hwNoDataCountLabel(nodata)}
          </div>
        )}
      </DisclosureSection>
    </div>
  );
}

