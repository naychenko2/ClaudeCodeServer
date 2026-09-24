// Очередь чата: элементы, ждущие устройство локального проекта (ADR-016), помечены отдельно
// от обычных и не получают перебоя «Прервать и отправить». Рендер статикой (react-dom/server)
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { PendingMessageList } from '../PendingMessageView';
import type { PendingChatMessage } from '../../../lib/chatReducer';

const item = (id: string, over: Partial<PendingChatMessage> = {}): PendingChatMessage => ({
  id, text: `Сообщение ${id}`, enqueuedAt: new Date().toISOString(), kind: 'agent', ...over,
});

const render = (items: PendingChatMessage[]) =>
  renderToStaticMarkup(createElement(PendingMessageList, {
    items, sessionId: 's1', onCancel: () => {}, onPreempt: () => {},
  }));

describe('очередь чата: ожидание устройства', () => {
  it('ждущий устройство элемент помечен, обычный — нет', () => {
    expect(render([item('a', { waitingForDevice: true })])).toContain('ждёт устройство');
    expect(render([item('a')])).not.toContain('ждёт устройство');
  });

  it('крестик снятия есть и у ждущего устройство', () => {
    expect(render([item('a', { waitingForDevice: true })])).toContain('Не доставлять это сообщение');
  });
});
