import { useSyncExternalStore } from 'react';

// Единая ширина боковых панелей для всех областей (чаты, проекты, воркспейс).
// Меняешь в одном месте — синхронно меняется везде (общий localStorage-ключ +
// live-обновление подписчиков и синхронизация между вкладками).

const KEY = 'cc_sidebar_width';
export const SIDEBAR_MIN = 220;
export const SIDEBAR_MAX = 520;
const DEFAULT = 288;

const clamp = (n: number) => Math.max(SIDEBAR_MIN, Math.min(SIDEBAR_MAX, Math.round(n)));

let current = (() => {
  const v = Number(typeof localStorage !== 'undefined' ? localStorage.getItem(KEY) : NaN);
  return Number.isFinite(v) && v > 0 ? clamp(v) : DEFAULT;
})();

const listeners = new Set<() => void>();
const emit = () => listeners.forEach(l => l());

export function setSidebarWidth(n: number) {
  const c = clamp(n);
  if (c === current) return;
  current = c;
  try { localStorage.setItem(KEY, String(c)); } catch { /* приватный режим */ }
  emit();
}

// Синхронизация между вкладками
if (typeof window !== 'undefined') {
  window.addEventListener('storage', e => {
    if (e.key === KEY && e.newValue) {
      const v = clamp(Number(e.newValue));
      if (v !== current) { current = v; emit(); }
    }
  });
}

function subscribe(cb: () => void) { listeners.add(cb); return () => { listeners.delete(cb); }; }
function getSnapshot() { return current; }

// Хук: [ширина, установить]. Все потребители остаются в синхроне.
export function useSidebarWidth(): [number, (n: number) => void] {
  const width = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  return [width, setSidebarWidth];
}
