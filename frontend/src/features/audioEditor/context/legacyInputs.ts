// Переезд старых входов звука в контекст чата (ADR-023 §1, 2з-3): `localStorage` `cc_audio_inputs:` держал
// путь к модели голоса (voiceModelPath) и записи для обучения (clipPaths). Модель из библиотеки `voices/<slug>/`
// становится референсом `audio-voice` с ролью «голос»; записи обучения у контекста роли не имеют — они входы формы
// «Обучить голос» (voices/TrainVoiceForm), форма сама подберёт их через legacyClipPaths.

import { attachRef } from 'aihome_shell/kit';
import { inputsKey, readInputs, writeInputs } from '../panel/inputs';

const PREFIX = 'cc_audio_inputs:';
const _tried = new Set<string>();

// voices/andrey/voice.pth → andrey; путь вне библиотеки — null (его контекст принять не может)
export const voiceSlugOfPath = (path: string): string | null => /^(?:\.\/)?voices\/([^/]+)\//.exec(path.trim())?.[1] ?? null;

interface RefLike { kind: string; ref: Record<string, unknown> }

// Один раз за вкладку на нить: модель голоса из старых входов — в референс, из входов — вон. Нет основного
// объекта или сервер отказал — входы остаются как были
export function migrateLegacyVoice(scope: string, sessionId: string, threadId: string, refs: readonly RefLike[]): void {
  const key = inputsKey(scope, sessionId, threadId);
  if (_tried.has(key)) return;
  _tried.add(key);
  const inputs = readInputs(key);
  const slug = voiceSlugOfPath(inputs.voiceModelPath);
  if (!slug) return;
  const clear = () => writeInputs(key, { ...readInputs(key), voiceModelPath: '', voiceIndexPath: '' });
  if (refs.some(r => r.kind === 'audio-voice' && r.ref.slug === slug)) { clear(); return; }
  void attachRef(sessionId, { kind: 'audio-voice', ref: { slug }, role: 'voice' }).then(r => { if (r === 'ok') clear(); });
}

// Записи обучения из старых входов области: первая непустая запись по любой нити; забрать — очистить
export function legacyClipPaths(scope: string): string[] {
  try {
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (!key?.startsWith(`${PREFIX}${scope}:`)) continue;
      const clips = readInputs(key).clipPaths.filter(p => p.trim());
      if (clips.length) return clips;
    }
  } catch { /* хранилище недоступно — форма пустая */ }
  return [];
}

export function dropLegacyClipPaths(scope: string): void {
  try {
    const keys: string[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key?.startsWith(`${PREFIX}${scope}:`)) keys.push(key);
    }
    for (const key of keys) {
      const v = readInputs(key);
      if (v.clipPaths.length) writeInputs(key, { ...v, clipPaths: [] });
    }
  } catch { /* не критично */ }
}

export const __resetLegacyInputs = () => _tried.clear();
