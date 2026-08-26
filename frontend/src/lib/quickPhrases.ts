// Стор быстрых фраз композера: готовые сообщения, уходящие в чат одним нажатием.
// Набор per-user (один на все чаты), источник истины — сервер (users.json,
// GET/PUT /api/me/quick-phrases). Паттерн — как у contextPrefs.ts: модульное
// состояние + подписки + useSyncExternalStore.
//
// Загрузка ЛЕНИВАЯ (ensureQuickPhrases при первом открытии попапа), а не при старте
// приложения: фразы нужны ровно в момент, когда за ними полезли, и грузить их
// каждому чату на монтировании композера незачем.

import { useSyncExternalStore } from 'react';
import { api } from './api';

// Потолки дублируют серверные (MyQuickPhrasesController): форма гасит перебор
// до запроса, сервер всё равно режет сам — на него и опираемся как на истину
export const QUICK_PHRASE_MAX_COUNT = 24;
export const QUICK_PHRASE_MAX_LENGTH = 500;

let _phrases: string[] = [];
let _loaded = false;
let _failed = false;
let _loading: Promise<void> | null = null;
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

export function getQuickPhrases(): string[] {
  return _phrases;
}

// Подписка компонента на набор фраз
export function useQuickPhrases(): string[] {
  return useSyncExternalStore(subscribe, getQuickPhrases, getQuickPhrases);
}

// Был ли ответ сервера (попапу — отличить «пусто» от «ещё не знаем»)
export function quickPhrasesLoaded(): boolean {
  return _loaded;
}

// Последняя попытка загрузки провалилась (офлайн, сервер лёг): пустой список тогда
// не значит «фраз нет» — попап скажет об этом прямо, а не позовёт заводить заново
export function quickPhrasesFailed(): boolean {
  return _failed;
}

// Ленивая загрузка с дедупликацией параллельных вызовов. Ошибка не бросается:
// попап покажет её текстом, ронять композер из-за списка фраз незачем.
export function ensureQuickPhrases(): Promise<void> {
  if (_loaded) return Promise.resolve();
  if (_loading) return _loading;
  _loading = api.quickPhrases.get()
    .then(r => { _phrases = r.phrases ?? []; _loaded = true; _failed = false; })
    .catch(() => { _failed = true; })
    .finally(() => { _loading = null; emit(); });
  return _loading;
}

// Полная замена набора. Локально применяем ИТОГ сервера (он режет пустые, дубли
// и потолок) — иначе форма показывала бы то, чего в хранилище нет.
export async function saveQuickPhrases(phrases: string[]): Promise<void> {
  const r = await api.quickPhrases.put(phrases);
  _phrases = r.phrases ?? [];
  _loaded = true;
  _failed = false;
  emit();
}
