// S2d-6c-F2: пилюля «История решений» в настройках нового чата — пятое место той же
// настройки (дефект 29f45c4b). Путь: новый проектный чат делают приватным (до первого
// сообщения это можно), затем в пустом состоянии выключают «Не сохранять решения» →
// бэк отказывает 409 { code: "private_chat_dossiers" } (SessionsController.cs:126).
// Раньше catch в persist (NewChatSetup.tsx:55) молчал: тумблер не двигался без объяснения.
// Теперь — тот же текст, что в четырёх закрытых местах (общая точка разбора —
// chatFieldsErrorText, api.ts:1064; в компоненте нет второго литерала отказа); прочие
// ошибки — нейтральный текст, как у соседних контролов той же настройки
// (DossierOptOutButton.tsx:35-39, ExpiryButton.tsx:37-38). Отказ не меняет локальное
// состояние: тумблер остаётся включённым, onSessionUpdated не зовётся.
// Мутация «не разбирать код» (в NewChatSetup вместо chatFieldsErrorText — нейтральный
// литерал) → кейс 409 красный, два прогона; откат — зелёный.
import { createElement } from 'react';
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Project, Session } from '../../../types';
import { installFakeDom, mount, reactProps, findNodeByAttr, textNodes } from './fakeDom';

installFakeDom();

// chatUpdate: подменён только updateChatFields (разбор кода — НАСТОЯЩИЙ api:
// тексты отказов живут в api и проверяются как есть). Тип мока — реальная сигнатура
// вызова (chatUpdate.ts:38)
const updateMock = vi.hoisted(() => vi.fn<(s: Session, p: { excludeFromDossiers?: boolean | null }) => Promise<Session>>());
const toastMock = vi.hoisted(() => vi.fn());
vi.mock('../../../lib/chatUpdate', () => ({ updateChatFields: updateMock }));
vi.mock('../../../lib/toast', () => ({ showToast: toastMock }));

import { NewChatSetup } from '../NewChatSetup';

// Пилюля «История решений» есть только у проектного чата (личные в историю не пишутся)
const project = { id: 'p1', name: 'Проект' } as unknown as Project;

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

// Монтаж одного экземпляра; afterEach снимает root — панель живёт внутри контейнера,
// без снятия её DOM остался бы в следующем кейсе
let current: { root: ReturnType<typeof mount>['root'] } | null = null;
function render(session: Session) {
  const onSessionUpdated = vi.fn();
  const m = mount(createElement(NewChatSetup, {
    session,
    project,
    onSessionUpdated,
    isMobile: false,
  }));
  current = m;
  return { ...m, onSessionUpdated };
}

// Пилюля по её подписи: кнопок в экране три (срок/история/теги), различаются только
// текстом подписи — это и есть то, что человек видит на экране
function pillByLabel(n: any, label: string): any | null {
  const buttons: any[] = [];
  const walk = (x: any): void => {
    if (x.nodeType === 1 && x.nodeName === 'BUTTON') buttons.push(x);
    for (const c of x.childNodes ?? []) walk(c);
  };
  walk(n);
  return buttons.find(b => textNodes(b).join('').includes(label)) ?? null;
}

const openDossiers = (m: { container: ReturnType<typeof mount>['container'] }) => {
  const btn = pillByLabel(m.container, 'История решений');
  expect(btn).not.toBeNull();
  act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
};
const switchNode = (m: { container: ReturnType<typeof mount>['container'] }) =>
  findNodeByAttr(m.container as never, 'aria-label', 'Не сохранять решения из этого чата');

beforeEach(() => {
  updateMock.mockReset();
  toastMock.mockReset();
});
afterEach(() => {
  const c = current;
  current = null;
  if (c) act(() => { c.root.unmount(); });
});

describe('NewChatSetup: выключение opt-out в приватном чате — человек читает текст, не молчание', () => {
  // Приватный чат с уже включённым opt-out — то самое состояние из шагов дефекта:
  // его человек пытается выключить, и бэк это запрещает
  const privateChat = () => chat({ privateToUserId: 'me-1', excludeFromDossiers: true });

  it('409 private_chat_dossiers → причина и действие; тумблер остаётся включённым, без onSessionUpdated', async () => {
    const s = privateChat();
    updateMock.mockRejectedValue(httpError(409, {
      error: 'Пока чат приватный, «Не сохранять решения» нельзя выключить',
      code: 'private_chat_dossiers',
    }));
    const m = render(s);
    openDossiers(m);
    const sw = switchNode(m);
    expect(sw).not.toBeNull();
    expect(sw!.getAttribute('aria-checked')).toBe('true');
    await act(async () => { (reactProps(sw as never)!.onClick as () => void)(); });
    // Запрос ушёл именно с выключением opt-out (не с включением)
    expect(updateMock).toHaveBeenCalledWith(s, { excludeFromDossiers: false });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [title, text] = toastMock.mock.calls[0] as [string, string];
    // Заголовок — подпись той пилюли, которую человек трогал
    expect(title).toBe('История решений');
    expect(text).toBe('Пока чат приватный, решения из него не сохраняются — это нельзя выключить. Сначала сделайте чат общим.');
    expect(text).not.toContain('409');
    expect(text).not.toContain('private_chat_dossiers');
    // Отказ: значение не оптимистичное — тумблер остался включённым, наверх не ушло ничего
    expect(switchNode(m)!.getAttribute('aria-checked')).toBe('true');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('409 с незнакомым кодом → нейтральный текст (код человеку не показывается), тумблер прежний', async () => {
    updateMock.mockRejectedValue(httpError(409, { error: 'что-то другое', code: 'other_code' }));
    const m = render(privateChat());
    openDossiers(m);
    await act(async () => { (reactProps(switchNode(m) as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Не удалось изменить настройку чата');
    expect(switchNode(m)!.getAttribute('aria-checked')).toBe('true');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('ошибка без тела (сетевой сбой) → тот же нейтральный текст', async () => {
    updateMock.mockRejectedValue(new Error('сеть недоступна'));
    const m = render(privateChat());
    openDossiers(m);
    await act(async () => { (reactProps(switchNode(m) as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Не удалось изменить настройку чата');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('успех (общий чат): ответ бэка уходит наверх, тоста нет', async () => {
    const s = chat({ excludeFromDossiers: true });
    const updated = { ...s, excludeFromDossiers: false } as Session;
    updateMock.mockResolvedValue(updated);
    const m = render(s);
    openDossiers(m);
    await act(async () => { (reactProps(switchNode(m) as never)!.onClick as () => void)(); });
    expect(updateMock).toHaveBeenCalledWith(s, { excludeFromDossiers: false });
    expect(m.onSessionUpdated).toHaveBeenCalledWith(updated);
    expect(toastMock).not.toHaveBeenCalled();
  });
});
