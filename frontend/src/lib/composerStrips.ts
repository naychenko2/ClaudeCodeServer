// Стор полос над композером (реестр `composer-strip`, хост — ComposerStripHost).
//
// Над композером живёт одна полоса за раз. Какая — решает правило старшинства
// (решение 3 разреза редактора картинок v3):
//   1. фокус: в чате выбрана картинка → полоса, которую запросил её владелец
//      («Картинки»), если человек не ушёл с неё вручную после этого запроса;
//   2. запомненная полоса чата — последний ручной выбор человека в этом чате;
//   3. Git — полоса по умолчанию;
//   4. первая доступная (проект без git).
// Недоступная полоса (модуль выключен, в проекте нет git) на любой ступени
// пропускается.
//
// Ручной выбор хранится на чат и устройство: localStorage `cc-composer-strip:{sessionId}`.
// Фокус — только в памяти: его источник правды — выбор картинки у самого чата,
// владелец полосы заново запрашивает её при входе в чат.
//
// Свёрнутость — своя у каждой полосы каждого чата (прототип полос, вариант C):
// `cc-composer-strip-collapsed:{sessionId}:{stripId}`. Пока человек не выбирал, на
// телефоне полосы свёрнуты в строку, на десктопе развёрнуты.

import { useCallback, useMemo, useSyncExternalStore } from 'react';

export const DEFAULT_STRIP = 'git';
const KEY_PREFIX = 'cc-composer-strip:';

export function stripStorageKey(sessionId: string): string {
  return KEY_PREFIX + sessionId;
}

// Запрос полосы владельцем (выбор картинки, режим композера «Картинка»).
// overridden — человек после запроса сам выбрал другую полосу: запрос в силе,
// но полосу больше не навязывает, пока его не повторят.
interface FocusRequest { stripId: string; overridden: boolean }

const _focus = new Map<string, FocusRequest>();
const COLLAPSED_PREFIX = 'cc-composer-strip-collapsed:';
// Свёрнутость, выбранная человеком: true/false; нет записи — умолчание устройства
const _collapsed = new Map<string, boolean | null>();
// Кэш прочитанного из localStorage: снапшот useSyncExternalStore обязан быть стабилен
const _remembered = new Map<string, string | null>();
let _version = 0;
const _listeners = new Set<() => void>();

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

export function subscribeComposerStrips(fn: () => void) {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}
export function getComposerStripsVersion() { return _version; }

export function getRememberedStrip(sessionId: string): string | null {
  if (_remembered.has(sessionId)) return _remembered.get(sessionId) ?? null;
  let v: string | null = null;
  try { v = localStorage.getItem(stripStorageKey(sessionId)); } catch { /* приватный режим */ }
  _remembered.set(sessionId, v);
  return v;
}

// Ручной выбор полосы человеком: запоминается на чат и снимает навязывание фокуса.
export function selectStrip(sessionId: string, stripId: string) {
  _remembered.set(sessionId, stripId);
  try { localStorage.setItem(stripStorageKey(sessionId), stripId); } catch { /* приватный режим */ }
  const f = _focus.get(sessionId);
  if (f && f.stripId !== stripId) _focus.set(sessionId, { ...f, overridden: true });
  emit();
}

// Владелец полосы просит показать её в этом чате. Повторный запрос (например,
// включили режим «Картинка») возвращает полосу, даже если человек с неё уходил.
export function requestStrip(sessionId: string, stripId: string) {
  const f = _focus.get(sessionId);
  if (f && f.stripId === stripId && !f.overridden) return;
  _focus.set(sessionId, { stripId, overridden: false });
  emit();
}

// Владелец снимает запрос (выбор картинки снят). Возврат к прежней полосе выходит
// сам: фокус запомненную полосу не переписывал.
export function releaseStrip(sessionId: string, stripId: string) {
  if (_focus.get(sessionId)?.stripId !== stripId) return;
  _focus.delete(sessionId);
  emit();
}

// Владелец режима поля ввода (слот composer-mode) сообщает, что его состояние поменялось:
// композер пересчитывает доступность и самовключение режима по этому же сигналу.
// Без него смена одной выбранной картинки на другую поле не перерисовывала — запрос
// полосы при этом не меняется
export function notifyComposer() {
  emit();
}

// Запуск режима поля ввода извне (кнопка закреплённого низа панели «Картинки»): композер чата
// отправляет текст режима своим же путём — onSubmit вклада, как по Enter. Поле пустое или
// режим не включён — композер включает режим и ставит фокус в поле. false — композера нет
const _submitters = new Map<string, (mode: string) => void>();

export function registerComposerSubmit(sessionId: string, fn: (mode: string) => void) {
  _submitters.set(sessionId, fn);
  return () => { if (_submitters.get(sessionId) === fn) _submitters.delete(sessionId); };
}

export function submitComposerMode(sessionId: string, mode: string): boolean {
  const fn = _submitters.get(sessionId);
  if (!fn) return false;
  fn(mode);
  return true;
}

// Полоса, которую запросил владелец, но человек ушёл с неё вручную: хост ставит
// точку на «▾», чтобы выбор не потерялся из виду.
export function getPendingFocus(sessionId: string): string | null {
  const f = _focus.get(sessionId);
  return f?.overridden ? f.stripId : null;
}

export function collapsedStorageKey(sessionId: string, stripId: string): string {
  return `${COLLAPSED_PREFIX}${sessionId}:${stripId}`;
}

// Чат ещё не создан — выбор живёт только в памяти
const collapsedKey = (sessionId: string | null, stripId: string) => collapsedStorageKey(sessionId ?? '', stripId);

export function isStripCollapsed(sessionId: string | null, stripId: string, isMobile: boolean): boolean {
  const key = collapsedKey(sessionId, stripId);
  if (!_collapsed.has(key)) {
    let v: boolean | null = null;
    if (sessionId) {
      try {
        const raw = localStorage.getItem(key);
        if (raw === '1' || raw === '0') v = raw === '1';
      } catch { /* приватный режим */ }
    }
    _collapsed.set(key, v);
  }
  return _collapsed.get(key) ?? isMobile;
}

export function setStripCollapsed(sessionId: string | null, stripId: string, collapsed: boolean) {
  const key = collapsedKey(sessionId, stripId);
  _collapsed.set(key, collapsed);
  if (sessionId) {
    try { localStorage.setItem(key, collapsed ? '1' : '0'); } catch { /* приватный режим */ }
  }
  emit();
}

// Чистое правило старшинства — под юнит-тестом.
export function resolveStrip(opts: {
  focus: string | null;
  remembered: string | null;
  available: readonly string[];
}): string | null {
  const { focus, remembered, available } = opts;
  const ok = (id: string | null): id is string => !!id && available.includes(id);
  if (ok(focus)) return focus;
  if (ok(remembered)) return remembered;
  if (available.includes(DEFAULT_STRIP)) return DEFAULT_STRIP;
  return available[0] ?? null;
}

// Активная полоса чата. sessionId = null (чат ещё не создан) — без запоминания и фокуса.
export function getActiveStrip(sessionId: string | null, available: readonly string[]): string | null {
  if (!sessionId) return resolveStrip({ focus: null, remembered: null, available });
  const f = _focus.get(sessionId);
  return resolveStrip({
    focus: f && !f.overridden ? f.stripId : null,
    remembered: getRememberedStrip(sessionId),
    available,
  });
}

export function useComposerStrip(sessionId: string | null, available: readonly string[], isMobile = false) {
  const version = useSyncExternalStore(subscribeComposerStrips, getComposerStripsVersion, getComposerStripsVersion);
  const availKey = available.join('|');
  // eslint-disable-next-line react-hooks/exhaustive-deps -- version и availKey в deps: пересчёт при изменении стора и состава полос
  const active = useMemo(() => getActiveStrip(sessionId, available), [sessionId, availKey, version]);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- version в deps: пересчёт при изменении стора
  const pendingFocus = useMemo(() => (sessionId ? getPendingFocus(sessionId) : null), [sessionId, version]);
  const select = useCallback((id: string) => { if (sessionId) selectStrip(sessionId, id); }, [sessionId]);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- version в deps: пересчёт при изменении стора
  const collapsed = useMemo(() => (active ? isStripCollapsed(sessionId, active, isMobile) : false), [sessionId, active, isMobile, version]);
  const setCollapsed = useCallback((v: boolean) => { if (active) setStripCollapsed(sessionId, active, v); }, [sessionId, active]);
  return { active, pendingFocus, select, collapsed, setCollapsed };
}

// Сброс состояния — только для тестов.
export function __resetComposerStrips() {
  _focus.clear();
  _remembered.clear();
  _collapsed.clear();
  emit();
}
