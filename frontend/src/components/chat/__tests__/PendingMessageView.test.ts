// Очередь чата: элементы, ждущие устройство локального проекта (ADR-016), помечены отдельно
// от обычных и не получают перебоя «Прервать и отправить». Рендер статикой (react-dom/server)
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { PendingMessageList } from '../PendingMessageView';
import type { PendingChatMessage } from '../../../lib/chatReducer';
import { canCancelPending } from '../../../features/projects/members/messageAuthor';

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
