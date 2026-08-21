// Поточная озвучка хода: переходы состояний стрима — чистая часть эффекта из
// ChatPanel. Компонентных тестов в репе нет, поэтому логика хода (дельты → куски →
// хвост на result; сброс на прерывании/ошибке/новом ходе) вынесена сюда и покрыта
// юнитами напрямую.

import { takeSpeakableChunk, sanitizeForSpeech } from './tts';
import { turnAlreadyEnded } from './chatReducer';
import type { ChatItem } from '../types';

// Состояние стрима одного хода. Владелец — ChatPanel, держится в refs (мутации
// эффектом, без ререндеров на каждую дельту).
export interface TurnStreamState {
  cursor: number; // курсор резки нарастающего текста хода
  off: boolean;   // стриминг выключен на этом ходу (hitMarkup — код/таблица)
}

export const TURN_STREAM_INIT: TurnStreamState = { cursor: 0, off: false };

// Дельты: нарезать нарастающий текст хода на куски. Возвращает отданные куски и
// новый курсор (сам ставит их в очередь только при заданном enqueue — так юнит-тест
// видит состав кусков без моков аудио), plus флаги «ход закрыт маркером конца»
// и «стриминг выключился по разметке». tool_use/thinking_delta сюда не приходят
// вовсе — курсор абсолютный, конкатенация текста продолжается сама.
export function turnStreamChunks(
  st: TurnStreamState,
  items: ChatItem[],
): { chunks: string[]; cursor: number; off: boolean; ended: boolean } {
  if (st.off) return { chunks: [], cursor: st.cursor, off: true, ended: false };
  if (turnAlreadyEnded(items)) return { chunks: [], cursor: st.cursor, off: false, ended: true };
  const text = turnText(items);
  const chunks: string[] = [];
  let cursor = st.cursor;
  for (;;) {
    const r = takeSpeakableChunk(text, cursor);
    if (r.hitMarkup) return { chunks, cursor, off: true, ended: false };
    if (!r.chunk) break;
    chunks.push(r.chunk);
    cursor = r.cursor;
  }
  return { chunks, cursor, off: false, ended: false };
}

// Result: хвост ВСЕГДА, даже если cursor не двигался (короткий ответ без точек —
// весь текст одним куском). Санитайзер вырежет разметку — ветка hitMarkup закрывает
// ход тем же путём, что и обычная.
export function turnStreamTail(st: TurnStreamState, items: ChatItem[]): string {
  return sanitizeForSpeech(turnText(items).slice(st.cursor));
}

// Текст последнего ответа: все text-элементы текущего хода, без реплик сабагентов
// (parentToolUseId). «Текст хода» — конкатенация: после tool_use text_delta открывает
// НОВЫЙ элемент, но для озвучки это один непрерывный поток.
// Якорь хода — последний user_message (не result): на реплике второго хода result'а
// нового ещё нет, и формула «после последнего result» возвращала текст ПРОШЛОГО
// хода — стриминг озвучивал старый ответ первой фразой ещё до дельт нового.
// Чьим голосом читать ход: персона последней реплики хода, иначе собеседник чата.
//
// Ход читается ОДНИМ голосом, даже если в групповом чате в нём говорили разные персоны.
// Причина не в лени: пакеты уходят на синтез заранее (prefetch в startStreamSpeak), и
// смена голоса посреди хода означала бы выбросить уже оплаченный пакет. Нарезка озвучки
// по говорящему — отдельная задача, не здесь.
export function turnVoicePersonaId(items: ChatItem[], chatPersonaId?: string | null): string | undefined {
  const lastUm = items.map((it, i) => ({ it, i })).filter(x => x.it.kind === 'user_message').at(-1)?.i;
  const turn = lastUm === undefined ? [] : items.slice(lastUm + 1);
  const spoken = turn
    .filter(it => it.kind === 'text' && !it.parentToolUseId && it.personaId)
    .at(-1);
  return (spoken?.kind === 'text' ? spoken.personaId : undefined) ?? chatPersonaId ?? undefined;
}

export function turnText(items: ChatItem[]): string {
  // Один якорь для живого и завершённого хода — последний user_message (как у
  // turnAlreadyEnded). Всё после него и до следующей реплики — текущий ход:
  // text-элементы конкатенируются, result/маркеры texts() игнорирует. Реплики
  // нет вовсе (снимок истории без хода) — пусто
  const lastUm = items.map((it, i) => ({ it, i })).filter(x => x.it.kind === 'user_message').at(-1)?.i;
  if (lastUm === undefined) return '';
  return items
    .slice(lastUm + 1)
    .flatMap(it => (it.kind === 'text' && !it.parentToolUseId ? [it.text] : []))
    .join('\n')
    .trim();
}
