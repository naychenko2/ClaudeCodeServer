// Режим панели «Картинки» v5 на чат: «Создать» рисует новую, «Править» меняет выбранную
// (docs/mockups/image-panel-v5*, вариант 1). Из фокуса режим не вывести: после генерации в
// фокусе готовая картинка, а режим остаётся «Создать», — поэтому он хранится отдельно, в
// памяти и в localStorage устройства, вкладки синхронизируются событием storage.
//
// Переходы: выбор картинки человеком → «Править»; «Создать» и черновик «Новая картинка» →
// «Создать»; снятие выбора (кем угодно) → «Создать»; выбор агентом режим не меняет.
// Всё это — только при флаге image-panel-v5 (modeAware): без него режим не пишется вовсе.

import { useSyncExternalStore } from 'react';
import { FLAGS, getFlag } from 'aihome_shell/kit';

export type ImageMode = 'create' | 'edit';

const key = (sessionId: string) => `cc-image-mode:${sessionId}`;
const isMode = (v: unknown): v is ImageMode => v === 'create' || v === 'edit';

// Новая панель включена: режим и выбор по режиму действуют
export const modeAware = () => getFlag(FLAGS.imagePanelV5);

const _modes = new Map<string, ImageMode | null>();
let _version = 0;
const _listeners = new Set<() => void>();
let _storageBound = false;

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

function bindStorage() {
  if (_storageBound || typeof window === 'undefined' || typeof window.addEventListener !== 'function') return;
  _storageBound = true;
  // Другая вкладка сменила режим чата — подхватываем
  window.addEventListener('storage', e => {
    const ev = e as StorageEvent;
    if (!ev.key?.startsWith('cc-image-mode:')) return;
    const sessionId = ev.key.slice('cc-image-mode:'.length);
    const next = isMode(ev.newValue) ? ev.newValue : null;
    if (_modes.get(sessionId) === next) return;
    _modes.set(sessionId, next);
    emit();
  });
}

// Запомненный режим чата; null — ещё не выбирали
export function getStoredImageMode(sessionId: string | null): ImageMode | null {
  if (!sessionId) return null;
  bindStorage();
  if (_modes.has(sessionId)) return _modes.get(sessionId) ?? null;
  let v: ImageMode | null = null;
  try {
    const raw = localStorage.getItem(key(sessionId));
    v = isMode(raw) ? raw : null;
  } catch { /* приватный режим */ }
  _modes.set(sessionId, v);
  return v;
}

export function setImageMode(sessionId: string, mode: ImageMode) {
  bindStorage();
  if (getStoredImageMode(sessionId) === mode) return;
  _modes.set(sessionId, mode);
  try { localStorage.setItem(key(sessionId), mode); } catch { /* приватный режим */ }
  emit();
}

// Переход по правилу — только под флагом: без него поведение и хранилище прежние
export function noteImageMode(sessionId: string, mode: ImageMode) {
  if (modeAware()) setImageMode(sessionId, mode);
}

// Режим, который действует: «Править» без картинки не действует — режим «Создать».
// Не выбирали ни разу — по картинке: она выбрана, значит правим
export const effectiveImageMode = (stored: ImageMode | null, hasImage: boolean): ImageMode =>
  hasImage ? stored ?? 'edit' : 'create';

export function useImageModeVersion(): number {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
}

export function __resetImageModes() {
  _modes.clear();
  emit();
}
