// «Обработать ▾» в карточке версии (макет audio-editor-v2-proposal.md, «Карточка нити в ленте»):
// операции режима «Обработка» для этой версии, «Склеить с другим звуком», «Сменить голос», кавер и
// перегенерация куска. Пункт ставит версию в работу и переключает панель «Звук» на операцию
// (requestOperation); склейка ещё и кладёт эту версию первым куском.

import type { AudioOp, AudioThread } from '../api';
import { panelOps } from '../panel/model';
import type { ConcatInputs, ConcatPiece } from '../panel/inputs';
import { focusLabel } from '../strip/summary';

export interface ProcMenuItem { op: AudioOp; label: string; group: 'process' | 'other' }

export function procMenuItems(): ProcMenuItem[] {
  const process = panelOps('process').map(o => ({
    op: o.op, label: o.op === 'concat' ? 'Склеить с другим звуком' : o.label, group: 'process' as const,
  }));
  return [
    ...process,
    { op: 'convertVoice', label: 'Сменить голос', group: 'other' },
    { op: 'cover', label: 'Кавер', group: 'other' },
    { op: 'repaint', label: 'Перегенерировать кусок', group: 'other' },
  ];
}

// Кусок склейки — эта версия нити
export const versionPiece = (thread: AudioThread, versionId: string): ConcatPiece =>
  ({ threadId: thread.id, versionId, label: focusLabel({ ...thread, currentVersionId: versionId }) });

const same = (a: ConcatPiece, b: ConcatPiece) =>
  (a.projectFile ?? null) === (b.projectFile ?? null) && (a.threadId ?? null) === (b.threadId ?? null) && (a.versionId ?? null) === (b.versionId ?? null);

// Кусок — первым; если он уже был в списке, переезжает наверх. Свои стыки привязаны к местам,
// поэтому при сдвиге мест сбрасываются на общий
export function withFirstPiece(c: ConcatInputs, piece: ConcatPiece): ConcatInputs {
  if (c.pieces[0] && same(c.pieces[0], piece)) return c;
  const pieces = [piece, ...c.pieces.filter(p => !same(p, piece))];
  return { ...c, pieces, joints: Array.from({ length: Math.max(pieces.length - 1, 0) }, () => null) };
}
