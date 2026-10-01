// Запуск из панели «Звук» — тот же путь, что у композера (котировка → задача строго по quoteId,
// ADR-021 §2), плюс правки без ИИ: обрезка и громкость (ручка edit, каждая правка — новая версия)
// и склейка (ручка concat, итог — новая нить).

import { showToast } from 'aihome_shell/kit';
import { audioApi, type AudioDspEditRequest, type AudioJobInput, type AudioQuoteRequest, type AudioThread } from '../api';
import { opInfo } from '../ops';
import { mutate } from '../thread/threadStore';
import type { PanelInputs, TrimInputs } from './inputs';
import type { PanelState } from './model';

export interface RunArgs {
  scope: string;
  sessionId: string;
  thread: AudioThread | null;
  state: PanelState;
  fields: Record<string, unknown>;
  inputs: PanelInputs;
  reference: File | null;
  text: string;
}

// Запрос котировки: явный выбор человека и поля модели поверх цепочки нити
export function quoteRequest(a: Omit<RunArgs, 'inputs' | 'reference'>): AudioQuoteRequest {
  const s = a.state;
  const field = opInfo(s.op)?.field ?? 'prompt';
  return {
    mode: s.mode, operation: s.op, provider: s.providerKey, model: s.modelId, count: s.count,
    sessionId: a.sessionId, threadId: a.thread?.id ?? null,
    text: field === 'text' ? a.text.trim() || null : null,
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

export const dialogueText = (inputs: PanelInputs) =>
  inputs.replicas.filter(r => r.text.trim()).map(r => (r.voice.trim() ? `${r.voice.trim()}: ${r.text.trim()}` : r.text.trim())).join('\n');

export function jobInput(a: RunArgs, quoteId: string): AudioJobInput {
  const s = a.state;
  const field = opInfo(s.op)?.field ?? 'prompt';
  const text = s.op === 'dialogue' ? dialogueText(a.inputs) : a.text.trim();
  const i = a.inputs;
  const usesRef = s.op === 'cloneVoice' || s.op === 'convertVoice' || s.op === 'master';
  return {
    quoteId, sessionId: a.sessionId, threadId: a.thread?.id ?? null,
    text: field === 'text' ? text || null : null,
    prompt: field === 'prompt' && text ? text : null,
    language: i.language || null,
    reference: usesRef ? a.reference : null,
    referencePath: usesRef && !a.reference ? i.referencePath.trim() || null : null,
    clipPaths: s.op === 'trainVoice' ? i.clipPaths.map(p => p.trim()).filter(Boolean) : undefined,
    voiceModelPath: s.op === 'convertVoice' ? i.voiceModelPath.trim() || null : null,
    voiceIndexPath: s.op === 'convertVoice' ? i.voiceIndexPath.trim() || null : null,
  };
}

// true — запущено; причина отказа показана тостом
export async function runPanel(a: RunArgs): Promise<boolean> {
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
      for (const step of trimSteps(inputs.trim)) {
        const ok = await mutate(scope, sessionId, async rev => (await audioApi.edit(scope, sessionId, thread.id, { ...step, revision: rev })).state);
        if (!ok) return false;
      }
      return true;
    }
    const quote = await audioApi.quote(scope, sessionId, quoteRequest({ ...a, text: s.op === 'dialogue' ? dialogueText(inputs) : a.text }));
    await audioApi.startJob(scope, sessionId, jobInput(a, quote.quoteId));
    return true;
  } catch (e) {
    showToast((e as Error).message || 'Звук не запущен', '', 'error');
    return false;
  }
}
