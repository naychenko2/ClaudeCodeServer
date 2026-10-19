// Вкладка «Железо» панели «Локальные модели» (только админ). Сверху вниз:
//   1. Баннер «Стенд не отвечает» (status='stale');
//   2. Card «Стенд»: плашка уровня, строка-сводка, сетка плиток HwTile, строка вентиляторов;
//   3. Card «Температуры»: окна, чипы семейств, линейный график, легенда;
//   4. Card «Видеокарты» («Видеокарта», если одна): подробные карточки GpuDetailCard.
//
// Макет: docs/mockups/local-model-hardware-v2.html. Окраска — по level (бэкенд);
// stale — значения приглушены (C.textMuted), точка на вкладке гаснет.

import { C, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { AlertTriangle, Fan } from 'lucide-react';
import type { HardwareReport, HwLevel } from './hardwareTypes';
import { hwFansLine, hwLevelLabel, hwSummary, hwTileView } from './hardwareFormat';
import { HwTile } from './HwTile';
import { TempChart } from './TempChart';
import { GpuDetailCard } from './GpuDetailCard';
import type { HardwareWindow } from './useLocalModelHardware';

// «HH:MM:SS» из unix-мс (баннер «Стенд не отвечает с …»).
function formatHms(ms?: number): string {
  if (ms == null || !Number.isFinite(ms)) return '';
  const d = new Date(ms);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

// Плашка уровня стенда: ok → muted, warn → warning, danger → danger.
function LevelPill({ level }: { level: HwLevel }) {
  if (level === 'none') return null;
  const bg = level === 'danger' ? C.dangerBg : level === 'warn' ? C.warningBg : C.bgInset;
  const fg = level === 'danger' ? C.dangerText : level === 'warn' ? C.warningText : C.textSecondary;
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', background: bg, color: fg,
      borderRadius: R.max, padding: `1px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
    }}>{hwLevelLabel(level)}</span>
  );
}

// Баннер «Стенд не отвечает»: C.warningBg/C.warningText, R.lg.
function StaleBanner({ since }: { since?: number }) {
  const t = formatHms(since);
  return (
    <div style={{
      display: 'flex', gap: SP.sm, alignItems: 'flex-start',
      background: C.warningBg, color: C.warningText, borderRadius: R.lg,
      padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm, lineHeight: 1.45,
    }}>
      <span style={{ flexShrink: 0, marginTop: 1 }}>
        <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </span>
      <div style={{ minWidth: 0 }}>
        <b style={{ fontWeight: 700 }}>Стенд не отвечает{t ? ` с ${t}` : ''}.</b>{' '}
        Показаны последние данные — они могут быть неверны.
      </div>
    </div>
  );
}

// Карточка «Стенд»: плашка уровня, сводка, сетка плиток, вентиляторы.
function StandCard({ report, isMobile }: { report: HardwareReport; isMobile: boolean }) {
  const stale = report.status === 'stale';
  const views = hwTileView(report);
  // Мобила (320): плитки в 2 колонки; десктоп — auto-fit minmax(130px,1fr).
  const gridCols = isMobile ? 'repeat(2, 1fr)' : 'repeat(auto-fit, minmax(130px, 1fr))';
  return (
    <section style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 style={{ margin: 0, fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: C.textSecondary, flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          Стенд
        </h3>
        <LevelPill level={report.level} />
      </div>
      {/* Строка-сводка */}
      <div style={{ fontSize: FS.sm, color: C.textSecondary, margin: `-2px 0 ${SP.sm}px` }}>
        {hwSummary(report)}
      </div>
      {/* Сетка плиток */}
      <div style={{ display: 'grid', gridTemplateColumns: gridCols, gap: SP.sm }}>
        {views.map(v => <HwTile key={v.key} view={v} stale={stale} />)}
      </div>
      {/* Вентиляторы корпуса */}
      {report.caseFans.length > 0 && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.xs, color: C.textSecondary, marginTop: SP.sm, flexWrap: 'wrap' }}>
          <Fan size={14} strokeWidth={ICON_STROKE} color={C.textMuted} />
          <span>{hwFansLine(report.caseFans)}</span>
        </div>
      )}
    </section>
  );
}

// Карточка «Видеокарты» (или «Видеокарта», если одна): подробные карточки.
function GpusCard({ report, isMobile }: { report: HardwareReport; isMobile: boolean }) {
  const n = report.gpus.length;
  if (n === 0) return null;
  const cols = isMobile ? '1fr' : 'repeat(auto-fit, minmax(160px, 1fr))';
  return (
    <section style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 style={{ margin: 0, fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: C.textSecondary, flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {n > 1 ? 'Видеокарты' : 'Видеокарта'}
        </h3>
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: cols, gap: SP.sm }}>
        {report.gpus.map(g => <GpuDetailCard key={g.uuid || g.index} gpu={g} total={n} />)}
      </div>
    </section>
  );
}

// === Вкладка ===
export function HardwareTab({ data, loading, isMobile, window, onWindowChange }: {
  data: HardwareReport | null;
  loading: boolean;
  isMobile: boolean;
  window: HardwareWindow;
  onWindowChange: (w: HardwareWindow) => void;
}) {
  // Первое открытие до первого ответа — скелетоны
  if (loading && !data) return <HardwareTabSkeleton />;
  if (!data) return null;

  const stale = data.status === 'stale';
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {stale && <StaleBanner since={data.staleSince} />}
      <StandCard report={data} isMobile={isMobile} />
      <TempChart report={data} window={window} onWindowChange={onWindowChange} isMobile={isMobile} />
      <GpusCard report={data} isMobile={isMobile} />
    </div>
  );
}

// Загрузка: скелетоны (C.bgInset, пульс; reduced-motion — статично).
function HardwareTabSkeleton() {
  const reduced = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;
  const pulse: React.CSSProperties = reduced ? {} : { animation: 'ccs-lm-skeleton 1.4s ease-in-out infinite' };
  const block = (h: number): React.CSSProperties => ({ background: C.bgInset, borderRadius: R.xl, height: h, ...pulse });
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }} aria-hidden>
      <div style={block(120)} />
      <div style={block(160)} />
      <div style={block(180)} />
    </div>
  );
}
