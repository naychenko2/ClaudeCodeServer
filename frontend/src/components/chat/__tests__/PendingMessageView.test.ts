// Очередь чата: элементы, ждущие устройство локального проекта (ADR-016), помечены отдельно
// от обычных и не получают перебоя «Прервать и отправить». Рендер статикой (react-dom/server)
import { describe, it, expect, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { PendingMessageList } from '../PendingMessageView';
import type { PendingChatMessage } from '../../../lib/chatReducer';
import { ChatProjectContext } from '../contexts';
import { canCancelPending } from '../../../features/projects/members/messageAuthor';

vi.mock('../../../lib/offline', async orig => ({ ...(await orig<typeof import('../../../lib/offline')>()), readStoredUserId: () => 'u-me' }));

const item = (id: string, over: Partial<PendingChatMessage> = {}): PendingChatMessage => ({
  id, text: `Сообщение ${id}`, enqueuedAt: new Date().toISOString(), kind: 'agent', ...over,
});

const render = (items: PendingChatMessage[]) =>
  renderToStaticMarkup(createElement(PendingMessageList, {
    items, sessionId: 's1', onCancel: () => {}, onPreempt: () => {},
  }));

describe('очередь чата: ожидание устройства', () => {
  it('ждущий устройство элемент помечен, обычный — нет', () => {
    expect(render([item('a', { waitingForDevice: true })])).toContain('Ждёт устройство');
    expect(render([item('a')])).not.toContain('Ждёт устройство');
  });

  it('крестик снятия есть и у ждущего устройство', () => {
    expect(render([item('a', { waitingForDevice: true })])).toContain('Не доставлять это сообщение');
  });
});

describe('очередь общего проекта: «Не доставлять» только отправителю и владельцу', () => {
  const mine = item('mine', { kind: 'user', senderUserId: 'u-me' });
  const theirs = item('theirs', { kind: 'user', senderUserId: 'u-maria' });
  const cancelMark = 'Не доставлять это сообщение';
  const renderFor = (items: PendingChatMessage[], canCancelItem: (i: PendingChatMessage) => boolean) =>
    renderToStaticMarkup(createElement(PendingMessageList, {
      items, sessionId: 's1', onCancel: () => {}, canCancelItem,
    }));
  // Правило участника — ровно то, что ChatPanel передаёт через canCancelPending
  const asEditor = (i: PendingChatMessage) => canCancelPending({ myRole: 'editor' }, i.senderUserId, 'u-me');
  const asOwner = (i: PendingChatMessage) => canCancelPending({ myRole: 'owner' }, i.senderUserId, 'u-owner');

  it('участник видит кнопку только на своём', () => {
    expect(renderFor([mine], asEditor)).toContain(cancelMark);
    expect(renderFor([theirs], asEditor)).not.toContain(cancelMark);
  });

  it('у чужого нет и значка «нет связи» — кнопка скрыта по роли, а не из-за офлайна', () => {
    expect(renderFor([theirs], asEditor)).not.toContain('нет связи');
  });

  it('владелец отменяет любое', () => {
    // Подпись кнопки встречается в разметке несколько раз (title, aria-label): сравниваем с одной
    const count = (html: string) => html.split(cancelMark).length - 1;
    expect(count(renderFor([mine], asOwner))).toBeGreaterThan(0);
    expect(count(renderFor([mine, theirs], asOwner))).toBe(2 * count(renderFor([mine], asOwner)));
  });

  it('без canCancelItem (личный проект) кнопка у всех — как раньше', () => {
    expect(render([theirs])).toContain(cancelMark);
  });
});

describe('очередь общего проекта: подпись автора', () => {
  const proj = { id: 'p1', rootPath: '/p', myRole: 'editor' as const };
  const renderIn = (items: PendingChatMessage[], project: typeof proj | null) =>
    renderToStaticMarkup(createElement(ChatProjectContext.Provider, { value: project },
      createElement(PendingMessageList, { items, sessionId: 's1' })));
  const mine = item('mine', { kind: 'user', senderUserId: 'u-me' });
  const theirs = item('theirs', { kind: 'user', senderUserId: 'u-maria' });

  it('чужая строка — запасное имя автора и аватар, не «Вы»', () => {
    const html = renderIn([theirs], proj);
    expect(html).toContain('Участник');
    expect(html).not.toContain('>Вы<');
  });

  it('своя строка — «Вы»', () => {
    expect(renderIn([mine], proj)).toContain('>Вы<');
  });

  it('личный проект — «Вы», даже при senderUserId', () => {
    expect(renderIn([theirs], { id: 'p1', rootPath: '/p' } as typeof proj)).toContain('>Вы<');
  });
});
