// Панель генерации следует за выбором (docs/mockups/image-editor-v4-panel-proposal.md,
// «Панель следует за выбором», согласовано 2026-10-01).
//
// 1. Клик человека по карточке в ленте переключает ОТКРЫТУЮ панель на раздел и вкладку
//    карточки, вид панели не меняется. Закрытая не открывается: выбор просто запомнен.
// 2. Выбор агентом панель не двигает: о нём говорит метка ✦ в строке и панели «Контекст».
// Кнопки («Работать с этой», «Обработать ▾») открывают панель и закрытую — это просьба
// открыть, а не выбор; они зовут revealWorkspacePanel без ifOpen.

import { genPanelKeys, toGenPanelKey } from './genPanelKeys';
import { revealWorkspacePanel } from './subsystems/registryCore';

// Клик человека по карточке: true — открытая панель переключена (или уже на месте)
// При флаге ключ всегда chatContext, вкладок у панели нет (§Д1)
export function followSelection(panelKey: string, sessionId: string, target: string, tab = 'settings'): boolean {
  return revealWorkspacePanel(toGenPanelKey(panelKey), tab, { sessionId, target, ifOpen: true });
}

// Какую открытую панель заменить при переходе «по выбору»: соперницу из той же группы,
// чтобы новая встала на её место, а не по правилу рельсы. null — заменять нечего
export function followHost(openKeys: readonly string[], key: string): string | null {
  const gen = genPanelKeys();
  if (!gen.includes(key) || openKeys.includes(key)) return null;
  return openKeys.find(k => k !== key && gen.includes(k)) ?? null;
}

// Клик по карточке, а не по её кнопке, ссылке или полю: у тех своё действие
const INTERACTIVE = 'button, a, input, textarea, select, [role="button"], [role="menuitem"], [role="slider"], [contenteditable="true"]';
export function isCardPick(target: EventTarget | null, card: Element): boolean {
  const el = target as Element | null;
  // Клик из портала (пункт меню «В контекст ▾») всплывает по дереву React, но лежит вне DOM карточки
  if (!el || !card.contains(el)) return false;
  const hit = el.closest?.(INTERACTIVE);
  return !hit || !card.contains(hit);
}
