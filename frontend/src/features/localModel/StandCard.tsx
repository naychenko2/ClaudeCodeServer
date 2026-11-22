// Карточка «Стенд» вкладки «Стенды» по макету v4 (§«Уровень 1–3»): уровень 1 — две
// нейтральные KPI («Загрузка CPU» и «Память») без точек светофора и тревожных заливок;
// уровень 2 — текущие температуры GPU/CPU без индивидуальных спарков: историю всех
// температур даёт TempChart, а у «Загрузки CPU» — свой Sparkline из отчёта
// (cpuLoadSpark15m, «пики за 10 с»). Источник данных — hwMetricGroups (ST1). Тревожные
// цвета — только по level от бэкенда; stale снимает заливки и приглушает значения
// (макет .stale). Чистый презентационный компонент: без опросов и собственного состояния.
//
// LevelPill переехала сюда из HardwareTab: ST4 удаляет старый StandCard вместе с его
// LevelPill, а старый и новый StandCard используют одну плашку — импорт
// HardwareTab → StandCard без цикла (StandCard не импортирует HardwareTab).

import { Cpu, MemoryStick } from 'lucide-react';
import { type CSSProperties, type ComponentType } from 'react';
import { C, FONT, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { HardwareReport, HwLevel } from './hardwareTypes';
import type { HwTileView } from './hardwareFormat';
import { hwMetricGroups, hwLevelLabel, hwSummary, ruNum } from './hardwareFormat';
import { Sparkline, SparklinePlaceholder } from './Sparkline';

// Плашка уровня стенда (перенесена из HardwareTab без изменения поведения):
// ok → muted, warn → warning, danger → danger.
export function LevelPill({ level, silent }: { level: HwLevel; silent?: boolean }) {
  if (level === 'none') return null;
  // Молчащий узел без опасности — «узел молчит», как в плашке «Узлы», а не «всё в норме».
  if (silent && level !== 'danger') {
    return (
      <span style={{
        display: 'inline-flex', alignItems: 'center', background: C.warningBg, color: C.warningText,
        borderRadius: R.max, padding: `1px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
      }}>узел молчит</span>
    );
  }
  const bg = level === 'danger' ? C.dangerBg : level === 'warn' ? C.warningBg : C.bgInset;
  const fg = level === 'danger' ? C.dangerText : level === 'warn' ? C.warningText : C.textSecondary;
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', background: bg, color: fg,
      borderRadius: R.max, padding: `1px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
    }}>{hwLevelLabel(level)}</span>
  );
}

// Оболочка карточки — как у карточек вкладки: C.bgWhite, C.borderLight, R.xl, SP.md.
const CARD: CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md,
};

// Заголовок карточки (макет .ch h3): 11 / 700 / uppercase / letterSpacing.
const HEAD: CSSProperties = {
  margin: 0, fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em',
  color: C.textSecondary, flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
};

// Уровень 1: две KPI в repeat(2, minmax(0, 1fr)) НА ВСЕХ ширинах (спека: на 320 px
// две KPI остаются в двух колонках); minWidth: 0 не даёт содержимому раздуть колонку.
const KPI_GRID: CSSProperties = {
  display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: SP.sm,
};

// Уровень 2: температуры сейчас — тоже две колонки (макет .temps).
const TEMP_GRID: CSSProperties = {
  display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: SP.sm,
};

// Подпись уровня 2 (макет .level-caption): «Температуры сейчас» + приписка.
const CAPTION: CSSProperties = {
  display: 'flex', flexWrap: 'wrap', gap: `2px ${SP.sm}px`, margin: `${SP.md}px 0 6px`,
  color: C.textHeading, fontSize: FS.xs, fontWeight: 600,
};

// Приписка в подписи («история — в графике ниже») — обычный вес и приглушённый цвет.
const CAPTION_NOTE: CSSProperties = { color: C.textMuted, fontWeight: 400 };

// KPI-плитка уровня 1 (макет .kpi): C.bgCard, рамка C.borderLight, R.xl, SP.md.
const KPI_TILE: CSSProperties = {
  minWidth: 0, background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md,
};

// Плитка уровня 2 (макет .temp): C.bgCard, C.borderLight, R.lg, SP.sm.
const TEMP_TILE: CSSProperties = {
  minWidth: 0, background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: SP.sm,
};

// «нет данных» / «датчика нет» (по sensor узла).
function noDataLabel(sensor: HwTileView['sensor']): string {
  return sensor === 'none' ? 'датчика нет' : 'нет данных';
}

// Значение KPI: FONT.mono FS.h1 600 C.textHeading; нет данных — мельче и приглушённо
// (паттерн FarmKpiCard: FONT.mono FS.sm C.textMuted).
function kpiValueStyle(has: boolean, stale: boolean): CSSProperties {
  return has
    ? { fontFamily: FONT.mono, fontSize: FS.h1, fontWeight: 600, lineHeight: 1.15, marginTop: SP.xs, color: stale ? C.textMuted : C.textHeading }
    : { fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted };
}

// Значение плитки уровня 2 (макет .temp-value): FONT.mono 20 px 600; warn/danger
// красят значение, stale и нет данных — приглушённые. level null — нейтрально:
// окраска не применяется, значение обычного цвета.
function tempValueStyle(level: HwLevel | null, noData: boolean, stale: boolean): CSSProperties {
  if (stale || noData) return { fontFamily: FONT.mono, fontSize: 20, fontWeight: 600, lineHeight: 1.2, marginTop: 2, color: C.textMuted };
  return {
    fontFamily: FONT.mono, fontSize: 20, fontWeight: 600, lineHeight: 1.2, marginTop: 2,
    color: level === 'danger' ? C.dangerText : level === 'warn' ? C.warningText : C.textHeading,
  };
}

// Тон плитки уровня 2 по level (макет .temp.warn/.temp.danger): ok — нейтральная
// плитка с зелёной точкой; stale, нет данных и level null (окраска не применяется)
// — без окраски и без зелёной точки, точка C.track.
// Тревожные цвета приходят ТОЛЬКО из level — своих порогов нет.
function tempTone(level: HwLevel | null, noData: boolean, stale: boolean): { bg: string; border: string; dot: string } {
  if (stale || noData || level == null) return { bg: C.bgCard, border: C.borderLight, dot: C.track };
  switch (level) {
    case 'danger': return { bg: C.dangerBg, border: C.dangerBorder, dot: C.danger };
    case 'warn': return { bg: C.warningBg, border: C.warning, dot: C.warning };
    default: return { bg: C.bgCard, border: C.borderLight, dot: C.success };
  }
}

// Иконки KPI — по view.icon из hwMetricGroups (hwIcon: cpu → Cpu, ram → MemoryStick).
const ICONS: Record<string, ComponentType<{ size?: number; strokeWidth?: number; color?: string; style?: CSSProperties }>> = {
  Cpu, MemoryStick,
};

// KPI-плитка уровня 1: иконка + подпись, значение, деталь и спарк. Нейтральная:
// точек светофора и тревожных заливок нет (мутация «добавить C.success в primary»
// обязана краснить тест). Рендер по value: sensor='none' RAM не скрывает KPI.
// Спарк — по модели: spark непустой → Sparkline C.info (занятость RAM, история
// загрузки CPU — «пики за 10 с»), value без числа → подложка; spark пустой → ничего.
function Kpi({ view, stale }: { view: HwTileView; stale: boolean }) {
  const has = view.value != null && Number.isFinite(view.value);
  const cpuKpi = view.key === 'cpu-load';
  const Icon = ICONS[view.icon] ?? Cpu;
  return (
    <div style={KPI_TILE}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary }}>
        <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        {/* У cpu-load подпись переносится (nameWrap) вместо усечения: «Загрузка CPU»
            обязана читаться целиком на 320 px; остальные подписи — многоточие. */}
        <span style={view.nameWrap
          ? { minWidth: 0, whiteSpace: 'normal' }
          : { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{view.name}</span>
      </div>
      <div style={kpiValueStyle(has, stale)}>
        {has ? <>{Math.round(view.value!)} <span style={KPI_UNIT}>{view.unit}</span></> : 'нет данных'}
      </div>
      {has && view.detail && (
        <div style={{ minWidth: 0, marginTop: 2, fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere' }}>{view.detail}</div>
      )}
      {view.spark.length === 0 ? null
        : has ? (
          cpuKpi
            ? <div title="Пики за 10 с — максимум в бакете, не средняя загрузка">
                <Sparkline points={view.spark} color={stale ? C.textMuted : C.info} ariaLabel="Загрузка CPU, пики за 10 с, 15 минут" />
              </div>
            : <Sparkline points={view.spark} color={stale ? C.textMuted : C.info} ariaLabel="Занятость памяти за 15 минут" />
        )
          : <SparklinePlaceholder />}
    </div>
  );
}

// Единицы в значении KPI — мелким шрифтом, приглушённые (число остаётся моно крупным).
const KPI_UNIT: CSSProperties = { fontSize: FS.xs, fontWeight: 400, color: C.textMuted };

// Плитка уровня 2: точка уровня + имя, значение, подпись датчика и деталь.
// Спарков нет — историю даёт TempChart (ST1 кладёт spark=[] у secondary).
function TempTile({ view, stale }: { view: HwTileView; stale: boolean }) {
  const noData = view.value == null;
  const t = tempTone(view.level, noData, stale);
  return (
    <div style={{ ...TEMP_TILE, background: t.bg, border: `1px solid ${t.border}` }} title={view.thresholdTitle || undefined}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary }}>
        <span style={{ width: 8, height: 8, borderRadius: R.full, background: t.dot, flex: 'none' }} aria-hidden />
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{view.name}</span>
      </div>
      <div style={tempValueStyle(view.level, noData, stale)}>
        {noData ? noDataLabel(view.sensor) : `${view.dec ? ruNum(view.value!) : Math.round(view.value!)} ${view.unit}`}
      </div>
      {view.sensorLabel && (
        <div style={{ fontSize: 10, color: C.textSecondary, marginTop: 2 }}>{view.sensorLabel}</div>
      )}
      {view.detail && (
        <div style={{ fontSize: 10, color: C.textSecondary, lineHeight: 1.4, marginTop: 2, overflowWrap: 'anywhere' }}>{view.detail}</div>
      )}
    </div>
  );
}

// === StandCard ===

// isMobile — строки сводки переносятся на узкой ширине, а не обрезаются.
export function StandCard({ report, isMobile }: { report: HardwareReport; isMobile: boolean }) {
  const stale = report.status === 'stale';
  const groups = hwMetricGroups(report);
  return (
    <section aria-labelledby="stand-card-title" style={CARD}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 id="stand-card-title" style={HEAD}>Стенд</h3>
        <LevelPill level={report.level} silent={stale} />
      </div>
      {/* Строка-сводка hwSummary: «нет свежих данных» при stale, иначе — самый горячий узел */}
      <div style={{ fontSize: FS.sm, color: C.textSecondary, margin: `-2px 0 ${SP.sm}px`, overflowWrap: isMobile ? 'anywhere' : 'normal' }}>
        {hwSummary(report)}
      </div>
      {/* Уровень 1: две нейтральные KPI */}
      <div style={KPI_GRID}>
        {groups.primary.map(v => <Kpi key={v.key} view={v} stale={stale} />)}
      </div>
      {/* Уровень 2: температуры сейчас; пусто (узел без CPU/GPU-датчиков) — не выводим */}
      {groups.secondary.length > 0 && (
        <>
          <div style={CAPTION}>Температуры сейчас <span style={CAPTION_NOTE}>история — в графике ниже</span></div>
          <div style={TEMP_GRID}>
            {groups.secondary.map(v => <TempTile key={v.key} view={v} stale={stale} />)}
          </div>
        </>
      )}
    </section>
  );
}
