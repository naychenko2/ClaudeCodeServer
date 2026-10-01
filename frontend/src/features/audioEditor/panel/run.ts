// Запуск из панели «Звук» — тот же путь, что у композера (котировка → задача строго по quoteId,
// ADR-021 §2), плюс правки без ИИ: обрезка и громкость (ручка edit, каждая правка — новая версия)
// и склейка (ручка concat, итог — новая нить).

import { showToast } from 'aihome_shell/kit';
import { audioApi, cloneRefusal, type AudioDspEditRequest, type AudioJobInput, type AudioQuote, type AudioQuoteRequest, type AudioThread } from '../api';
import { opInfo } from '../ops';
import { TO_END, type AudioSelection } from '../player/selection';
import { mutate } from '../thread/threadStore';
import { pickedSlug } from '../voices/model';
import { LIBRARY_VOICE_OPS, type PanelInputs, type TrimInputs } from './inputs';
import type { PanelState } from './model';
import { durationRange, lyricsToSend } from './music';
import { pieceSeconds } from './piece';

export interface RunArgs {
  scope: string;
  sessionId: string;
  thread: AudioThread | null;
  state: PanelState;
  fields: Record<string, unknown>;
  inputs: PanelInputs;
  reference: File | null;
  text: string;
  // Кусок нити — общий с выделением на волне (обрезка и «Перегенерировать кусок»)
  piece: AudioSelection | null;
}

// Запрос котировки: явный выбор человека и поля модели поверх цепочки нити
export function quoteRequest(a: Omit<RunArgs, 'inputs' | 'reference' | 'piece'> & { durationSec?: number | null }): AudioQuoteRequest {
  const s = a.state;
  const field = opInfo(s.op)?.field ?? 'prompt';
  return {
    mode: s.mode, operation: s.op, provider: s.providerKey, model: s.modelId, count: s.count,
    sessionId: a.sessionId, threadId: a.thread?.id ?? null,
    text: field === 'text' ? a.text.trim() || null : null,
    durationSec: a.durationSec ?? null,
    fields: a.fields,
  };
}

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

// Длительность уходит только операциям, где её задают, и только в пределах модели
export function musicDuration(s: PanelState, inputs: PanelInputs): number | null {
  return durationRange(s.op, s.model) && inputs.durationSec !== null ? inputs.durationSec : null;
}

export const dialogueText = (inputs: PanelInputs) =>
  inputs.replicas.filter(r => r.text.trim()).map(r => (r.voice.trim() ? `${r.voice.trim()}: ${r.text.trim()}` : r.text.trim())).join('\n');

export function jobInput(a: RunArgs, quoteId: string): AudioJobInput {
  const s = a.state;
  const field = opInfo(s.op)?.field ?? 'prompt';
  const text = s.op === 'dialogue' ? dialogueText(a.inputs) : a.text.trim();
  const i = a.inputs;
  // Голос из библиотеки заменяет образец и модель RVC: всё нужное сервер возьмёт из voices/
  const voice = LIBRARY_VOICE_OPS.has(s.op) && i.voice ? i.voice : null;
  const usesRef = !voice && (s.op === 'cloneVoice' || s.op === 'convertVoice' || s.op === 'master');
  return {
    quoteId, sessionId: a.sessionId, threadId: a.thread?.id ?? null,
    text: field === 'text' ? text || null : null,
    prompt: field === 'prompt' && text ? text : null,
    language: i.language || null,
    reference: usesRef ? a.reference : null,
    referencePath: usesRef && !a.reference ? i.referencePath.trim() || null : null,
    clipPaths: s.op === 'trainVoice' ? i.clipPaths.map(p => p.trim()).filter(Boolean) : undefined,
    voiceModelPath: s.op === 'convertVoice' && !voice ? i.voiceModelPath.trim() || null : null,
    voiceIndexPath: s.op === 'convertVoice' && !voice ? i.voiceIndexPath.trim() || null : null,
    voice,
    lyrics: s.mode === 'music' ? lyricsToSend(s.op, s.model, i) : null,
    durationSec: musicDuration(s, i),
    ...(s.op === 'repaint' ? pieceSeconds(a.piece) : {}),
  };
}

// Отказ «клон MiniMax протух или не создан»: панель показывает причину и «Пересоздать · цена»
export interface CloneRefusal { message: string; slug: string; quote: AudioQuote | null }

// true — запущено; отказ клона уходит в onCloneRefusal, прочие причины — тостом
export async function runPanel(a: RunArgs, onCloneRefusal?: (r: CloneRefusal) => void): Promise<boolean> {
  const { scope, sessionId, thread, state: s, inputs } = a;
  try {
    if (s.op === 'concat') {
      const c = inputs.concat;
      await audioApi.concat(scope, sessionId, {
        pieces: c.pieces.map(p => ({ threadId: p.threadId ?? null, versionId: p.versionId ?? null, projectFile: p.projectFile ?? null })),
        joint: c.joint,
        joints: c.joints.length ? c.joints : null,
        normalizeLoudness: c.normalize,
        name: c.name.trim() || null,
        format: c.format,
      });
      return true;
    }
    if (s.op === 'trim') {
      if (!thread) return false;
      for (const step of trimSteps(trimWithPiece(inputs.trim, a.piece))) {
        const ok = await mutate(scope, sessionId, async rev => (await audioApi.edit(scope, sessionId, thread.id, { ...step, revision: rev })).state);
        if (!ok) return false;
      }
      return true;
    }
    const quote = await audioApi.quote(scope, sessionId, quoteRequest({
      ...a, text: s.op === 'dialogue' ? dialogueText(inputs) : a.text, durationSec: musicDuration(s, inputs),
    }));
    await audioApi.startJob(scope, sessionId, jobInput(a, quote.quoteId));
    return true;
  } catch (e) {
    const clone = cloneRefusal(e);
    const slug = clone?.recreate?.recreateVoice ?? pickedSlug(inputs.voice);
    if (clone && slug && onCloneRefusal) {
      onCloneRefusal({ message: clone.message, slug, quote: clone.recreate });
      return false;
    }
    showToast((e as Error).message || 'Звук не запущен', '', 'error');
    return false;
  }
}
