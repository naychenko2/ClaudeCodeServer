// Карточка «Движок» (EngineCard) — детали узла на вкладке «Стенды» (макет v3, §«Движок узла»).
// Чистый компонент на пропсах: данных не грузит, типы берёт существующие (LocalModelStats,
// LocalModelMedia). vLLM: строки нагрузки + строка-причина падения скорости; ComfyUI: «В работе /
// в очереди». Нет значения — «нет данных» (курсив C.textMuted), не «0» и не прочерк;
// stats/media = null — всё тело карточки «нет данных» (заголовок остаётся: карточка опознаваема).
// stale — цифры приглушены, под заголовком строка «последние данные · HH:MM:SS».
// Строки kv и шапка карточки — как в GpuDetailCard; полоса KV-кэша — ui/ProgressBar.

import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import { ProgressBar } from '../../components/ui/ProgressBar';
import type { LocalModelMedia, LocalModelStats } from './types';

// Причина падения скорости по данным узла: thermal — карта сбрасывает частоту от перегрева,
// powerCap — карта упёрлась в лимит мощности. null — строки-причины нет.
export type EngineThrottle = 'thermal' | 'powerCap' | null;

export type EngineCardProps = {
  // Строка-причина в теле карточки — только у vLLM: у ComfyUI метрики скорости нет.
  throttled?: EngineThrottle;
  // true — данные несвежие: цифры приглушены + строка «последние данные · HH:MM:SS».
  stale?: boolean;
  // unix-мс последних данных (время подписи при stale)
  since?: number;
} & (
  | { kind: 'vllm'; title: string; stats: LocalModelStats | null }
  | { kind: 'comfy'; media: LocalModelMedia | null }
);

const NO_DATA = 'нет данных';

// Доля 0..1 → целые проценты (0.42 → 42); speed — целые ток/с (71.4 → 71).
const pct = (v: number) => Math.round(v * 100);
const tps = (v: number) => String(Math.round(v));

// Значение строки: число (mono 600) или «нет данных» (курсив, C.textMuted) — как .na в макете.
// stale приглушивает настоящее значение; «нет данных» приглушено и так.
function Val({ v, stale }: { v: string | null; stale: boolean }) {
  if (v == null) return <span style={{ color: C.textMuted, fontStyle: 'italic' }}>{NO_DATA}</span>;
  return <span style={{ fontFamily: FONT.mono, fontWeight: 600, color: stale ? C.textMuted : C.textPrimary }}>{v}</span>;
}

// Строка «ключ — значение» — тот же вид, что у GpuDetailCard.
function Kv({ k, v, stale }: { k: string; v: string | null; stale: boolean }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.xs, fontSize: FS.xs, padding: '1px 0', minWidth: 0 }}>
      <span style={{ color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{k}</span>
      <Val v={v} stale={stale} />
    </div>
  );
}

// Всё тело карточки без данных: та же формулировка, что у отдельного пропуска.
function NoBody() {
  return <div style={{ fontSize: FS.xs, fontStyle: 'italic', color: C.textMuted, padding: `${SP.xs}px 0` }}>{NO_DATA}</div>;
}

// Текст строки-причины. Оба варианта — продолжение одной фразы («Скорость упала: …»),
// разные тона: thermal — тревожный (danger), powerCap — предупредительный (warning).
const THROTTLE: Record<'thermal' | 'powerCap', { text: string; bg: string; fg: string }> = {
  thermal: { text: 'Скорость упала: карта сбрасывает частоту от перегрева', bg: C.dangerBg, fg: C.dangerText },
  powerCap: { text: 'Скорость упала: карта упёрлась в лимит мощности', bg: C.warningBg, fg: C.warningText },
};

// HH:MM:SS локальных часов узла (снимок чужих часов — ложная точность, как в «Дежурстве»).
function clockHms(since: number): string {
  const d = new Date(since);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

function VllmBody({ stats, stale }: { stats: LocalModelStats; stale: boolean }) {
  const run = stats.running;
  const wait = stats.waiting;
  const usage = stats.kv?.usagePerc;
  const prefix = stats.kv?.prefixHitPerc;
  const gen = stats.speed?.genPerStream;
  const preempt = stats.kv?.preemptions15m;
  return (
    <>
      {/* Парная строка не делима: без одной из половин состав не собрать — «нет данных» целиком */}
      <Kv k="В работе / в очереди" v={run != null && wait != null ? `${run} / ${wait}` : null} stale={stale} />
      <Kv k="KV-кэш" v={usage != null ? `${pct(usage)} %` : null} stale={stale} />
      {usage != null && (
        <ProgressBar value={pct(usage)} label="Занятость KV-кэша" style={{ margin: `0 0 ${SP.xs}px` }} />
      )}
      <Kv k="Префикс-кэш, попадания" v={prefix != null ? `${pct(prefix)} %` : null} stale={stale} />
      <Kv k="Скорость, ток/с" v={gen != null ? tps(gen) : null} stale={stale} />
      <Kv k="Вытеснения за 15 мин" v={preempt != null ? String(preempt) : null} stale={stale} />
    </>
  );
}

function ComfyBody({ media, stale }: { media: LocalModelMedia; stale: boolean }) {
  // Списки прогонов — обязательное поле ответа: пустой список это ноль, а не «нет данных».
  return <Kv k="В работе / в очереди" v={`${media.running.length} / ${media.queue.length}`} stale={stale} />;
}

export function EngineCard(props: EngineCardProps) {
  const stale = props.stale ?? false;
  const title = props.kind === 'vllm' && props.title
    ? `Движок · vLLM · ${props.title}`
    : props.kind === 'vllm' ? 'Движок · vLLM' : 'Движок · ComfyUI';
  // Причина относится к скорости генерации — метрики скорости есть только у vLLM.
  const th = props.kind === 'vllm' ? props.throttled ?? null : null;

  return (
    <div style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: SP.sm, minWidth: 0 }}>
      <div style={{ fontSize: FS.sm, fontWeight: 700, color: C.textHeading, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {title}
      </div>

      {stale && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, margin: `0 0 ${SP.xs}px`, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          последние данные{props.since != null ? ` · ${clockHms(props.since)}` : ''}
        </div>
      )}

      {th && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: SP.xs, margin: `0 0 ${SP.xs}px`,
          background: THROTTLE[th].bg, color: THROTTLE[th].fg, borderRadius: R.lg,
          padding: `${SP.xs}px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, lineHeight: 1.35,
        }}>
          <span aria-hidden style={{ flexShrink: 0 }}>↓</span>
          <span>{THROTTLE[th].text}</span>
        </div>
      )}

      {props.kind === 'vllm'
        ? (props.stats == null ? <NoBody /> : <VllmBody stats={props.stats} stale={stale} />)
        : (props.media == null ? <NoBody /> : <ComfyBody media={props.media} stale={stale} />)}
    </div>
  );
}

// Одна строка движка для карточки узла в сводке (макет engLine): vLLM «N ток/с · KV M %»,
// ComfyUI «очередь N · идёт M». Нет данных для строки — та же формулировка «нет данных».
export function engineLine(props: EngineCardProps): string {
  if (props.kind === 'vllm') {
    const gen = props.stats?.speed?.genPerStream;
    const usage = props.stats?.kv?.usagePerc;
    if (gen == null || usage == null) return NO_DATA;
    return `${tps(gen)} ток/с · KV ${pct(usage)} %`;
  }
  const m = props.media;
  if (m == null) return NO_DATA;
  return `очередь ${m.queue.length} · идёт ${m.running.length}`;
}
