// Режим звука чата — одна точка для панели, полосы, композера и карточек агента (план
// «Звук» v3/v4, шаг K1). Режим не хранится отдельным полем: его выводят из выбранной нити,
// выбора человека и последнего режима создания. Поэтому снятие выбора одинаково для ✕ в
// полосе, ✕ в панели и для агента (audio_focus без аргументов), ловить события не нужно.

import type { AudioMode, AudioPrefs, AudioThread, AudioThreadSettings } from '../api';
import { isCreateMode } from '../ops';
import { getChosenMode, getFocusedThread, getLastCreateMode, getPendingSettings, getPrefs } from './threadStore';

// Режим, который человек выбрал сам. withSound — выбран при выбранном звуке: «Обработка» такого
// выбора без звука не держится. Выбранная без звука (склейка) — держится
export interface ChosenMode { mode: AudioMode; withSound: boolean }

// Выбор человека, ещё не доехавший до сервера; threadId — чьи это настройки (null — префы режима)
export interface PendingSettings { threadId: string | null; settings: AudioThreadSettings }

// Звук, с которым работают операции «Обработки»: готовая версия, а не пустой черновик
export const hasSound = (thread: AudioThread | null | undefined): boolean => !!thread?.currentVersionId;

export interface ModeInput {
  thread: AudioThread | null;
  chosen: ChosenMode | null;
  lastCreate: AudioMode | null;
}

// Действующий режим. Нить со звуком — её режим. Черновик — его режим, кроме «Обработки».
// Без нити — выбор человека. «Обработка» без звука — последний режим создания, если только
// человек не выбрал её сам без звука
export function effectiveMode({ thread, chosen, lastCreate }: ModeInput): AudioMode {
  const create = lastCreate ?? (isCreateMode(chosen?.mode) ? chosen!.mode : null) ?? 'voice';
  const own = thread?.settings?.mode ?? null;
  if (hasSound(thread)) return own ?? chosen?.mode ?? create;
  const mode = own ?? chosen?.mode ?? create;
  if (mode !== 'process') return mode;
  return !thread && !own && chosen?.mode === 'process' && !chosen.withSound ? 'process' : create;
}

// Выбор человека поверх нити (или префов режима без нити)
export function overlayPending(thread: AudioThread | null, prefs: AudioPrefs, pending: AudioThreadSettings | null) {
  if (!pending) return { thread, prefs };
  if (thread) return { thread: { ...thread, settings: pending }, prefs };
  return {
    thread: null,
    prefs: {
      ...prefs,
      [pending.mode]: {
        operation: pending.operation, provider: pending.provider, model: pending.model, count: pending.count ?? null,
        fields: pending.fields, inputs: pending.inputs ?? null,
      },
    },
  };
}

export interface SoundSource {
  // Нить для resolveLaunch/resolvePanel: настройки чужого режима сняты — иначе они перебили бы режим
  thread: AudioThread | null;
  prefs: AudioPrefs;
  mode: AudioMode;
}

// Откуда полоса, панель и запуск берут настройки: нить в фокусе и префы с невыехавшим выбором
// человека плюс действующий режим
export function soundSource(scope: string, sessionId: string | null, focus: AudioThread | null = getFocusedThread(sessionId)): SoundSource {
  const pending = getPendingSettings(sessionId, focus?.id ?? null)?.settings ?? null;
  const eff = overlayPending(focus, getPrefs(scope), pending);
  const mode = effectiveMode({ thread: eff.thread, chosen: getChosenMode(sessionId), lastCreate: getLastCreateMode(sessionId) });
  const thread = eff.thread?.settings && eff.thread.settings.mode !== mode ? { ...eff.thread, settings: null } : eff.thread;
  return { thread, prefs: eff.prefs, mode };
}

export const getSoundMode = (scope: string, sessionId: string | null): AudioMode => soundSource(scope, sessionId).mode;
