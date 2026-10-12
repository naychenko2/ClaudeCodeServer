// «Подставить персонажа» на лицо (этап 2 · шаг 10a): чистая логика состояния поиска лиц,
// выбора лица и персонажа. Разделена от React-хука, чтобы тестировать редьюсер без DOM.
// Экран (оверлей, список персонажей) — шаг 10b; здесь только данные.

import { useCallback, useReducer } from 'react';
import { findFaces, facesError, type DetectedFace, type FacesResponse } from '../api';

// Фаза поиска лиц: idle — ещё не искали, loading — запрос в пути, ready — список получен,
// error — отказ (код в state.error)
export type FaceSwapPhase = 'idle' | 'loading' | 'ready' | 'error';

export interface FaceSwapState {
  phase: FaceSwapPhase;
  // Свежий список лиц (ready); facesKey — ключ ревизии, уходящий с запуском
  faces: DetectedFace[];
  facesKey: string | null;
  // Выбранное лицо (n); null — не выбрано
  selectedFace: number | null;
  // Выбранный персонаж (slug); null — не выбран. Персонаж контекста сам не подставляется
  selectedCharacter: string | null;
  // Код отказа (error)
  error: string | null;
}

export const initialFaceSwap: FaceSwapState = {
  phase: 'idle', faces: [], facesKey: null, selectedFace: null, selectedCharacter: null, error: null,
};

// Лицо «доступное»: не мелкое (small) — мелкое выбрать нельзя
export const faceSelectable = (f: DetectedFace): boolean => !f.small;

// Сколько доступных (не small) лиц в списке
export function selectableFaces(faces: readonly DetectedFace[]): DetectedFace[] {
  return faces.filter(faceSelectable);
}

// Авто-выбор: ровно одно доступное лицо выбирается сразу; больше — человек выбирает сам
function autoSelect(faces: readonly DetectedFace[]): number | null {
  const avail = selectableFaces(faces);
  return avail.length === 1 ? avail[0].n : null;
}

export type FaceSwapAction =
  // Начать поиск лиц
  | { type: 'searchStart' }
  // Список получен: обновляем; одно доступное лицо — сразу, иначе выбор по прежнему (если ещё валиден)
  | { type: 'searchOk'; response: FacesResponse; reset?: boolean }
  // Отказ поиска
  | { type: 'searchError'; code: string }
  // 409 faces_changed: свежий список, выбор лица сбрасывается (безопаснее)
  | { type: 'facesChanged'; response: FacesResponse }
  // Выбрать/снять лицо (n)
  | { type: 'selectFace'; n: number | null }
  // Выбрать/снять персонажа (slug)
  | { type: 'selectCharacter'; slug: string | null };

// Чистый редьюсер. reset (searchOk) — сбросить выбор лица; facesChanged — всегда сбрасывает
export function faceSwapReducer(s: FaceSwapState, a: FaceSwapAction): FaceSwapState {
  switch (a.type) {
    case 'searchStart':
      return { ...s, phase: 'loading', error: null };
    case 'searchOk': {
      const keep = !a.reset && s.selectedFace != null && a.response.faces.some(f => f.n === s.selectedFace);
      return {
        ...s, phase: 'ready', faces: a.response.faces, facesKey: a.response.facesKey, error: null,
        selectedFace: a.reset ? null : keep ? s.selectedFace : autoSelect(a.response.faces),
      };
    }
    case 'searchError':
      return { ...s, phase: 'error', error: a.code };
    case 'facesChanged':
      // Сброс выбора лица: сервер сопоставил старую рамку с новой, безопаснее переспросить
      return {
        ...s, phase: 'ready', faces: a.response.faces, facesKey: a.response.facesKey, error: null,
        selectedFace: null,
      };
    case 'selectFace':
      return { ...s, selectedFace: a.n };
    case 'selectCharacter':
      return { ...s, selectedCharacter: a.slug };
    default:
      return s;
  }
}

// canSubmit: лицо и персонаж выбраны, и исполнитель — локальные модели
export function faceSwapCanSubmit(s: FaceSwapState, provider: string | null | undefined): boolean {
  return s.selectedFace != null && s.selectedCharacter != null && provider === 'local';
}

// Хук-состояние (без разметки): обёртка над редьюсером + запрос findFaces.
// threadId/versionId — версия, на которой ищем лица; provider — исполнитель текущего запуска
export function useFaceSwap(threadId: string | null, versionId: string | null, provider: string | null | undefined) {
  const [state, dispatch] = useReducer(faceSwapReducer, initialFaceSwap);

  const search = useCallback(async (): Promise<void> => {
    if (!threadId || !versionId) return;
    dispatch({ type: 'searchStart' });
    try {
      const res = await findFaces(threadId, versionId);
      dispatch({ type: 'searchOk', response: res });
    } catch (e) {
      const err = facesError(e);
      if (err.code === 'faces_changed' && err.faces) {
        dispatch({ type: 'facesChanged', response: err.faces });
      } else {
        dispatch({ type: 'searchError', code: err.code });
      }
    }
  }, [threadId, versionId]);

  const selectFace = useCallback((n: number | null) => dispatch({ type: 'selectFace', n }), []);
  const selectCharacter = useCallback((slug: string | null) => dispatch({ type: 'selectCharacter', slug }), []);

  return {
    state,
    canSubmit: faceSwapCanSubmit(state, provider),
    search,
    selectFace,
    selectCharacter,
  };
}
