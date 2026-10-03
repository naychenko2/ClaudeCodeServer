// Передача хода в «Картинки» и «Звук» через панель «Контекст» (ADR-023, шаг 3ф-3), когда флаг строки контекста
// включён. Своей панели у них больше нет: черновик становится основным объектом, предвыбор ставит нужное
// действие чипом («Нарисовать», «Песня»), а «↩ К сцене» / «↩ К фильму» хранит прежний основной объект.
// Вызовов revealWorkspacePanel с ключами генерации здесь быть не должно — их считает сторож features/**.

import {
  getChatContextState, objectKey, presetAction, revealContextPanel, setContextReturn, setPrimary, showToast,
  type ChatContextItem,
} from 'aihome_shell/kit';
import { FILM_KIND, SCENE_KIND } from './state';

// Виды «Картинок» и «Звука» — ключи контракта хранилища контекста; импортировать вертикали друг у друга нельзя
const IMAGE_KIND = 'image';
const AUDIO_KIND = 'audio';

// Основной объект, если он ровно тот, куда потом возвращаться; иначе возврата нет
function currentIf(sessionId: string, kind: string, match: (ref: Record<string, unknown>) => boolean): ChatContextItem | null {
  const p = getChatContextState(sessionId).primary;
  return p && p.kind === kind && match(p.ref) ? (p as ChatContextItem) : null;
}

interface Handoff {
  sessionId: string;
  kind: string;
  threadId: string;
  actionId?: string;
  prefill?: string;
  prev: ChatContextItem | null;
  label: string;
  // На телефоне шторка закрыла бы поле ввода, а чипы действий над ним появляются сами
  reveal: boolean;
  failText: string;
}

async function handOff(h: Handoff): Promise<boolean> {
  const input = { kind: h.kind, ref: { threadId: h.threadId } };
  // Предвыбор раньше смены объекта: хост поля применит его при первом же разрешении действия
  if (h.actionId) presetAction(h.sessionId, objectKey(input), { actionId: h.actionId, ...(h.prefill ? { prefill: h.prefill } : {}) });
  if (await setPrimary(h.sessionId, input) === 'failed') {
    showToast(h.failText, '', 'error');
    return false;
  }
  if (h.prev) setContextReturn(h.sessionId, { prev: h.prev, label: h.label });
  if (h.reveal) revealContextPanel(h.sessionId, {});
  return true;
}

// Кадр сцены: нить «Картинок» (черновик «Нарисовать» или файл «Править») → основной объект
export function sceneToImages(p: {
  sessionId: string; sceneId: string; sceneName: string; threadId: string; draw: boolean; reveal: boolean;
}): Promise<boolean> {
  return handOff({
    sessionId: p.sessionId, kind: IMAGE_KIND, threadId: p.threadId, ...(p.draw ? { actionId: 'draw' } : {}),
    prev: currentIf(p.sessionId, SCENE_KIND, r => r.sceneId === p.sceneId),
    label: `К сцене «${p.sceneName}»`, reveal: p.reveal, failText: 'Не удалось открыть картинку в контексте',
  });
}

// «Сочинить под фильм…»: черновик звука → основной объект, чип «Песня» с описанием стиля
export function filmToSound(p: {
  sessionId: string; path: string; filmName: string; threadId: string; style: string; reveal: boolean;
}): Promise<boolean> {
  return handOff({
    sessionId: p.sessionId, kind: AUDIO_KIND, threadId: p.threadId, actionId: 'song', prefill: p.style,
    prev: currentIf(p.sessionId, FILM_KIND, r => r.filmPath === p.path),
    label: `К фильму «${p.filmName}»`, reveal: p.reveal, failText: 'Не удалось открыть звук в контексте',
  });
}
