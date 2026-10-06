// Черновики панелей генерации по ключу элемента («Панель следует за выбором», правило 3).
// Правка поля, ещё не ушедшая в запуск, — черновик своего элемента: клик по другой карточке
// её не сбрасывает, а пока черновик есть, каркас ставит пометку «черновик» в «Работаем с».
// Запуск забирает черновик и снимает пометку.
//
// Ключ элемента складывает вертикаль: `images:{нить}`, `sound:{нить}`. Хранится две вещи:
// текст поля ввода режима (его возвращает поле при возврате к элементу) и признак «поле
// панели изменено» — сами значения полей панели вертикаль хранит у себя по той же нити.

import { useSyncExternalStore } from 'react';

interface Draft { text: string | null; fields: boolean }
const _drafts = new Map<string, Draft>();
let _version = 0;
const _listeners = new Set<() => void>();
const emit = () => { _version++; _listeners.forEach(fn => fn()); };

function put(key: string, d: Draft) {
  const prev = _drafts.get(key);
  if (prev && prev.text === d.text && prev.fields === d.fields) return;
  if (d.text === null && !d.fields) {
    if (!prev) return;
    _drafts.delete(key);
  } else _drafts.set(key, d);
  emit();
}

// Поле панели изменено
export function noteGenDraft(key: string | null) {
  if (!key) return;
  put(key, { text: _drafts.get(key)?.text ?? null, fields: true });
}

// Текст поля ввода режима; null или пусто — текста-черновика нет
export function setGenDraftText(key: string | null, text: string | null) {
  if (!key) return;
  put(key, { text: text?.trim() ? text : null, fields: _drafts.get(key)?.fields ?? false });
}

export const getGenDraftText = (key: string | null): string | null => (key && _drafts.get(key)?.text) || null;

export const hasGenDraft = (key: string | null): boolean => !!key && _drafts.has(key);

// Запуск забрал черновик
export function clearGenDraft(key: string | null) {
  if (key) put(key, { text: null, fields: false });
}

export function useGenDraft(key: string | null): boolean {
  useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
  return hasGenDraft(key);
}

export function __resetGenDrafts() {
  _drafts.clear();
  _version = 0;
}
