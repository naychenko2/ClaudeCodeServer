// Ссылки внешнего доступа к дев-серверам (поддомен, а не путь /preview/**).
//
// Список СКВОЗНОЙ по проектам владельца, хотя показывает его проектная панель: забытая
// открытой витрина в соседнем проекте иначе осталась бы невидимой, а это ровно тот
// случай, ради которого список и нужен.
//
// Стор ОБЩИЙ (модульный, как lib/git.ts), а не состояние компонента: читателей двое —
// панель «Сервисы» (блок «Открыто наружу») и рельса воркспейса (жёлтый кружок на кнопке
// панели). С двумя независимыми копиями кружок отставал бы от панели: доступ выдали, а
// индикатор рядом показывает прежнее число до перезагрузки страницы.
import { useCallback, useEffect, useSyncExternalStore } from 'react';
import type { ExternalPreviewLink } from '../types';
import { api } from '../lib/api';

interface ExternalLinksState {
  // Пока не знаем — считаем выключенным: лучше не показать кнопку, чем показать нерабочую
  enabled: boolean;
  links: ExternalPreviewLink[];
}

let state: ExternalLinksState = { enabled: false, links: [] };
const listeners = new Set<() => void>();

function set(next: ExternalLinksState): void {
  state = next;
  listeners.forEach(l => l());
}

function subscribe(l: () => void): () => void {
  listeners.add(l);
  return () => { listeners.delete(l); };
}

export async function refreshExternalPreviewLinks(): Promise<void> {
  try {
    const r = await api.externalPreview.list();
    set({ enabled: r.enabled, links: r.links });
  } catch {
    // офлайн — оставляем прежнее состояние, кнопки не мигают
  }
}

// Отзыв показываем сразу, не дожидаясь сервера: доступ закрывается мгновенно, и
// задержка в интерфейсе выглядела бы так, будто ссылка ещё жива
export async function revokeExternalPreviewLink(jti: string): Promise<void> {
  set({ ...state, links: state.links.filter(l => l.jti !== jti) });
  try { await api.externalPreview.revoke(jti); } catch { void refreshExternalPreviewLinks(); }
}

export async function revokeAllExternalPreviewLinks(): Promise<void> {
  set({ ...state, links: [] });
  try { await api.externalPreview.revokeAll(); } catch { void refreshExternalPreviewLinks(); }
}

let started = false;

// Первичная загрузка для тех, кто список только ПОКАЗЫВАЕТ и сам его не открывает
// (кружок в рельсе). Идемпотентна — как ensureGit.
//
// Заодно перечитываем список по возврату фокуса: у ссылок конечный срок жизни, и
// протухшая оставляла бы гореть индикатор «открыто наружу» при уже закрытом доступе.
export function ensureExternalPreviewLinks(): void {
  if (started) return;
  started = true;
  void refreshExternalPreviewLinks();
  window.addEventListener('focus', () => { void refreshExternalPreviewLinks(); });
}

// Только подписка на стор, без запроса: для читателей, которым список показывает
// кто-то другой (рельса рядом с открывающей его панелью).
export function useExternalPreviewLinksState(): ExternalLinksState {
  return useSyncExternalStore(subscribe, () => state, () => state);
}

export function useExternalPreviewLinks() {
  const st = useExternalPreviewLinksState();
  const refresh = useCallback(() => refreshExternalPreviewLinks(), []);
  const revoke = useCallback((jti: string) => revokeExternalPreviewLink(jti), []);
  const revokeAll = useCallback(() => revokeAllExternalPreviewLinks(), []);
  // Открыли панель — список перечитывается: он мог измениться на другом устройстве
  useEffect(() => { void refreshExternalPreviewLinks(); }, []);
  return { enabled: st.enabled, links: st.links, refresh, revoke, revokeAll };
}
