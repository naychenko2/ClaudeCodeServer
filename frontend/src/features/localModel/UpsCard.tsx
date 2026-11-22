// Карточка «ИБП» вкладки «Стенды» (макет docs/mockups/local-model-hardware-cluster-v4.html,
// §«Компактный ИБП»): шапка с плашкой питания (role=status), строка-сводка, одна крупная
// KPI «Заряд» и одна полоса из трёх ячеек (нагрузка, сеть, батарея).
// Рядом с карточкой — отдельная секция «Динамика за 15 мин» (UpsDynamicsSection, sibling:
// состояние раскрытия и подключение — за HardwareTab, ST4; внутрь UpsCard не вкладывается).
// Значения, детали и уровни даёт upsMetricGroups (hardwareFormat.ts, ST1) — своих порогов
// и минут автономии фронт не считает. ИБП молчит (status stale/down) — как молчащий узел:
// плашка «ИБП молчит», значения C.textMuted, тревожные заливки сняты.

import { BatteryMedium } from 'lucide-react';
import { type CSSProperties } from 'react';
import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import { ICON_STROKE } from '../../components/ui/icons';
import type { HardwareUps, HwLevel } from './hardwareTypes';
import { ruNum, upsMetricGroups } from './hardwareFormat';
import { DisclosureSection } from '../../components/ui/DisclosureSection';
import { Sparkline, SparklinePlaceholder } from './Sparkline';

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
// (ватты — в полосе); молчит — «не отвечает с HH:MM:SS».
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

// Цвет значения по level: warn/danger окрашиваются, остальное нейтрально.
const VALUE_COLOR: Record<HwLevel, string> = {
  ok: C.textHeading, none: C.textHeading, warn: C.warningText, danger: C.dangerText,
};

// KPI «Заряд» (макет .kpi/.ups-kpi): bgCard, borderLight, R.xl, SP.md.
const KPI_BOX: CSSProperties = {
  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md,
};

// Полоса из трёх ячеек (макет .ups-cells): repeat(3, minmax(0,1fr)), bgCard,
// borderLight, R.lg; разделители ячеек — border-left borderLight.
const STRIP: CSSProperties = {
  display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', minWidth: 0, overflow: 'hidden',
  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
};

// Значение с единицей: null — «нет данных» (курсив, C.textMuted, FS.sm);
// 0 — измеренное значение, выводится «0 %» (по dec — с запятой).
// large — крупная KPI (макет .kpi-value 600 28px/1.15); иначе ячейка полосы
// (макет .ups-cell-value 600 16px/1.25). Размер KPI обязан быть крупнее ячейки.
function MetricValue({ v, unit, dec, color, large = false }: { v: number | null; unit: string; dec: boolean; color: string; large?: boolean }) {
  if (v == null) {
    return (
      <div style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600, lineHeight: 1.3, marginTop: 2, color: C.textMuted, fontStyle: 'italic' }}>
        нет данных
      </div>
    );
  }
  const fontSize = large ? FS.h1 : FS.lg;
  const lineHeight = large ? 1.15 : 1.25;
  return (
    <div style={{ fontFamily: FONT.mono, fontSize, fontWeight: 600, lineHeight, overflowWrap: 'anywhere', color }}>
      {`${dec ? ruNum(v) : Math.round(v)} ${unit}`}
    </div>
  );
}

// Ячейка полосы: подпись (FS.xxs C.textMuted), значение (mono FS.lg 600),
// деталь (FS.xxs C.textSecondary). Разделитель — border-left на соседних ячейках.
function UpsCell({ label, value, dec, unit, level, detail, silent }: {
  label: string; value: number | null; dec: boolean; unit: string; level: HwLevel | null; detail: string; silent: boolean;
}) {
  return (
    <div style={{ minWidth: 0, padding: SP.sm, borderLeft: `1px solid ${C.borderLight}` }}>
      <div style={{ fontSize: FS.xxs, color: C.textMuted }}>{label}</div>
      {/* level null — окраска не применяется: значение нейтрального цвета */}
      <MetricValue v={value} unit={unit} dec={dec} color={silent ? C.textMuted : level == null ? C.textHeading : VALUE_COLOR[level]} />
      {detail && <div style={{ fontSize: FS.xxs, color: C.textSecondary, marginTop: 2 }}>{detail}</div>}
    </div>
  );
}

// Компактный ИБП: шапка, сводка, одна KPI «Заряд», одна полоса (нагрузка, сеть, батарея).
// Данные — из upsMetricGroups; заливка KPI — только при lowBattery по powerLevel и
// только когда ИБП отвечает; при молчании значения C.textMuted, заливки сняты.
export function UpsCard(props: { ups: HardwareUps; isMobile: boolean }) {
  const { ups } = props;
  const silent = upsSilent(ups);
  const g = upsMetricGroups(ups);
  const low = ups.power === 'lowBattery' && !silent;
  const kpiLevel: HwLevel = silent ? 'none' : (ups.power === 'lowBattery' ? ups.powerLevel : 'ok');
  const [load, input, battery] = g.secondary;
  const pill = upsPill(ups);
  const summary = upsSummary(ups);
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
      {/* KPI «Заряд»: заливка (danger) только при lowBattery и живом ИБП */}
      <div style={{ ...KPI_BOX, marginBottom: SP.sm, background: low ? C.dangerBg : KPI_BOX.background, borderColor: low ? C.dangerBorder : C.borderLight }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary }}>
          <BatteryMedium size={14} strokeWidth={ICON_STROKE} aria-hidden />
          <span>Заряд</span>
        </div>
        <MetricValue v={g.primary.value} unit="%" dec={false} color={silent ? C.textMuted : VALUE_COLOR[kpiLevel]} large />
        <div style={{ fontSize: FS.xs, color: C.textSecondary, marginTop: 4 }}>{g.primary.detail}</div>
      </div>
      {/* Полоса из трёх ячеек: нагрузка, сеть, батарея */}
      <div style={STRIP}>
        <UpsCell label={load.name} value={load.value} dec={false} unit={load.unit} level={silent ? 'none' : load.level} detail={load.detail} silent={silent} />
        <UpsCell label={input.name} value={input.value} dec={false} unit={input.unit} level={silent ? 'none' : input.level} detail={input.detail} silent={silent} />
        <UpsCell label={battery.name} value={battery.value} dec={true} unit={battery.unit} level={silent ? 'none' : battery.level} detail={battery.detail} silent={silent} />
      </div>
    </section>
  );
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

// Секция «Динамика за 15 мин»: sibling карточки (div id="ups-dynamics"), штатный
// DisclosureSection «Динамика за 15 мин»; внутри четыре подписанных Sparkline
// в две колонки. Состояние раскрытия — у потребителя (HardwareTab, ST4): open/onToggle,
// своего useState нет. Ряды и деление десятых вольта — из upsMetricGroups (ST1).
export function UpsDynamicsSection({ ups, open, onToggle, isMobile }: {
  ups: HardwareUps; open: boolean; onToggle: () => void; isMobile: boolean;
}) {
  const silent = upsSilent(ups);
  const series = upsMetricGroups(ups).dynamics;
  // Молчит — линия C.textMuted вместо accent (макет: c = stale ? muted : accent).
  const lineColor = silent ? C.textMuted : C.accent;
  return (
    <div id="ups-dynamics">
      <DisclosureSection title="Динамика за 15 мин" meta="4 графика · 90 точек · шаг 10 с" open={open} onToggle={onToggle} isMobile={isMobile}>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: SP.sm, paddingTop: SP.md }}>
          {series.map(s => {
            const has = s.points.some(v => v != null);
            return (
              <div key={s.label} style={{ minWidth: 0 }}>
                <div style={{ fontSize: FS.xs, color: C.textSecondary }}>{s.label}</div>
                {has
                  ? <Sparkline points={s.points} color={lineColor} ariaLabel={`${s.label}, 15 минут`} />
                  : <SparklinePlaceholder />}
              </div>
            );
          })}
        </div>
      </DisclosureSection>
    </div>
  );
}
