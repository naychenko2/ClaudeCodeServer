// S2d-6c-F: opt-out «не сохранять решения» у приватного чата — бэк отказывает 409
// private_chat_dossiers (SessionsController). Человек читает текст причины и действие,
// а не нейтральное «Не удалось изменить настройку чата» (разбор кода — в
// features/chat/api.ts:53, рядом с CHAT_VISIBILITY_TEXTS там же, features/chat/api.ts:16).
// Отказ не меняет локальное
// состояние: тумблер остаётся в прежнем положении, onSessionUpdated не зовётся.
// Прочие ошибки — тот же нейтральный текст, что был.
// Мутация «не разбирать код» (chatFieldsErrorText всегда нейтральный) → первый кейс красный.
import { createElement } from 'react';
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Session } from '../../../types';
import { installFakeDom, mount, reactProps, findNodeByAttr } from './fakeDom';

installFakeDom();

// chatUpdate: подменён только updateChatFields (разбор кода — НАСТОЯЩИЙ features/chat/api:
// тексты отказов живут там же и проверяются как есть). Тип мока — реальная сигнатура
// (chatUpdate.ts:38)
const updateMock = vi.hoisted(() => vi.fn<(s: Session, p: { excludeFromDossiers?: boolean | null }) => Promise<Session>>());
const toastMock = vi.hoisted(() => vi.fn());
vi.mock('../../../lib/chatUpdate', () => ({ updateChatFields: updateMock }));
vi.mock('../../../lib/toast', () => ({ showToast: toastMock }));

import { DossierOptOutButton } from '../DossierOptOutButton';

const chat = (over: Partial<Session> = {}): Session => ({
  id: 'c1', projectId: 'p1', mode: 'auto', status: 'active', messageCount: 0,
  createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', origin: 'manual',
  ...over,
} as Session);

// Ошибка как её бросает request() (offline.ts:1664): status и тело целиком висят на Error
const httpError = (status: number, body: { error?: string; code?: string }): Error => {
  const e = new Error(body.error ?? 'error') as Error & { status?: number; body?: unknown };
  e.status = status;
  e.body = body;
  return e;
};

// Монтаж одного экземпляра; afterEach снимает root — поповер живёт порталом в body,
// без снятия его окно осталось бы в DOM следующего кейса
let current: { root: ReturnType<typeof mount>['root'] } | null = null;
function render(session: Session) {
  const onSessionUpdated = vi.fn();
  const m = mount(createElement(DossierOptOutButton, {
    session,
    isMobile: false,
    onSessionUpdated,
  }));
  current = m;
  return { ...m, onSessionUpdated };
}

// Кнопка шапки — единственная кнопка компонента (её title меняется с состоянием,
// искать по нему нельзя)
function firstButton(n: any): any | null {
  if (n.nodeType === 1 && n.nodeName === 'BUTTON') return n;
  for (const c of n.childNodes ?? []) { const hit = firstButton(c); if (hit) return hit; }
  return null;
}

// Открыть поповер: клик по кнопке (rect — заглушка, Menu строит портал в body)
function openMenu(m: { container: ReturnType<typeof mount>['container'] }) {
  const btn = firstButton(m.container);
  expect(btn).not.toBeNull();
  act(() => { (reactProps(btn as never)!.onClick as (e: unknown) => void)({ currentTarget: btn }); });
}
const body = () => (globalThis as any).document.body;
const switchNode = () => findNodeByAttr(body(), 'aria-label', 'Не сохранять решения из этого чата');

beforeEach(() => {
  updateMock.mockReset();
  toastMock.mockReset();
});
afterEach(() => {
  const c = current;
  current = null;
  if (c) act(() => { c.root.unmount(); });
});

describe('DossierOptOutButton: отказ приватного чата — человек читает текст, не код', () => {
  it('409 private_chat_dossiers → причина и действие; тумблер в прежнем положении, без onSessionUpdated', async () => {
    updateMock.mockRejectedValue(httpError(409, {
      error: 'Пока чат приватный, «Не сохранять решения» нельзя выключить',
      code: 'private_chat_dossiers',
    }));
    const m = render(chat());
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Пока чат приватный, решения из него не сохраняются — это нельзя выключить. Сначала сделайте чат общим.');
    expect(text).not.toContain('409');
    expect(text).not.toContain('private_chat_dossiers');
    // Отказ: значение не оптимистичное — тумблер остался выключен, наверх не ушло ничего
    expect(switchNode()!.getAttribute('aria-checked')).toBe('false');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('409 с незнакомым кодом → прежний нейтральный текст (код человеку не показывается)', async () => {
    updateMock.mockRejectedValue(httpError(409, { error: 'что-то другое', code: 'other_code' }));
    const m = render(chat());
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Не удалось изменить настройку чата');
    expect(switchNode()!.getAttribute('aria-checked')).toBe('false');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('ошибка без тела (сетевой сбой) → тот же нейтральный текст', async () => {
    updateMock.mockRejectedValue(new Error('сеть недоступна'));
    const m = render(chat());
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Не удалось изменить настройку чата');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('успех: ответ бэка уходит наверх, тоста нет', async () => {
    const s = chat();
    const updated = { ...s, excludeFromDossiers: true } as Session;
    updateMock.mockResolvedValue(updated);
    const m = render(s);
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(updateMock).toHaveBeenCalledWith(s, { excludeFromDossiers: true });
    expect(m.onSessionUpdated).toHaveBeenCalledWith(updated);
    expect(toastMock).not.toHaveBeenCalled();
  });
});
