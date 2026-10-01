// Ожидаемое раскрытие телефонной шторки «Картинки». Событие revealWorkspacePanel ловим на
// уровне модуля: «Редактировать» из дерева файлов шлёт его ДО перехода в чат, полоса
// монтируется позже, и её собственный слушатель событие бы пропустил. Просьба с чатом ждёт
// полосу именно этого чата (смена чата у живой полосы её не гасит), без чата — любую.
// Признак «закрыл в этом чате» проверяет autoRevealGenerationPanel до события.
import { REVEAL_PANEL_EVENT, type RevealPanelDetail } from 'aihome_shell/kit';
import { IMAGES_PANEL } from '../characters/panel';

let pending: { sessionId: string | null } | null = null;
const subs = new Set<() => void>();

if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => {
    const d = (e as CustomEvent<Partial<RevealPanelDetail>>).detail;
    if (d?.key !== IMAGES_PANEL) return;
    pending = { sessionId: d.sessionId ?? null };
    subs.forEach(fn => fn());
  });
}

// Забрать просьбу для полосы чата sessionId: true — шторку поднять
export function takeSheetReveal(sessionId: string | null): boolean {
  if (!pending || (pending.sessionId !== null && pending.sessionId !== sessionId)) return false;
  pending = null;
  return true;
}

export function subscribeSheetReveal(fn: () => void): () => void {
  subs.add(fn);
  return () => { subs.delete(fn); };
}

export function __resetSheetReveal() { pending = null; }
