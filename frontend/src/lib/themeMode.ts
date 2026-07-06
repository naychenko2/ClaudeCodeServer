// Глобальный стор режима темы (светлая/тёмная/системная).
// Паттерн — как у featureFlags.ts: модульное состояние + подписки +
// useSyncExternalStore. Выбор хранится в localStorage; эффективная тема
// проставляется в document.documentElement.dataset.theme, откуда её читают
// CSS-переменные (см. lib/theme.css). Для 'system' слушаем prefers-color-scheme.

import { useSyncExternalStore } from 'react';

export type ThemeMode = 'light' | 'dark' | 'system';
export type EffectiveTheme = 'light' | 'dark';

const STORAGE_KEY = 'theme-mode';

const _listeners = new Set<() => void>();

// Медиа-запрос системной темы (в SSR/тестах window может отсутствовать)
const _mql =
  typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-color-scheme: dark)')
    : null;

function readStoredMode(): ThemeMode {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    if (v === 'light' || v === 'dark' || v === 'system') return v;
  } catch {
    /* localStorage недоступен — падаем на дефолт */
  }
  return 'system';
}

let _mode: ThemeMode = readStoredMode();

function emit() {
  _listeners.forEach(fn => fn());
}

// Системная тема на текущий момент
function systemTheme(): EffectiveTheme {
  return _mql?.matches ? 'dark' : 'light';
}

// Эффективная тема с учётом режима
export function getEffectiveTheme(): EffectiveTheme {
  return _mode === 'system' ? systemTheme() : _mode;
}

// Применить эффективную тему к <html>
function applyTheme() {
  if (typeof document !== 'undefined') {
    document.documentElement.dataset.theme = getEffectiveTheme();
  }
}

export function getThemeMode(): ThemeMode {
  return _mode;
}

export function setThemeMode(mode: ThemeMode) {
  _mode = mode;
  try {
    localStorage.setItem(STORAGE_KEY, mode);
  } catch {
    /* игнорируем — просто не запомнится */
  }
  applyTheme();
  emit();
}

export function subscribeThemeMode(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Реакция на смену системной темы (актуально только при mode === 'system')
if (_mql) {
  const onChange = () => {
    if (_mode === 'system') {
      applyTheme();
      emit();
    }
  };
  // addEventListener есть в современных браузерах; addListener — легаси-фолбэк
  if (typeof _mql.addEventListener === 'function') {
    _mql.addEventListener('change', onChange);
  } else if (typeof (_mql as MediaQueryList).addListener === 'function') {
    (_mql as MediaQueryList).addListener(onChange);
  }
}

// Синхронизируем DOM при загрузке модуля (на случай, если inline-скрипт
// в index.html не отработал — например, в dev или тестах).
applyTheme();

// Хук подписки компонента на выбранный режим.
export function useThemeMode(): ThemeMode {
  return useSyncExternalStore(subscribeThemeMode, getThemeMode, getThemeMode);
}
