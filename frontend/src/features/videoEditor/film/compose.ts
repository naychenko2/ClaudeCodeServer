// «Сочинить под фильм…» (ADR-022, контракт films/music): сервер заводит черновик звука в этом чате и
// ждёт его первую версию — она встанет музыкой фильма сама, кто бы ни запустил (человек или агент).
// Здесь — запрос черновика и панель «Звук» на нём с заготовкой и возвратом; остальные варианты — «Сменить».

import { FLAGS, getFlag, holdStripRequests, revealWorkspacePanel, showToast } from 'aihome_shell/kit';
import { errorText, videoApi, type FilmState } from '../api';
import { VIDEO_PANEL, VIDEO_STRIP } from '../store/videoStore';
import { filmToSound } from '../context/handoff';
import { soundPreset } from './model';

// Фильм → музыка до запроса: новая музыка после него — «из «Звука»»
const _pending = new Map<string, string | null>();

// Решение v7 №9: полоса над полем ввода остаётся на «Видео», панель «Звук» открывается рядом.
// Черновик звука берёт фокус нитей «Звука» (сервером и ответом), а тот просит свою полосу — держим её
const SOUND_STRIP_KEY = 'sound';
const KEEP_STRIP_MS = 15_000;

export async function composeForFilm(scope: string, sessionId: string, name: string, f: FilmState, reveal = true): Promise<boolean> {
  holdStripRequests(sessionId, SOUND_STRIP_KEY, KEEP_STRIP_MS);
  try {
    const draft = await videoApi.composeMusic(scope, sessionId, f.path);
    _pending.set(f.path, f.document.music?.file ?? null);
    if (getFlag(FLAGS.composerContextRow)) {
      // Строка контекста: черновик звука — основной объект, чип «Песня» с описанием стиля, назад — «К фильму»
      const style = String(soundPreset(name, f, draft).style ?? '');
      await filmToSound({ sessionId, path: f.path, filmName: name, threadId: draft.threadId, style, reveal });
    } else revealWorkspacePanel('sound', 'settings', {
      sessionId, preset: { ...soundPreset(name, f, draft), thread: draft.threadId },
      returnTo: { key: VIDEO_PANEL, strip: VIDEO_STRIP, tab: 'film', target: f.path, label: `К фильму «${name}» — панель «Видео»` },
    });
    if (draft.durationNote) showToast(draft.durationNote, '', 'info');
    return true;
  } catch (e) {
    holdStripRequests(sessionId, SOUND_STRIP_KEY, 0);
    showToast(errorText(e, 'Не удалось завести звук под фильм'), '', 'error');
    return false;
  }
}

export const isComposing = (path: string, music: string | null | undefined) =>
  _pending.has(path) && (_pending.get(path) ?? null) === (music ?? null);

export const isFromSound = (path: string, file: string) => _pending.has(path) && _pending.get(path) !== file;
