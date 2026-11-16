// Вкладка «Стенды» панели «Локальные модели» (только админ), макет А. Сверху вниз:
//   1. Карточка «Ферма» (FarmSummaryCard): все узлы, касание выбирает узел;
//   2. Баннер «Узел <key> не отвечает с HH:MM:SS» (узел молчит, данные последние);
//   3. Детали выбранного узла: Card «Стенд» (плитки, счётчик «N показателей без данных»,
//      вентиляторы), «Температуры», «Видеокарты», затем «Движок» (vLLM — если у узла есть
//      engines, ComfyUI — если media). Узел молчит и отчёта нет ни разу — «Нет связи с узлом».
//
// Макет: docs/mockups/local-model-hardware-cluster-v3.html. Окраска — по level (бэкенд);
// stale — значения приглушены (C.textMuted), точка на вкладке гаснет. Выбранный узел и
// опросы живут в LocalModelPanel (ему нужен ключ узла для запроса); вкладка — на пропсах.

import { C, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { AlertTriangle, Fan, WifiOff } from 'lucide-react';
import { EmptyState } from '../../components/ui/EmptyState';
import type { HardwareFarm, HardwareFarmNode, HardwareReport, HwLevel } from './hardwareTypes';
import {
  farmNodeSilent, hwFansLine, hwLevelLabel, hwNoDataCount, hwNoDataCountLabel, hwSummary,
  hwThrottled, hwTileView,
} from './hardwareFormat';
import { HwTile } from './HwTile';
import { TempChart } from './TempChart';
import { GpuDetailCard } from './GpuDetailCard';
import { FarmSummaryCard } from './FarmSummaryCard';
import { EngineCard, engineLine } from './EngineCard';
import type { LocalModelMedia, LocalModelStats } from './types';
import type { HardwareWindow } from './useLocalModelHardware';

// «HH:MM:SS» из unix-мс (баннер молчащего узла).
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

// Баннер молчащего узла: C.warningBg/C.warningText, R.lg.
function StaleBanner({ nodeKey, since }: { nodeKey: string; since?: number | null }) {
  const t = formatHms(since ?? undefined);
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
        <b style={{ fontWeight: 700 }}>Узел {nodeKey} не отвечает{t ? ` с ${t}` : ''}.</b>{' '}
        Показаны последние данные — они могут быть неверны.
      </div>
    </div>
  );
}

// Карточка «Стенд»: плашка уровня, сводка, сетка плиток, вентиляторы.
function StandCard({ report, isMobile }: { report: HardwareReport; isMobile: boolean }) {
  const stale = report.status === 'stale';
  const views = hwTileView(report);
  const noData = hwNoDataCount(report);
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
      {/* Плитки «датчик есть, но не ответил»; «датчика нет» в счётчик не входит */}
      {noData > 0 && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, fontStyle: 'italic', marginTop: SP.sm }}>
          {hwNoDataCountLabel(noData)}
        </div>
      )}
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

// Строка движков узла для карточки сводки: «12 ток/с · KV 40 %» (vLLM) и «очередь 0 · идёт 1»
// (ComfyUI); оба — через « / ». Данные есть только у выбранного узла (опрос один), у прочих
// строки нет. undefined — у узла нет ни движка, ни медиа.
export function nodeEngineLine(
  node: HardwareFarmNode, stats: LocalModelStats | null, media: LocalModelMedia | null,
): string | undefined {
  const parts: string[] = [];
  if (node.engines.length > 0) parts.push(engineLine({ kind: 'vllm', title: stats?.model ?? '', stats }));
  if (node.media) parts.push(engineLine({ kind: 'comfy', media }));
  return parts.length ? parts.join(' / ') : undefined;
}

// Узел молчит и отчёта по нему нет ни разу: показывать нечего, кроме причины.
function hasNoReport(report: HardwareReport | null): boolean {
  return report == null || (report.nodes.length === 0 && report.gpus.length === 0);
}

// === Вкладка ===
export function HardwareTab({
  farm, farmLoading, selected, onSelect, report, loading, stats, media, isMobile, window, onWindowChange,
}: {
  farm: HardwareFarm | null;
  farmLoading: boolean;
  // Ключ выбранного узла (farmPickNode в панели); null — фермы ещё нет.
  selected: string | null;
  onSelect: (key: string) => void;
  // Отчёт выбранного узла; null — ещё не пришёл (или смена узла сбросила снимок).
  report: HardwareReport | null;
  loading: boolean;
  // Статистика движка выбранного узла (первый ключ node.engines) и медиа узла.
  stats: LocalModelStats | null;
  media: LocalModelMedia | null;
  isMobile: boolean;
  window: HardwareWindow;
  onWindowChange: (w: HardwareWindow) => void;
}) {
  const node = farm?.farmNodes.find(n => n.key === selected) ?? null;
  // Первое открытие до первого ответа — скелетоны
  if (!farm && !report) return loading || farmLoading ? <HardwareTabSkeleton /> : null;

  const silent = node != null && farmNodeSilent(node);
  const down = node != null && silent && node.status !== 'incompatible';
  const reportStale = report?.status === 'stale';
  const stale = silent || reportStale;
  const since = node?.since ?? report?.staleSince ?? null;
  const bannerKey = node?.key ?? report?.node?.key ?? '';
  const engLine = node ? nodeEngineLine(node, stats, media) : undefined;
  const engineLines: Record<string, string> | undefined = node && engLine ? { [node.key]: engLine } : undefined;
  const showBanner = (down || reportStale) && !(down && hasNoReport(report)) && bannerKey !== '';

  let details: React.ReactNode;
  if (down && hasNoReport(report)) {
    // Узел молчит, и отчёта не было ни разу: вместо пустых плиток — причина.
    details = (
      <EmptyState
        compact inline
        icon={<WifiOff size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
        title={`Нет связи с узлом ${node!.title}`}
        subtitle="Данных по узлу пока не было — они появятся, когда узел ответит."
      />
    );
  } else if (!report) {
    details = <HardwareTabSkeleton />;
  } else {
    const engines = node ? (
      <>
        {node.engines.length > 0 && (
          <EngineCard kind="vllm" title={stats?.model ?? ''} stats={stats}
            throttled={hwThrottled(report)} stale={stale} since={since ?? undefined} />
        )}
        {node.media && (
          <EngineCard kind="comfy" media={media} stale={stale} since={since ?? undefined} />
        )}
      </>
    ) : null;
    details = (
      <>
        <StandCard report={report} isMobile={isMobile} />
        <TempChart report={report} window={window} onWindowChange={onWindowChange} isMobile={isMobile} />
        <GpusCard report={report} isMobile={isMobile} />
        {engines}
      </>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {farm && <FarmSummaryCard farm={farm} selected={selected} onSelect={onSelect} isMobile={isMobile} engineLines={engineLines} />}
      {showBanner && <StaleBanner nodeKey={bannerKey} since={since} />}
      {details}
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
