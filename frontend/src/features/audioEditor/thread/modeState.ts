// Режим звука чата — одна точка для запуска текста из карточки агента и котировок. Режим не хранится отдельным
// полем: его выводят из нити в фокусе. Без выбора человека (панели «Звук» и полосы больше нет) черновик без звука
// и нить без настроек идут режимом «Голос».

import type { AudioMode, AudioPrefs, AudioThread } from '../api';
import { getFocusedThread, getPrefs } from './threadStore';

// Звук, с которым работают операции «Обработки»: готовая версия, а не пустой черновик
const hasSound = (thread: AudioThread | null | undefined): boolean => !!thread?.currentVersionId;

// Действующий режим. Нить со звуком — её режим. Черновик — его режим, кроме «Обработки»: обрабатывать
// нечего, поэтому черновик «Обработки» без звука идёт режимом «Голос»
export function effectiveMode(thread: AudioThread | null): AudioMode {
  const own = thread?.settings?.mode ?? null;
  if (hasSound(thread)) return own ?? 'voice';
  const mode = own ?? 'voice';
  return mode === 'process' ? 'voice' : mode;
}

export interface SoundSource {
  // Нить для resolveLaunch/resolvePanel: настройки чужого режима сняты — иначе они перебили бы режим
  thread: AudioThread | null;
  prefs: AudioPrefs;
  mode: AudioMode;
}

// Откуда запуск берёт настройки: нить в фокусе и префы области плюс действующий режим
export function soundSource(scope: string, sessionId: string | null, focus: AudioThread | null = getFocusedThread(sessionId)): SoundSource {
  const mode = effectiveMode(focus);
  const thread = focus?.settings && focus.settings.mode !== mode ? { ...focus, settings: null } : focus;
  return { thread, prefs: getPrefs(scope), mode };
}
