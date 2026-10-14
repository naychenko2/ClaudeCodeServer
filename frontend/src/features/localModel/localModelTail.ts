// Хвост кольцевой пилюли Ctx для чата на локальной модели (флаг local-model-stats).
// Чистая функция «что писать в хвосте»: по состоянию useLocalModelLive → текст + тон.
//
// Правила (макет local-model-stats-chip.html, раздел «Чип Ctx: десктоп»):
//  • генерация со скоростью > 0 — «38 ток/с»;
//  • генерация БЕЗ первого токена (0/нет) и чтение промпта — «читает…» (0 ток/с не
//    показываем никогда);
//  • очередь — «в очереди · ждут N» (N = waitingTotal, общее число, НЕ позиция хода);
//    при 0/нет данных — просто «в очереди»;
//  • после хода (idle) — скорость последнего хода, приглушённо (dim).
// null — хвоста нет (чат не на локальной модели, или idle без скорости последнего хода).
//
// Компактность (мобила, вариант А): короткие формы ≤ 7 знаков — «38 т/с», «читает»,
// «очередь»; после хода (idle) хвоста нет вовсе (лицо возвращается к «Ctx 61k»).

import type { LocalModelLiveState } from './useLocalModelLive';

export type LocalModelTailTone = 'normal' | 'warn' | 'dim';
export interface LocalModelTail {
  text: string;
  tone: LocalModelTailTone;
}

// «38 ток/с» — целое, без десятичных (движок целыми и отсчитывает).
// ≥ 1000 — «1,2k ток/с» (десктоп) / «1,2k т/с» (компакт), как в макете.
export function formatTps(v: number, compact?: boolean): string {
  const n = Math.round(v);
  if (n >= 1000) {
    const k = (n / 1000).toFixed(1).replace('.', ',') + 'k';
    return compact ? `${k} т/с` : `${k} ток/с`;
  }
  return compact ? `${n} т/с` : `${n} ток/с`;
}

// «Ожидание ≈ N с»: время в очереди (мс → сек). < 500 мс округляется к «≈ 1 с»,
// а не «≈ 0 с» (Math.max(1, …)); null/нет данных — «—» (как в поповере).
export function localModelQueueText(queueMs: number | null): string {
  if (queueMs == null) return '—';
  return `≈ ${Math.max(1, Math.round(queueMs / 1000))} с`;
}

export function localModelTail(state: LocalModelLiveState | null, compact?: boolean): LocalModelTail | null {
  if (!state) return null;
  switch (state.phase) {
    case 'generating':
      // Пока нет первого токена (0/нет скорости) — «читает…», а не «0 ток/с»
      if (state.genTps != null && state.genTps > 0) return { text: formatTps(state.genTps, compact), tone: 'normal' };
      return { text: compact ? 'читает' : 'читает…', tone: 'normal' };
    case 'prefill':
      return { text: compact ? 'читает' : 'читает…', tone: 'normal' };
    case 'queued':
      if (compact) return { text: 'очередь', tone: 'warn' };
      return state.waitingTotal != null && state.waitingTotal > 0
        ? { text: `в очереди · ждут ${state.waitingTotal}`, tone: 'warn' }
        : { text: 'в очереди', tone: 'warn' };
    case 'idle':
      // После хода: скорость последнего хода, приглушённо; нет скорости — хвоста нет.
      // На мобиле (compact) после хода хвоста нет вовсе — лицо возвращается к «Ctx 61k».
      if (compact) return null;
      if (state.lastTurn?.genTps != null && state.lastTurn.genTps > 0)
        return { text: formatTps(state.lastTurn.genTps), tone: 'dim' };
      return null;
    default:
      return null;
  }
}
