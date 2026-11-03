// S2d-F2: тумблер «Приватный чат» — НАСТОЯЩИЙ компонент в fake-DOM (паттерн RingPillBadge).
// Проверяется граница показа (зеркало бэкового гейта ChatsController.SetVisibility),
// неактивность после первого сообщения, человеческие тексты отказов 409/403
// и поведение карты переноса вложений (S2d-6b-4F2: remap ленты + отдача наверх).
// Мутация «показывать тумблер читателю» (убрать гейт myRole==='reader') → кейс
// «читателю нет» красный; «показывать при сообщениях» → кейс «неактивен» красный.
import { createElement } from 'react';
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Project, Session } from '../../../types';
import { installFakeDom, mount, reactProps, textNodes, findNodeByAttr } from './fakeDom';

installFakeDom();

const ME = 'me-1';

// readStoredUserId: пара «токен+владелец» из одного хранилища (sessionStorage первый)
const sstore = new Map<string, string>([['cc_token', 't'], ['cc_user_id', ME]]);
(globalThis as any).sessionStorage = {
  getItem: (k: string) => sstore.get(k) ?? null,
  setItem: (k: string, v: string) => { sstore.set(k, String(v)); },
  removeItem: (k: string) => { sstore.delete(k); },
  clear: () => sstore.clear(),
  key: () => null,
  get length() { return sstore.size; },
} as Storage;

// api: подменён только setChatVisibility (класс ChatVisibilityError — настоящий,
// тексты отказов живут в api и проверяются как есть)
const apiMock = vi.hoisted(() => ({ setChatVisibility: vi.fn<(id: string, p: boolean) => Promise<Session>>() }));
const toastMock = vi.hoisted(() => vi.fn());
// remap ленты по карте переноса (S2d-6b-4F2): сам стор в этом файле не нужен —
// проверяется только факт и аргументы вызова
const remapMock = vi.hoisted(() => vi.fn());
vi.mock('../../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/api')>();
  return { ...orig, api: { ...orig.api, chats: { ...orig.api.chats, setChatVisibility: apiMock.setChatVisibility } } };
});
vi.mock('../../../lib/toast', () => ({ showToast: toastMock }));
vi.mock('../../../hooks/useSession', () => ({ remapSessionAttachedPaths: remapMock }));

import { ChatVisibilityError } from '../../../lib/api';
import { PrivateChatToggle } from '../PrivateChatToggle';

const sharedProject = { id: 'p1', name: 'Проект', myRole: 'editor' } as unknown as Project;
const ownerProject = { id: 'p1', name: 'Проект' } as unknown as Project; // личный/владелец: myRole нет

const chat = (over: Partial<Session> = {}): Session => ({
  id: 'c1', projectId: 'p1', mode: 'auto', status: 'active', messageCount: 0,
  createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', origin: 'manual',
  createdByUserId: ME, ...over,
} as Session);

// Монтаж одного экземпляра; afterEach снимает root — поповер живёт порталом в body,
// без снятия его окно осталось бы в DOM следующего кейса (и поиск ловил бы чужой)
let current: { root: ReturnType<typeof mount>['root'] } | null = null;
function render(opts: { session?: Session; project?: Project | null; hasMessages?: boolean } = {}) {
  const onSessionUpdated = vi.fn();
  const onAttachmentMoves = vi.fn();
  const m = mount(createElement(PrivateChatToggle, {
    session: opts.session ?? chat(),
    project: opts.project !== undefined ? opts.project : sharedProject,
    hasMessages: opts.hasMessages ?? false,
    online: true,
    isMobile: false,
    onSessionUpdated,
    onAttachmentMoves,
  }));
  current = m;
  return { ...m, onSessionUpdated, onAttachmentMoves };
}

// Кнопка тумблера — единственная кнопка компонента (её title меняется с состоянием,
// искать по нему нельзя)
function firstButton(n: any): any | null {
  if (n.nodeType === 1 && n.nodeName === 'BUTTON') return n;
  for (const c of n.childNodes ?? []) { const hit = firstButton(c); if (hit) return hit; }
  return null;
}
const toggleBtn = (container: ReturnType<typeof mount>['container']) => firstButton(container);

// Открыть поповер: клик по кнопке (rect — заглушка, Menu строит портал в body)
function openMenu(m: { container: ReturnType<typeof mount>['container'] }) {
  const btn = toggleBtn(m.container);
  expect(btn).not.toBeNull();
  act(() => { (reactProps(btn as never)!.onClick as (e: unknown) => void)({ currentTarget: btn }); });
}
// Поповер живёт порталом в document.body — ищем от body
const body = () => (globalThis as any).document.body;
const switchNode = () => findNodeByAttr(body(), 'aria-label', 'Приватный чат — видите только вы');

beforeEach(() => {
  apiMock.setChatVisibility.mockReset();
  toastMock.mockReset();
  remapMock.mockReset();
});
afterEach(() => {
  const c = current;
  current = null;
  if (c) act(() => { c.root.unmount(); });
});

describe('PrivateChatToggle: граница показа (только автор общего проекта, не читатель)', () => {
  it('автор в общем проекте, пустой чат — кнопка есть', () => {
    const m = render();
    expect(toggleBtn(m.container)).not.toBeNull();
  });

  it('читателю проекта — нет вовсе (бэковый 403 reader не обещается кнопкой)', () => {
    const m = render({ project: { ...sharedProject, myRole: 'reader' } as Project });
    expect(toggleBtn(m.container)).toBeNull();
    expect(textNodes(m.container).join('')).not.toContain('Приватный чат');
  });

  it('личному проекту (myRole нет) — нет: приватность нечего держать', () => {
    const m = render({ project: ownerProject });
    expect(toggleBtn(m.container)).toBeNull();
  });

  it('не автору чата (createdByUserId чужой) — нет', () => {
    const m = render({ session: chat({ createdByUserId: 'other-1' }) });
    expect(toggleBtn(m.container)).toBeNull();
  });

  it('чату без автора (старая запись) — нет: бэк таким отказывает всем', () => {
    const m = render({ session: chat({ createdByUserId: null }) });
    expect(toggleBtn(m.container)).toBeNull();
  });
});

describe('PrivateChatToggle: после первого сообщения — неактивен с подсказкой', () => {
  it('есть переписка: в поповере тумблер не переключается и стоит подсказка', () => {
    const m = render({ hasMessages: true });
    openMenu(m);
    const sw = switchNode();
    expect(sw).not.toBeNull();
    expect(sw!.getAttribute('aria-checked')).toBe('false');
    // «неактивен» проверяется поведением: клик по выключенному тумблеру не зовёт API
    act(() => { (reactProps(sw as never)!.onClick as () => void)(); });
    expect(apiMock.setChatVisibility).not.toHaveBeenCalled();
    expect(textNodes(body()).join(' ')).toContain('Сделать приватным можно только до первого сообщения');
  });

  it('тот же чат по messageCount из списка (лента ещё не загружена) — тоже неактивен', () => {
    const m = render({ session: chat({ messageCount: 3 }), hasMessages: false });
    openMenu(m);
    expect(textNodes(body()).join(' ')).toContain('Сделать приватным можно только до первого сообщения');
  });

  it('приватный чат с сообщениями — выключить МОЖНО (возврат в общий не ограничен)', async () => {
    const updated = chat({ privateToUserId: null, messageCount: 5 });
    apiMock.setChatVisibility.mockResolvedValue(updated);
    const m = render({ session: chat({ privateToUserId: ME, messageCount: 5 }) });
    // своя кнопка приватного состояния — по подсказке «видите только вы»
    const btn = findNodeByAttr(m.container, 'title', 'Этот чат видите только вы. Нажмите, чтобы изменить.');
    expect(btn).not.toBeNull();
    act(() => { (reactProps(btn as never)!.onClick as (e: unknown) => void)({ currentTarget: btn }); });
    const sw = switchNode();
    expect(sw).not.toBeNull();
    expect(sw!.getAttribute('aria-checked')).toBe('true');
    await act(async () => { (reactProps(sw as never)!.onClick as () => void)(); });
    expect(apiMock.setChatVisibility).toHaveBeenCalledWith('c1', false);
  });
});

describe('PrivateChatToggle: отказ бэка — человек читает текст, не код', () => {
  it('409 chat_has_messages → «В чате уже есть сообщения…», не код и не статус', async () => {
    apiMock.setChatVisibility.mockRejectedValue(new ChatVisibilityError(409, { code: 'chat_has_messages' }));
    const m = render();
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('В чате уже есть сообщения — приватным его не сделать');
    expect(text).not.toContain('409');
    expect(text).not.toContain('chat_has_messages');
  });

  it('403 reader → «Читатель проекта не меняет видимость чата»', async () => {
    apiMock.setChatVisibility.mockRejectedValue(new ChatVisibilityError(403, { code: 'reader' }));
    const m = render();
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Читатель проекта не меняет видимость чата');
  });

  it('409 not_shared_project → «Приватным можно сделать только чат общего проекта»', async () => {
    apiMock.setChatVisibility.mockRejectedValue(new ChatVisibilityError(409, { code: 'not_shared_project' }));
    const m = render();
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Приватным можно сделать только чат общего проекта');
  });

  it('409 attachments_move_failed → тост с текстом, ни лента, ни вызывающий не тронуты (тумблер остаётся в прежнем положении)', async () => {
    apiMock.setChatVisibility.mockRejectedValue(new ChatVisibilityError(409, { code: 'attachments_move_failed' }));
    const m = render();
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Не удалось перенести вложения чата — видимость не изменена');
    expect(text).not.toContain('409');
    expect(text).not.toContain('attachments_move_failed');
    // Отказ: ни onSessionUpdated, ни перестановки ленты, ни карты наверх
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
    expect(remapMock).not.toHaveBeenCalled();
    expect(m.onAttachmentMoves).not.toHaveBeenCalled();
  });
});

describe('PrivateChatToggle: успех — ответ бэка становится истиной вызывающего', () => {
  it('включение: PUT с {true}, ответ (Session с privateToUserId) уходит в onSessionUpdated', async () => {
    const updated = chat({ privateToUserId: ME });
    apiMock.setChatVisibility.mockResolvedValue(updated);
    const m = render();
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(apiMock.setChatVisibility).toHaveBeenCalledWith('c1', true);
    expect(m.onSessionUpdated).toHaveBeenCalledWith(updated);
    expect(toastMock).not.toHaveBeenCalled();
  });

  it('ответ с attachmentMoves: лента переставляется, карта уходит наверх; onSessionUpdated — без поля карты', async () => {
    const moves = { 'attachments/c1/a.png': 'attachments-private/c1/a.png' };
    const updated = { ...chat({ privateToUserId: ME }), attachmentMoves: moves } as Session;
    apiMock.setChatVisibility.mockResolvedValue(updated);
    const m = render();
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    // Лента этой же сессии переставляется по карте, карта целиком — вызывающему (F3)
    expect(remapMock).toHaveBeenCalledTimes(1);
    expect(remapMock).toHaveBeenCalledWith('c1', moves);
    expect(m.onAttachmentMoves).toHaveBeenCalledTimes(1);
    expect(m.onAttachmentMoves).toHaveBeenCalledWith(moves);
    // В onSessionUpdated карта не попадает: шапка получает чистый Session
    const passed = m.onSessionUpdated.mock.calls[0]?.[0];
    expect(passed).toBeDefined();
    expect(passed.privateToUserId).toBe(ME);
    expect(passed).not.toHaveProperty('attachmentMoves');
    expect(toastMock).not.toHaveBeenCalled();
  });

  it('ответ без attachmentMoves: ни перестановки ленты, ни карты наверх', async () => {
    apiMock.setChatVisibility.mockResolvedValue(chat({ privateToUserId: ME }));
    const m = render();
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(m.onSessionUpdated).toHaveBeenCalledTimes(1);
    expect(remapMock).not.toHaveBeenCalled();
    expect(m.onAttachmentMoves).not.toHaveBeenCalled();
  });

  it('пустая карта {} — тот же no-op: перестановка только при непустой карте', async () => {
    apiMock.setChatVisibility.mockResolvedValue({ ...chat({ privateToUserId: ME }), attachmentMoves: {} } as Session);
    const m = render();
    openMenu(m);
    await act(async () => { (reactProps(switchNode() as never)!.onClick as () => void)(); });
    expect(m.onSessionUpdated).toHaveBeenCalledTimes(1);
    expect(remapMock).not.toHaveBeenCalled();
    expect(m.onAttachmentMoves).not.toHaveBeenCalled();
  });
});
