// Режим UI-инспектора (admin-only): включил — кликаешь по любому элементу интерфейса
// и пишешь заметку с привязкой к исходнику (по data-cc-src, см. scripts/babel-cc-src.mjs).
// Модульный стор по паттерну lib/notes.ts: App монтирует оверлей при enabled.

import { useSyncExternalStore } from 'react';

let _enabled = false;
let _admin = false;   // выставляет App по auth.role — без него тумблер и хоткей инертны
const _listeners = new Set<() => void>();

function emit() { _listeners.forEach(fn => fn()); }

export function toggleUiInspector(): void {
  if (!_admin) return;
  _enabled = !_enabled;
  emit();
}

// Выключение из самого оверлея (Esc, плавающая кнопка) — без проверки admin:
// раз режим включён, выключить его можно всегда
export function disableUiInspector(): void {
  if (!_enabled) return;
  _enabled = false;
  emit();
}

// App вызывает при установке auth; сброс роли (logout) гасит и сам режим
export function setUiInspectorAdmin(isAdmin: boolean): void {
  _admin = isAdmin;
  if (!isAdmin && _enabled) { _enabled = false; emit(); }
}

export function useUiInspector(): boolean {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _enabled,
    () => _enabled,
  );
}

// Глобальный хоткей Ctrl+Alt+I — App регистрирует один раз при старте.
// e.code вместо e.key: с зажатым Alt раскладка может отдать другой символ.
let _hotkeyWired = false;
export function wireUiInspectorHotkey(): void {
  if (_hotkeyWired || typeof window === 'undefined') return;
  _hotkeyWired = true;
  window.addEventListener('keydown', e => {
    if (!e.ctrlKey || !e.altKey || e.code !== 'KeyI') return;
    if (!_admin) return;
    e.preventDefault();
    toggleUiInspector();
  });
}
