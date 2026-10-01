// Панель генерации следует за выбором (docs/mockups/image-editor-v4-panel-proposal.md,
// «Панель следует за выбором», согласовано 2026-10-01).
//
// 1. Клик человека по карточке в ленте переключает ОТКРЫТУЮ панель на раздел и вкладку
//    карточки, вид панели не меняется. Закрытая не открывается: выбор просто запомнен.
// 2. Выбор агентом панель не двигает: каркас показывает подсказку «Claude взял в работу ·
//    Открыть · ✕» в панели другого раздела. Человек сам выбрал тот же элемент — подсказка уходит.
// Кнопки («Работать с этой», «Обработать ▾») открывают панель и закрытую — это просьба
// открыть, а не выбор; они зовут revealWorkspacePanel без ifOpen.

import { useEffect, useSyncExternalStore } from 'react';
import { GEN_PANEL_KEYS } from './genPanelDismissed';
import { revealWorkspacePanel } from './subsystems/registryCore';

// Клик человека по карточке: true — открытая панель переключена (или уже на месте)
export function followSelection(panelKey: string, sessionId: string, target: string, tab = 'settings'): boolean {
  dropAgentPick(sessionId, target);
  return revealWorkspacePanel(panelKey, tab, { sessionId, target, ifOpen: true });
}

// Какую открытую панель заменить при переходе «по выбору»: соперницу из той же группы,
// чтобы новая встала на её место, а не по правилу рельсы. null — заменять нечего
export function followHost(openKeys: readonly string[], key: string): string | null {
  if (!GEN_PANEL_KEYS.includes(key) || openKeys.includes(key)) return null;
  return openKeys.find(k => k !== key && GEN_PANEL_KEYS.includes(k)) ?? null;
}

// Клик по карточке, а не по её кнопке, ссылке или полю: у тех своё действие
const INTERACTIVE = 'button, a, input, textarea, select, [role="button"], [role="menuitem"], [role="slider"], [contenteditable="true"]';
export function isCardPick(target: EventTarget | null, card: Element): boolean {
  const el = target as Element | null;
  const hit = el?.closest?.(INTERACTIVE);
  return !hit || !card.contains(hit);
}

// ── Выбор агентом ──

export interface AgentPick { panelKey: string; target: string; label: string; tab?: string }
// Слот шапки каркаса GenerationPanel
export interface GenerationAgentPick { label: string; onOpen: () => void; onDismiss: () => void }

const _picks = new Map<string, AgentPick>();
let _version = 0;
const _listeners = new Set<() => void>();
const emit = () => { _version++; _listeners.forEach(fn => fn()); };

// Агент взял элемент в работу (фокус пришёл событием с сервера, а не ответом на клик)
export function noteAgentPick(sessionId: string, pick: AgentPick) {
  const prev = _picks.get(sessionId);
  if (prev && prev.target === pick.target && prev.label === pick.label) return;
  _picks.set(sessionId, pick);
  emit();
}

// Подсказку убрать: ✕, «Открыть», выбор того же элемента человеком. Без target — любую
export function dropAgentPick(sessionId: string, target?: string) {
  const p = _picks.get(sessionId);
  if (!p || (target !== undefined && p.target !== target)) return;
  _picks.delete(sessionId);
  emit();
}

// Агент снял выбор в своём разделе — подсказка о нём больше не нужна
export function dropAgentPickOf(sessionId: string, panelKey: string) {
  if (_picks.get(sessionId)?.panelKey === panelKey) dropAgentPick(sessionId);
}

export const getAgentPick = (sessionId: string | null): AgentPick | null => (sessionId && _picks.get(sessionId)) || null;

// Слот agentPick для панели panelKey: подсказка видна только в панели ДРУГОГО раздела.
// Открылась панель самого выбора — подсказка своё отработала
export function useAgentPick(sessionId: string | null, panelKey: string): GenerationAgentPick | undefined {
  useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
  const own = getAgentPick(sessionId)?.panelKey === panelKey;
  useEffect(() => { if (own && sessionId) dropAgentPick(sessionId); }, [own, sessionId]);
  return agentPickSlot(sessionId, panelKey);
}

export function agentPickSlot(sessionId: string | null, panelKey: string): GenerationAgentPick | undefined {
  const pick = getAgentPick(sessionId);
  if (!pick || pick.panelKey === panelKey || !sessionId) return undefined;
  return {
    label: pick.label,
    onOpen: () => {
      dropAgentPick(sessionId);
      revealWorkspacePanel(pick.panelKey, pick.tab ?? 'settings', { sessionId, target: pick.target, ifOpen: true });
    },
    onDismiss: () => dropAgentPick(sessionId),
  };
}

export function __resetAgentPicks() {
  _picks.clear();
  _version = 0;
}
