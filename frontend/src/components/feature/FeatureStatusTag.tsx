// Статусный тег фичи (шапка страницы и view исполнения): единая точка правды — run.state
// (11 значений, бэк Б1 + W4-HOLD-B1): из него бэк выводит bucket строки индекса и порядок;
// тег с ними не расходится.
//
// W5-F3a: модуль выделен из FeaturePageHeader.tsx по хвосту ревью W5-R-F1 (п.9): «чистая»
// модель строки анализа тянула featureRunStatusTag из модуля шапки, и цикл
// FeaturePageHeader ⇄ FeatureAnalysisModel замыкался. Шапка и строка анализа зовут тег
// отсюда; самого тега в шапке больше нет.
//
// Таблица макета — 8 подписей: `idle` («ничего не идёт, не ждёт, не закрыта»)
// показывается как `not_started` — для пользователя фича ещё не началась.
// `stageName` — имя активной стадии (тег «идёт · {этап}»); null — просто «идёт».
// Тон `loop` («Доработка») run.state не возвращает — только фолбэк по индексу.
// `paused`/`draft` (W4-HOLD-F2, feature-hold-1.md §«Теги»): при паузе тон активной
// стадии на бэке остаётся «run» — тег обязан показать «на паузе», а не «идёт»
// (пункт вердикта ревью); тон hold — bgInset + кромка border, НЕ тревожный.

import type { CSSProperties } from 'react';
import { Pause } from 'lucide-react';
import { C, R, SP, FS, FONT } from '../../lib/design';
import { ICON_STROKE } from '../ui/icons';
import { MERGE_PENDING_STATUS, type FeatureStatusTag } from '../../lib/chatGroups';
import type { FeatureQueue, FeatureState } from '../../types/featureRun';

export type FeatureRunTagTone = 'notStarted' | 'run' | 'wait' | 'stop' | 'merge' | 'loop' | 'merged' | 'closed' | 'hold' | 'draft';
export interface FeatureRunTag {
  label: string;
  tone: FeatureRunTagTone;
  // Точка (C.info) у «ждёт вас» / «ждёт согласия на мерж».
  dot?: boolean;
}

// Тег для состояния вне словаря (бэк опередил фронт).
export const UNKNOWN_RUN_TAG: FeatureRunTag = { label: 'статус неизвестен', tone: 'notStarted' };

// Удержание для подписи паузы: причина и место в очереди (позицию считает бэк).
export interface FeatureHoldInfo {
  holdReason?: string | null;
  queue?: FeatureQueue | null;
}

// «в очереди · 2 из 3»; без queue (бэк ещё не отдал) — просто «в очереди», нулей не показываем.
function queueLabel(queue?: FeatureQueue | null): string {
  return queue && queue.position > 0 && queue.length > 0
    ? `в очереди · ${queue.position} из ${queue.length}`
    : 'в очереди';
}

export function featureRunStatusTag(state: FeatureState, stageName?: string | null, hold?: FeatureHoldInfo): FeatureRunTag {
  switch (state) {
    case 'not_started':
    case 'idle':
      return { label: 'не начата', tone: 'notStarted' };
    case 'running':
      return { label: stageName ? `идёт · ${stageName}` : 'идёт', tone: 'run' };
    case 'waiting':
      return { label: 'ждёт вас', tone: 'wait', dot: true };
    case 'stopped':
      return { label: 'стоит', tone: 'stop' };
    case 'paused':
      // Тон активной стадии при паузе на бэке остаётся «run» — stageName сюда не
      // подмешиваем: при паузе нет «идёт», есть «на паузе» (вердикт ревью W4-HOLD-F2).
      if (hold?.holdReason === 'queue') return { label: queueLabel(hold.queue), tone: 'hold' };
      return { label: 'на паузе', tone: 'hold' };
    case 'draft':
      return { label: 'черновик', tone: 'draft' };
    case 'merge_pending':
      return { label: MERGE_PENDING_STATUS, tone: 'merge', dot: true };
    case 'merging':
      return { label: 'мерж · идёт', tone: 'run' };
    case 'merged':
      return { label: 'смержена', tone: 'merged' };
    case 'closed':
      return { label: 'закрыта без мержа', tone: 'closed' };
    default:
      // Состояние вне словаря (бэк опередил фронт) — нейтральный тег, а не undefined.
      return UNKNOWN_RUN_TAG;
  }
}

// Старый тег индекса (featureStatusTag, 7 тонов) → тег страницы: тоны совпадают
// один в один (loop — только фолбэк по индексу, run.state его не возвращает).
export function toRunTag(t: FeatureStatusTag): FeatureRunTag {
  return { label: t.label, tone: t.tone as FeatureRunTagTone };
}

// Тон → вид (макет §3): не начата/закрыта — bgSelected/textSecondary; идёт —
// bgSelected/textHeading; ждёт/мерж — infoBg/textPrimary (+точка); стоит —
// dangerBg/dangerText + inset-кромка; доработка — warningBg/warningText;
// смержена — successBg/successText. «На паузе» — bgInset + inset-кромка border
// (плашка «утоплена», не тревожная); «черновик» — прозрачный фон, пунктир
// textMuted (не C.dashed: в тёмной теме сливается с фоном, замечание M1).
const TAG_STYLE: Record<FeatureRunTagTone, CSSProperties> = {
  notStarted: { background: C.bgSelected, color: C.textSecondary },
  run:      { background: C.bgSelected, color: C.textHeading },
  wait:     { background: C.infoBg, color: C.textPrimary },
  stop:     { background: C.dangerBg, color: C.dangerText, boxShadow: `inset 0 0 0 1px ${C.danger}` },
  merge:    { background: C.infoBg, color: C.textPrimary },
  loop:     { background: C.warningBg, color: C.warningText },
  merged:   { background: C.successBg, color: C.successText },
  closed:   { background: C.bgSelected, color: C.textSecondary },
  hold:     { background: C.bgInset, color: C.textPrimary, boxShadow: `inset 0 0 0 1px ${C.border}` },
  draft:    { background: 'transparent', color: C.textSecondary, border: `1px dashed ${C.textMuted}` },
};

// Тег очереди — «в очереди» или «в очереди · N из M»; признак читаем по подписи,
// чтобы не расширять контракт FeatureRunTag (его сверяют toEqual в соседних тестах).
const QUEUE_LABEL = /^в очереди(?: · (\d+ из \d+))?$/;

// Глиф очереди (макет feature-queue-1, «Тег и глиф»): три полосы 12×2.4, rx 1.2,
// по y 2.5 / 6.8 / 11.1; currentColor и opacity — на самих полосах, не на обёртке.
function QueueGlyph() {
  return (
    <svg data-testid="queue-glyph" aria-hidden width={10} height={10} viewBox="0 0 14 14" style={{ flexShrink: 0 }}>
      <rect x={1} y={2.5} width={12} height={2.4} rx={1.2} fill="currentColor" />
      <rect x={1} y={6.8} width={12} height={2.4} rx={1.2} fill="currentColor" opacity={0.55} />
      <rect x={1} y={11.1} width={12} height={2.4} rx={1.2} fill="currentColor" opacity={0.55} />
    </svg>
  );
}

// Тег: 11/600, поля 1 7, R.sm, строка 16 (макет §1). Точка — у «ждёт вас»/«к мержу».
// «На паузе» — глиф «‖» 10 px currentColor перед подписью (макет feature-hold-1);
// «черновик» — поля 0 6: пунктирная рамка занимает место, её компенсируют полем.
export function StatusTag({ tag, testId }: { tag: FeatureRunTag; testId?: string }) {
  const queue = tag.tone === 'hold' ? QUEUE_LABEL.exec(tag.label) : null;
  return (
    <span
      data-testid={testId}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0,
        fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans, lineHeight: '16px',
        ...(tag.tone === 'draft' ? { padding: `0 6px` } : { padding: `${SP.xxs}px ${SP.xs}px` }),
        borderRadius: R.sm, whiteSpace: 'nowrap',
        ...TAG_STYLE[tag.tone],
      }}
    >
      {queue && <QueueGlyph />}
      {tag.tone === 'hold' && !queue && (
        <span aria-hidden style={{ display: 'inline-flex', color: 'currentColor' }}>
          <Pause size={10} strokeWidth={ICON_STROKE} />
        </span>
      )}
      {tag.dot && (
        <span aria-hidden style={{ width: 6, height: 6, borderRadius: R.full, background: C.info, flexShrink: 0 }} />
      )}
      {queue && queue[1] ? (
        <>
          в очереди ·{' '}
          <span data-testid="queue-nums" style={{ fontFamily: FONT.mono }}>{queue[1]}</span>
        </>
      ) : tag.label}
    </span>
  );
}
