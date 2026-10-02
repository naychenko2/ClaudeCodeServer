import { useSyncExternalStore } from 'react';
import { REVEAL_PANEL_EVENT, revealWorkspacePanel } from './subsystems/registryCore';
import type { PanelReturnTo, RevealPanelDetail } from './subsystems/registryCore';

// Приём `preset` и `returnTo` из revealWorkspacePanel на стороне панели-получателя. Слушатель
// стоит на уровне модуля: закрытая панель монтируется уже ПОСЛЕ события, и собственный
// слушатель её бы его пропустил (так же, как с `tab`). Вертикаль-источник («Видео») знает
// только ключ чужой панели, чужая панель — только прозрачные объекты.
interface Entry { preset: Record<string, unknown> | null; returnTo: PanelReturnTo | null; version: number }

const entries = new Map<string, Entry>();
const subs = new Set<() => void>();
const EMPTY: Entry = { preset: null, returnTo: null, version: 0 };
const entryOf = (key: string): Entry => entries.get(key) ?? EMPTY;
const notify = () => subs.forEach(fn => fn());

function onReveal(d: Partial<RevealPanelDetail> | undefined) {
  if (!d?.key) return;
  const prev = entryOf(d.key);
  // Показ без returnTo (клик по карточке, ярлык) снимает ссылку прежнего вызова: она
  // вела бы к панели, которую человек уже не просил
  entries.set(d.key, { preset: d.preset ?? null, returnTo: d.returnTo ?? null, version: prev.version + 1 });
  notify();
}
if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => onReveal((e as CustomEvent<Partial<RevealPanelDetail>>).detail));
}

const subscribe = (fn: () => void) => { subs.add(fn); return () => { subs.delete(fn); }; };

// Ссылка «↩ …» для панели с ключом key; null — вызова с returnTo не было
export function usePanelReturnTo(key: string): PanelReturnTo | null {
  return useSyncExternalStore(subscribe, () => entryOf(key).returnTo, () => null);
}

// Ждущая заготовка (null — нет). Версия растёт с каждым показом: по ней панель понимает,
// что пришла новая заготовка, даже если прежняя ещё не разобрана
export function usePendingPreset(key: string): Record<string, unknown> | null {
  return useSyncExternalStore(subscribe, () => entryOf(key).preset, () => null);
}

// Заготовка принята — второй раз её применять нельзя
export function consumePreset(key: string) {
  const e = entries.get(key);
  if (!e?.preset) return;
  entries.set(key, { ...e, preset: null });
  notify();
}

export function dropReturnTo(key: string) {
  const e = entries.get(key);
  if (!e?.returnTo) return;
  entries.set(key, { ...e, returnTo: null });
  notify();
}

const FALLBACK: Record<string, string> = { scene: 'К сцене', film: 'К фильму' };
export const returnLabel = (r: PanelReturnTo): string => r.label ?? (r.tab ? FALLBACK[r.tab] : undefined) ?? 'Назад';

// Клик по ссылке: открыть вызвавшую панель на нужном месте и забыть ссылку
export function returnToOrigin(from: string, r: PanelReturnTo, sessionId?: string) {
  dropReturnTo(from);
  revealWorkspacePanel(r.key, r.tab, { target: r.target, sessionId });
}

export function __resetGenPanelReturn() { entries.clear(); }

// Только для тестов: состояние без React-хуков
export const __peek = (key: string) => ({ preset: entryOf(key).preset, returnTo: entryOf(key).returnTo });
