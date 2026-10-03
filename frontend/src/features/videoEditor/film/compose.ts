// «Сочинить под фильм…» (ADR-022, контракт films/music): сервер заводит черновик звука в этом чате и
// ждёт его первую версию — она встанет музыкой фильма сама, кто бы ни запустил (человек или агент).
// Здесь — запрос черновика и передача хода в «Звук» через панель «Контекст»; остальные варианты — «Сменить».

import { showToast } from 'aihome_shell/kit';
import { errorText, videoApi, type FilmState } from '../api';
import { filmToSound } from '../context/handoff';
import { soundPreset } from './model';

// Фильм → музыка до запроса: новая музыка после него — «из «Звука»»
const _pending = new Map<string, string | null>();

export async function composeForFilm(scope: string, sessionId: string, name: string, f: FilmState, reveal = true): Promise<boolean> {
  try {
    const draft = await videoApi.composeMusic(scope, sessionId, f.path);
    _pending.set(f.path, f.document.music?.file ?? null);
    // Черновик звука — основной объект, чип «Песня» с описанием стиля, назад — «К фильму»
    const style = String(soundPreset(name, f, draft).style ?? '');
    await filmToSound({ sessionId, path: f.path, filmName: name, threadId: draft.threadId, style, reveal });
    if (draft.durationNote) showToast(draft.durationNote, '', 'info');
    return true;
  } catch (e) {
    showToast(errorText(e, 'Не удалось завести звук под фильм'), '', 'error');
    return false;
  }
}

export const isComposing = (path: string, music: string | null | undefined) =>
  _pending.has(path) && (_pending.get(path) ?? null) === (music ?? null);

export const isFromSound = (path: string, file: string) => _pending.has(path) && _pending.get(path) !== file;
