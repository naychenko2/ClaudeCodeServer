// Поле «Кусок» панели (обрезка без ИИ и «Перегенерировать кусок») и выделение на волне карточки
// нити — одно значение в сторе (getSelection/setSelection): кто бы ни правил, другой видит то же.
// Поле держит черновик строкой — «1:» пока набирают ещё не число, — а в стор уходит только
// разобранное значение.

import { TO_END, type AudioSelection } from '../player/selection';
import { getSelection, setSelection, type ThreadSelection } from '../thread/threadStore';

// «1:06.2» → 66.2; пусто или мусор — null
export function parseSec(s: string): number | null {
  const t = s.trim().replace(',', '.');
  if (!t) return null;
  const m = /^(\d+):(\d+(?:\.\d+)?)$/.exec(t);
  const n = m ? Number(m[1]) * 60 + Number(m[2]) : Number(t);
  return Number.isFinite(n) && n >= 0 ? n : null;
}

const show = (v: number) => String(Math.round(v * 10) / 10);

export interface PieceText { start: string; end: string; toEnd: boolean }

export const EMPTY_PIECE: PieceText = { start: '', end: '', toEnd: false };

export function pieceText(sel: AudioSelection | null): PieceText {
  if (!sel) return EMPTY_PIECE;
  return sel.end === TO_END ? { start: show(sel.start), end: '', toEnd: true } : { start: show(sel.start), end: show(sel.end), toEnd: false };
}

// Кусок из полей: без конца или с «до конца» — до конца версии; без начала — с нуля; конец не
// позже начала — куска нет. undefined — в полях недобор (набирают), стор не трогаем
export function pieceFromText(t: PieceText): AudioSelection | null | undefined {
  const blankStart = !t.start.trim();
  const blankEnd = !t.end.trim();
  if (blankStart && blankEnd && !t.toEnd) return null;
  const start = blankStart ? 0 : parseSec(t.start);
  const end = t.toEnd || blankEnd ? TO_END : parseSec(t.end);
  if (start === null || end === null) return undefined;
  if (end !== TO_END && end <= start) return undefined;
  return { start, end };
}

// Правка поля → выделение нити (его тут же покажет волна в карточке)
export function commitPiece(sessionId: string, threadId: string, versionId: string, t: PieceText): void {
  const sel = pieceFromText(t);
  if (sel === undefined) return;
  setSelection(sessionId, threadId, sel ? { ...sel, versionId } : null);
}

export const readPiece = (sessionId: string | null, threadId: string | null): ThreadSelection | null =>
  getSelection(sessionId, threadId);

// Секунды для запуска: «до конца» уходит как −1 (end_seconds у local-media; fal такой конец не шлёт)
export function pieceSeconds(sel: AudioSelection | null): { startSec: number | null; endSec: number | null } {
  return sel ? { startSec: sel.start, endSec: sel.end } : { startSec: null, endSec: null };
}
