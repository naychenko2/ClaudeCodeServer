// «Сочинить под фильм…» (ADR-022, контракт films/music): сервер заводит черновик звука в этом чате и
// ждёт его первую версию — она встанет музыкой фильма сама, кто бы ни запустил (человек или агент).
// Здесь — запрос черновика и панель «Звук» на нём с заготовкой и возвратом; остальные варианты — «Сменить».

import { revealWorkspacePanel, showToast } from 'aihome_shell/kit';
import { errorText, videoApi, type FilmState } from '../api';
import { VIDEO_PANEL } from '../store/videoStore';
import { soundPreset } from './model';

// Фильм → музыка до запроса: новая музыка после него — «из «Звука»»
const _pending = new Map<string, string | null>();

export async function composeForFilm(scope: string, sessionId: string, name: string, f: FilmState): Promise<boolean> {
  try {
    const draft = await videoApi.composeMusic(scope, sessionId, f.path);
    _pending.set(f.path, f.document.music?.file ?? null);
    revealWorkspacePanel('sound', 'settings', {
      sessionId, preset: { ...soundPreset(name, f), thread: draft.threadId },
      returnTo: { key: VIDEO_PANEL, tab: 'film', target: f.path, label: `К фильму «${name}» — панель «Видео»` },
    });
    return true;
  } catch (e) {
    showToast(errorText(e, 'Не удалось завести звук под фильм'), '', 'error');
    return false;
  }
}

export const isComposing = (path: string, music: string | null | undefined) =>
  _pending.has(path) && (_pending.get(path) ?? null) === (music ?? null);

export const isFromSound = (path: string, file: string) => _pending.has(path) && _pending.get(path) !== file;
