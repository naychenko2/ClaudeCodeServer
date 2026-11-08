import { useCallback, useSyncExternalStore } from 'react';

// «Область панели вики» (W8): какую область раздела показывает каждая панель —
// «Входящие», «Журнал», «Здоровье». Значение структурно то же, что SectionChipValue (W7),
// — тип объявлен здесь, а не импортирован: W7 и W8 независимы (постановка W8).
// У «Настроек» области нет: они общие на всю базу (WikiSettings.tsx). По умолчанию у всех follow.
//
// Отдельный стор, а не поле panelStackState: стор ядра общий для всех экранов, и его sanitize
// пришлось бы менять для всех сразу. Здесь один ключ localStorage на все три панели —
// рядом с раскладкой зон, живёт столько же и не зависит от WikiTab.
//
// Порядок стора — как в lib/pinnedProjects.ts и features/imageEditor/thread/modeState.ts:
// модульное состояние + подписчики + useSyncExternalStore + синхронизация вкладок событием
// 'storage'. Привязка к storage — при первом обращении: в node-тестах window появляется
// после импорта модуля (тот же порядок, что bindStorage в modeState).

export const WIKI_SCOPE_KEY = 'cc_notes_wiki_scope';

export type WikiPanelKey = 'wikiInbox' | 'wikiJournal' | 'wikiHealth';
// Тот же shape, что SectionChipValue (W7), но свой тип: чип и панели не связаны импортом
export type WikiPanelScopeValue = { mode: 'follow' } | { mode: 'all' } | { mode: 'section'; key: string };

const PANEL_KEYS: readonly WikiPanelKey[] = ['wikiInbox', 'wikiJournal', 'wikiHealth'];
const FOLLOW: WikiPanelScopeValue = { mode: 'follow' };

type ScopeMap = Record<WikiPanelKey, WikiPanelScopeValue>;
const defaults = (): ScopeMap => ({ wikiInbox: FOLLOW, wikiJournal: FOLLOW, wikiHealth: FOLLOW });

// Значение из произвольного JSON: принимаем только shape контракта, чужое — null
// (вызывающий подставит follow). Для «Здоровья» all не бывает: отчёт строится по одному
// разделу, поэтому записанный «all» (из своей или чужой вкладки) читается как follow.
function parseScopeValue(key: WikiPanelKey, raw: unknown): WikiPanelScopeValue | null {
  if (!raw || typeof raw !== 'object') return null;
  const mode = (raw as { mode?: unknown }).mode;
  if (mode === 'follow') return FOLLOW;
  if (mode === 'all') return key === 'wikiHealth' ? FOLLOW : { mode: 'all' };
  if (mode === 'section') {
    const k = (raw as { key?: unknown }).key;
    if (typeof k === 'string' && k.length > 0) return { mode: 'section', key: k };
  }
  return null;
}

function normalizeValue(key: WikiPanelKey, value: WikiPanelScopeValue): WikiPanelScopeValue {
  return key === 'wikiHealth' && value.mode === 'all' ? FOLLOW : value;
}

function sameValue(a: WikiPanelScopeValue, b: WikiPanelScopeValue): boolean {
  if (a.mode === 'section' && b.mode === 'section') return a.key === b.key;
  return a.mode === b.mode;
}

// Хранилище читается целиком: битый JSON, не-объект, бросок getItem (приватный режим) —
// всё это просто defaults(), наружу ничего не выходит
function readStored(): ScopeMap {
  const map = defaults();
  let raw: string | null;
  try { raw = localStorage.getItem(WIKI_SCOPE_KEY); } catch { return map; }
  if (!raw) return map;
  let data: unknown;
  try { data = JSON.parse(raw); } catch { return map; }
  if (!data || typeof data !== 'object') return map;
  for (const key of PANEL_KEYS) {
    const v = parseScopeValue(key, (data as Record<string, unknown>)[key]);
    if (v) map[key] = v;
  }
  return map;
}

let _state: ScopeMap = defaults();
let _loaded = false;
let _storageBound = false;
const _listeners = new Set<() => void>();
const emit = () => { _listeners.forEach((fn) => fn()); };

function bindStorage() {
  if (_storageBound || typeof window === 'undefined' || typeof window.addEventListener !== 'function') return;
  _storageBound = true;
  // Другая вкладка сменила область — перечитываем всё значение целиком; чужие ключи не наши
  window.addEventListener('storage', (e) => {
    if ((e as StorageEvent).key !== WIKI_SCOPE_KEY) return;
    _state = readStored();
    emit();
  });
}

function ensureLoaded(): ScopeMap {
  if (!_loaded) { _loaded = true; _state = readStored(); }
  bindStorage();
  return _state;
}

// Запись: читает-меняет-пишет из ТЕКУЩЕГО значения в памяти — не из снимка, взятого до
// предыдущей записи. Два set подряд по разным панелям оставляют в записи оба.
// Бросок setItem (квота, приватный режим) не выходит наружу: в памяти значение остаётся.
export function setWikiPanelScope(key: WikiPanelKey, value: WikiPanelScopeValue): void {
  const state = ensureLoaded();
  const next = normalizeValue(key, value);
  if (sameValue(state[key], next)) return;
  _state = { ...state, [key]: next };
  try { localStorage.setItem(WIKI_SCOPE_KEY, JSON.stringify(_state)); } catch { /* квота, приватный режим */ }
  emit();
}

// Что реально показывает панель: follow → ключ открытой области (null, если ничего не
// открыто); all → 'all'; section(k) → k, пока раздел есть в каталоге, иначе follow —
// раздел могли удалить, значение в хранилище при этом живёт
export function resolveWikiScope(
  value: WikiPanelScopeValue,
  ctx: { followKey: string | null; sectionKeys: string[] },
): string | null | 'all' {
  if (value.mode === 'all') return 'all';
  if (value.mode === 'section' && ctx.sectionKeys.includes(value.key)) return value.key;
  return ctx.followKey;
}

export function useWikiPanelScope(key: WikiPanelKey): [WikiPanelScopeValue, (v: WikiPanelScopeValue) => void] {
  const snapshot = () => ensureLoaded()[key];
  const value = useSyncExternalStore(subscribe, snapshot, snapshot);
  const set = useCallback((v: WikiPanelScopeValue) => setWikiPanelScope(key, v), [key]);
  return [value, set];
}

function subscribe(cb: () => void): () => void {
  _listeners.add(cb);
  return () => { _listeners.delete(cb); };
}

// Тестовый сброс: следующая читка снова идёт из localStorage — как перезагрузка страницы
export function __resetWikiPanelScope(): void {
  _loaded = false;
  _state = defaults();
  emit();
}
