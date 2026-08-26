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
import type { QuickPhrase } from '../types';

export type { QuickPhrase };

// Потолки дублируют серверные (MyQuickPhrasesController): форма гасит перебор
// до запроса, сервер всё равно режет сам — на него и опираемся как на истину
export const QUICK_PHRASE_MAX_COUNT = 24;
export const QUICK_PHRASE_MAX_LENGTH = 500;
export const QUICK_PHRASE_MAX_GROUP_LENGTH = 40;

// Перестановка строки набора (обмен с соседом). Порядок фраз = порядок в попапе,
// поэтому правится он руками в форме. Выход за границы списка — не ошибка, а
// обычный клик по крайней строке: возвращаем исходный массив нетронутым.
export function movePhrase<T>(phrases: T[], index: number, delta: number): T[] {
  const target = index + delta;
  if (index < 0 || index >= phrases.length || target < 0 || target >= phrases.length) return phrases;
  const next = [...phrases];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

// Раскладка набора по уровням попапа: корневые фразы и группы в порядке ПЕРВОГО
// появления своей фразы. Порядок набора — единственный источник порядка показа:
// сортировать группы по алфавиту нельзя, человек расставил строки руками.
// Фразы одной группы собираются вместе, даже если в списке лежат вразнобой.
export function groupQuickPhrases(phrases: QuickPhrase[]): {
  root: QuickPhrase[];
  groups: { name: string; phrases: QuickPhrase[] }[];
} {
  const root: QuickPhrase[] = [];
  const groups: { name: string; phrases: QuickPhrase[] }[] = [];
  const byName = new Map<string, { name: string; phrases: QuickPhrase[] }>();

  for (const p of phrases) {
    const name = (p.group ?? '').trim();
    if (!name) { root.push(p); continue; }
    let g = byName.get(name);
    if (!g) { g = { name, phrases: [] }; byName.set(name, g); groups.push(g); }
    g.phrases.push(p);
  }
  return { root, groups };
}

// Секция формы правки: «Без группы» (name === null) или именованная группа.
// Форма работает секциями, а не плоским списком, ровно потому, что попап
// двухуровневый: плоский список врал бы про порядок — перестановка строки через
// границу группы в попапе не двигала бы ничего (groupQuickPhrases всё равно
// собирает фразы группы вместе).
export interface QuickPhraseSection {
  id: string;
  name: string | null;
  rows: { id: string; text: string }[];
}

// Плоский набор → секции формы. Корневая секция идёт первой всегда, даже пустая:
// это приёмник для фраз без группы, и рисовать её надо до всех групп (в попапе
// корневые пункты тоже сверху).
export function toSections(phrases: QuickPhrase[], newId: () => string): QuickPhraseSection[] {
  const { root, groups } = groupQuickPhrases(phrases);
  return [
    { id: newId(), name: null, rows: root.map(p => ({ id: newId(), text: p.text })) },
    ...groups.map(g => ({
      id: newId(),
      name: g.name,
      rows: g.phrases.map(p => ({ id: newId(), text: p.text })),
    })),
  ];
}

// Секции формы → плоский набор для сервера. Пустые строки и безымянные секции
// отсеиваются здесь же: на сервере группа выводится из фраз, поэтому пустой
// секции там не существует в принципе.
export function flattenSections(sections: QuickPhraseSection[]): QuickPhrase[] {
  const out: QuickPhrase[] = [];
  for (const s of sections) {
    const name = (s.name ?? '').trim();
    for (const row of s.rows) {
      const text = row.text.trim();
      if (!text) continue;
      out.push(name ? { text, group: name } : { text });
    }
  }
  return out;
}

let _phrases: QuickPhrase[] = [];
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

export function getQuickPhrases(): QuickPhrase[] {
  return _phrases;
}

// Подписка компонента на набор фраз
export function useQuickPhrases(): QuickPhrase[] {
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
export async function saveQuickPhrases(phrases: QuickPhrase[]): Promise<void> {
  const r = await api.quickPhrases.put(phrases);
  _phrases = r.phrases ?? [];
  _loaded = true;
  _failed = false;
  emit();
}
