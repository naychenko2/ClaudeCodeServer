// Карточка «Движок» (EngineCard) — детали узла на вкладке «Стенды» (макет v3, §«Движок узла»).
// Чистый компонент на пропсах: данных не грузит, типы берёт существующие (LocalModelStats,
// LocalModelMedia). vLLM: строки нагрузки + строка-причина падения скорости; ComfyUI: «В работе /
// в очереди». Нет значения — «нет данных» (курсив C.textMuted), не «0» и не прочерк;
// stats/media = null — всё тело карточки «нет данных» (заголовок остаётся: карточка опознаваема).
// stale — цифры приглушены, под заголовком строка «последние данные · HH:MM:SS».
// Строки kv — как в GpuDetailCard; заголовок — как заголовки секций «Стенд»/«Видеокарты»/«Температуры»;
// полоса KV-кэша — ui/ProgressBar.

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

// Строка-причины: thermal — продолжение фразы «Скорость упала» тревожным тоном (danger);
// powerCap — информационная плашка «Упирается в лимит мощности» (info): упор в лимит — не
// поломка, по решению дизайна (docs/mockups/local-model-hardware-spec.md:56, макеты v2/v3 «p-info»).
const THROTTLE: Record<'thermal' | 'powerCap', { text: string; bg: string; fg: string }> = {
  thermal: { text: 'Скорость упала: карта сбрасывает частоту от перегрева', bg: C.dangerBg, fg: C.dangerText },
  powerCap: { text: 'Упирается в лимит мощности', bg: C.infoBg, fg: C.info },
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
  // Имя движка/модели — в mono-спане без капса (как .nn в «Узел <ключ>»): капс исказил бы идентификатор.
  const name = props.kind === 'vllm' ? (props.title ? `vLLM · ${props.title}` : 'vLLM') : 'ComfyUI';
  // Причина относится к скорости генерации — метрики скорости есть только у vLLM.
  const th = props.kind === 'vllm' ? props.throttled ?? null : null;

  return (
    <div style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md, minWidth: 0 }}>
      {/* Заголовок — как секции «Стенд»/«Видеокарты»/«Температуры»: префикс капсом textSecondary, имя — mono-спан textHeading */}
      <h3 style={{ margin: `0 0 ${SP.sm}px`, fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: C.textSecondary, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        {'Движок · '}<span style={{ fontFamily: FONT.mono, textTransform: 'none', letterSpacing: 0, color: C.textHeading }}>{name}</span>
      </h3>

      {stale && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, margin: `0 0 ${SP.xs}px`, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          последние данные{props.since != null ? ` · ${clockHms(props.since)}` : ''}
        </div>
      )}

      {/* thermal — частоту реально сбросило: тревожная строка с глифом «↓». */}
      {th === 'thermal' && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: SP.xs, margin: `0 0 ${SP.xs}px`,
          background: THROTTLE.thermal.bg, color: THROTTLE.thermal.fg, borderRadius: R.lg,
          padding: `${SP.xs}px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, lineHeight: 1.35,
        }}>
          <span aria-hidden style={{ flexShrink: 0 }}>↓</span>
          <span>{THROTTLE.thermal.text}</span>
        </div>
      )}
      {/* powerCap — упор в лимит мощности это штатный режим, не поломка: пилюля info без глифа
          (R.max, как Badge в GpuDetailCard); «↓» читался бы как «стало хуже». */}
      {th === 'powerCap' && (
        <div style={{ display: 'flex', flexWrap: 'wrap', margin: `0 0 ${SP.xs}px` }}>
          <span style={{
            display: 'inline-flex', alignItems: 'center', background: THROTTLE.powerCap.bg, color: THROTTLE.powerCap.fg,
            borderRadius: R.max, padding: `2px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600,
            whiteSpace: 'normal', lineHeight: 1.3,
          }}>{THROTTLE.powerCap.text}</span>
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
