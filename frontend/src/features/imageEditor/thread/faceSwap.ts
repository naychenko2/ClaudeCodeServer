// «Подставить персонажа» на лицо (этап 2 · шаг 10a): чистая логика состояния поиска лиц,
// выбора лица и персонажа. Разделена от React-хука, чтобы тестировать редьюсер без DOM.
// Экран (оверлей, список персонажей) — шаг 10b; здесь только данные.

import { useCallback, useEffect, useReducer, useRef } from 'react';
import { findFaces, facesError, type DetectedFace, type FacesResponse } from '../api';
import {
  assignCharacter, emptyAssignments, pruneAssignments, removeAssignment, searchFailureText, tapFace,
  type FaceAssignments,
} from '../faceSwapView';

// Фаза поиска лиц: idle — ещё не искали, loading — запрос в пути, ready — список получен,
// error — отказ (код в state.error)
export type FaceSwapPhase = 'idle' | 'loading' | 'ready' | 'error';

// Причина отказа ПОИСКА лиц (не запуска): код + человекочитаемый текст для плашки.
// busy → «Видеокарта занята другой задачей…», provider_unavailable → «Локальные модели
// сейчас недоступны», прочие — общий текст. null — отказа нет.
export interface SearchFailure { code: string; text: string }

export interface FaceSwapState {
  phase: FaceSwapPhase;
  // Свежий список лиц (ready); facesKey — ключ ревизии, уходящий с запуском
  faces: DetectedFace[];
  facesKey: string | null;
  // Размеры картинки (px) из ответа: bbox в пикселях масштабируется в доли через них
  width: number;
  height: number;
  // Выбранное лицо (n); null — не выбрано
  selectedFace: number | null;
  // Выбранный персонаж (slug); null — не выбран. Персонаж контекста сам не подставляется
  selectedCharacter: string | null;
  // Назначения «лицо → персонаж» (ADR-028, этап 3): максимум 2, active/limit — см. faceSwapView
  assignments: FaceAssignments;
  // Код отказа (error)
  error: string | null;
  // Причина отказа поиска (busy/provider_unavailable/…): плашка, пока не повторят поиск
  searchFailure: SearchFailure | null;
  // Номер последнего запроса: 0 — ещё не было запросов; >0 — в пути или завершён.
  // Ответ применяется только если его requestId совпадает с requestSeq (защита от out-of-order)
  requestSeq: number;
}

export const initialFaceSwap: FaceSwapState = {
  phase: 'idle', faces: [], facesKey: null, width: 0, height: 0, selectedFace: null, selectedCharacter: null,
  assignments: emptyAssignments, error: null, searchFailure: null, requestSeq: 0,
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
  // Начать поиск лиц: инкремент requestSeq; сбрасывает причину отказа поиска (повторный поиск)
  | { type: 'searchStart' }
  // Список получен: обновляем; одно доступное лицо — сразу, иначе выбор по прежнему (если ещё валиден)
  | { type: 'searchOk'; response: FacesResponse; reset?: boolean; requestId: number }
  // Отказ поиска: код + текст для плашки (busy/provider_unavailable/…)
  | { type: 'searchError'; code: string; text: string; requestId: number }
  // 409 faces_changed: свежий список, выбор лица и ВСЕ назначения сбрасываются (безопаснее)
  | { type: 'facesChanged'; response: FacesResponse; requestId: number }
  // Выбрать/снять лицо (n)
  | { type: 'selectFace'; n: number | null }
  // Выбрать/снять персонажа (slug)
  | { type: 'selectCharacter'; slug: string | null }
  // Назначить персонажа лицу (создаёт/заменяет; active сбрасывается)
  | { type: 'assign'; face: number; slug: string }
  // Снять назначение с лица (освобождает персонажа)
  | { type: 'remove'; face: number }
  // Тап по лицу (активация / переназначение / лимит)
  | { type: 'tap'; face: number }
  // Сбросить все назначения (кисть работает с одним лицом)
  | { type: 'clearAssignments' }
  // Размонтирование хука: обнуляет requestSeq, чтобы висящий ответ не применился
  | { type: 'unmount' };

// Чистый редьюсер. reset (searchOk) — сбросить выбор лица; facesChanged — всегда сбрасывает.
// searchOk/searchError/facesChanged применяются только если requestId совпадает с requestSeq
// (защита от out-of-order: медленный ответ первого запроса не затирает результат второго).
export function faceSwapReducer(s: FaceSwapState, a: FaceSwapAction): FaceSwapState {
  switch (a.type) {
    case 'searchStart':
      // Повторный поиск сбрасывает причину отказа (busy/provider_unavailable/…)
      return { ...s, phase: 'loading', error: null, searchFailure: null, requestSeq: s.requestSeq + 1 };
    case 'searchOk': {
      if (a.requestId !== s.requestSeq) return s;
      const keep = !a.reset && s.selectedFace != null && a.response.faces.some(f => f.n === s.selectedFace);
      return {
        ...s, phase: 'ready', faces: a.response.faces, facesKey: a.response.facesKey,
        width: a.response.width, height: a.response.height, error: null, searchFailure: null,
        selectedFace: a.reset ? null : keep ? s.selectedFace : autoSelect(a.response.faces),
        // Назначения на лица, которых нет в новом списке, отбрасываются (ревью шага 15)
        assignments: pruneAssignments(s.assignments, a.response.faces),
      };
    }
    case 'searchError': {
      if (a.requestId !== s.requestSeq) return s;
      return { ...s, phase: 'error', error: a.code, searchFailure: { code: a.code, text: a.text } };
    }
    case 'facesChanged': {
      if (a.requestId !== s.requestSeq) return s;
      // Сброс выбора лица и ВСЕХ назначений: сервер сопоставил старую рамку с новой,
      // безопаснее переспросить (409 faces_changed, дефект high приёмки этапа 2)
      return {
        ...s, phase: 'ready', faces: a.response.faces, facesKey: a.response.facesKey,
        width: a.response.width, height: a.response.height, error: null,
        selectedFace: null, assignments: emptyAssignments,
      };
    }
    case 'selectFace':
      return { ...s, selectedFace: a.n };
    case 'selectCharacter':
      return { ...s, selectedCharacter: a.slug };
    case 'assign':
      return { ...s, assignments: assignCharacter(s.assignments, a.face, a.slug) };
    case 'remove':
      return { ...s, assignments: removeAssignment(s.assignments, a.face) };
    case 'tap':
      return { ...s, assignments: tapFace(s.assignments, a.face, s.faces) };
    case 'clearAssignments':
      return { ...s, assignments: emptyAssignments };
    case 'unmount':
      return { ...s, requestSeq: 0 };
    default:
      return s;
  }
}

// canSubmit: лицо и персонаж выбраны, и исполнитель — локальные модели
export function faceSwapCanSubmit(s: FaceSwapState, provider: string | null | undefined): boolean {
  return s.selectedFace != null && s.selectedCharacter != null && provider === 'local';
}

// Хук-состояние (без разметки): обёртка над редьюсером + запрос findFaces.
// threadId/versionId — версия, на которой ищем лица; provider — исполнитель текущего запуска.
// scope/sessionId — область и чат, для маршрута findFaces (только проектная).
export function useFaceSwap(
  scope: string, sessionId: string,
  threadId: string | null, versionId: string | null,
  provider: string | null | undefined,
) {
  const [state, dispatch] = useReducer(faceSwapReducer, initialFaceSwap);
  // Счётчик запросов: инкремент в search, обнуление при размонтировании.
  // Держим в ref (не в state), чтобы два последовательных search в одном тике
  // получили разные номера — state ещё не перерисовался.
  const seqRef = useRef(0);

  // Размонтирование: обнуляем requestSeq, чтобы висящий ответ не применился
  useEffect(() => () => { seqRef.current = 0; dispatch({ type: 'unmount' }); }, []);

  const search = useCallback(async (): Promise<void> => {
    if (!threadId || !versionId) return;
    seqRef.current += 1;
    const mySeq = seqRef.current;
    dispatch({ type: 'searchStart' });
    try {
      const res = await findFaces(scope, sessionId, threadId, versionId);
      dispatch({ type: 'searchOk', response: res, requestId: mySeq });
    } catch (e) {
      const err = facesError(e);
      if (err.code === 'faces_changed' && err.faces) {
        dispatch({ type: 'facesChanged', response: err.faces, requestId: mySeq });
      } else {
        // Отказ поиска (busy/provider_unavailable/…): причина с текстом — для плашки шага 16
        dispatch({ type: 'searchError', code: err.code, text: searchFailureText(err.code), requestId: mySeq });
      }
    }
  }, [scope, sessionId, threadId, versionId]);

  // 409 faces_changed при ЗАПУСКЕ: тело — свежий ImageThreadFacesResponse (facesKey + faces).
  // Обновляем ключ/список и сбрасываем ВСЕ назначения (редьюсер facesChanged) — иначе повторный
  // запуск уходит со старым facesKey → вечный цикл 409 (дефект high приёмки этапа 2)
  const applyFacesChanged = useCallback((response: FacesResponse) => {
    dispatch({ type: 'facesChanged', response, requestId: seqRef.current });
  }, []);

  const selectFace = useCallback((n: number | null) => dispatch({ type: 'selectFace', n }), []);
  const selectCharacter = useCallback((slug: string | null) => dispatch({ type: 'selectCharacter', slug }), []);
  const assign = useCallback((face: number, slug: string) => dispatch({ type: 'assign', face, slug }), []);
  const remove = useCallback((face: number) => dispatch({ type: 'remove', face }), []);
  const tap = useCallback((face: number) => dispatch({ type: 'tap', face }), []);
  const clearAssignments = useCallback(() => dispatch({ type: 'clearAssignments' }), []);

  return {
    state,
    canSubmit: faceSwapCanSubmit(state, provider),
    search,
    applyFacesChanged,
    selectFace,
    selectCharacter,
    assign,
    remove,
    tap,
    clearAssignments,
  };
}
