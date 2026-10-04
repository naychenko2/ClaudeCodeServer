// Правки без ИИ: обрезка и громкость (ручка edit, каждая правка — новая версия) для редактора звука.

import { audioApi, type AudioDspEditRequest } from '../api';
import { TO_END, type AudioSelection } from '../player/selection';
import { mutate } from '../thread/threadStore';
import type { TrimInputs } from './inputs';

// Правки обрезки по порядку: кусок, громкость с фейдами, нормализация
export function trimSteps(t: TrimInputs): AudioDspEditRequest[] {
  const format = t.format || null;
  const steps: AudioDspEditRequest[] = [];
  if (t.start !== null || t.end !== null) steps.push({ op: 'trim', startSec: t.start, endSec: t.end, format });
  if (t.gainDb !== 0 || t.fadeIn > 0 || t.fadeOut > 0) {
    steps.push({ op: 'gainFade', gainDb: t.gainDb, fadeInSec: t.fadeIn, fadeOutSec: t.fadeOut, format });
  }
  if (t.normalize) steps.push({ op: 'normalize', format });
  return steps;
}

// Кусок обрезки — из выделения: «до конца» обрезке не нужен, это просто отсутствие конца
export function trimWithPiece(t: TrimInputs, piece: AudioSelection | null): TrimInputs {
  return { ...t, start: piece ? piece.start : null, end: piece && piece.end !== TO_END ? piece.end : null };
}

// Правки без ИИ по порядку, каждая — новая версия; false — одна из них не прошла (дальше не идём)
export async function runTrimSteps(scope: string, sessionId: string, threadId: string, t: TrimInputs): Promise<boolean> {
  for (const step of trimSteps(t)) {
    const ok = await mutate(scope, sessionId, async rev => (await audioApi.edit(scope, sessionId, threadId, { ...step, revision: rev })).state);
    if (!ok) return false;
  }
  return true;
}
