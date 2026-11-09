// Очередь чата: элементы, ждущие устройство локального проекта (ADR-016), помечены отдельно
// от обычных и не получают перебоя «Прервать и отправить». Рендер статикой (react-dom/server)
import { describe, it, expect, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { PendingMessageList } from '../PendingMessageView';
import type { PendingChatMessage } from '../../../lib/chatReducer';
import { ChatProjectContext } from '../contexts';
import { canCancelPending } from '../../../features/projects/members/messageAuthor';

// Статический рендер кликов не знает: подменяем начальное false у useState на true,
// чтобы строка очереди отрисовалась раскрытой (флаг включается только в тестах ниже)
const forceOpen = vi.hoisted(() => ({ on: false }));
vi.mock('react', async orig => {
  const react = await orig<typeof import('react')>();
  const useState = ((init: unknown) => react.useState(forceOpen.on && init === false ? true : init)) as typeof react.useState;
  return { ...react, default: react, useState };
});

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

describe('очередь общего проекта: подпись автора (раскрытая строка)', () => {
  const proj = { id: 'p1', rootPath: '/p', myRole: 'editor' as const, formerMembers: [{ userId: 'u-old', displayName: 'Олег' }] };
  const renderIn = (items: PendingChatMessage[], project: typeof proj | null) => {
    forceOpen.on = true;
    try {
      return renderToStaticMarkup(createElement(ChatProjectContext.Provider, { value: project },
        createElement(PendingMessageList, { items, sessionId: 's1', onPreempt: () => {} })));
    } finally { forceOpen.on = false; }
  };
  const mine = item('mine', { kind: 'user', senderUserId: 'u-me' });
  const theirs = item('theirs', { kind: 'user', senderUserId: 'u-maria' });
  const former = item('former', { kind: 'user', senderUserId: 'u-old' });

  it('чужая: имя автора (запасное «Участник») и пояснение с ним, без «Вы»', () => {
    const html = renderIn([theirs], proj);
    expect(html).toContain('Уйдёт, когда агент ответит Участник');
    expect(html).not.toContain('>Вы<');
    expect(html).not.toContain('больше не в проекте');
  });

  it('бывший участник — имя из formerMembers и пометка', () => {
    const html = renderIn([former], proj);
    expect(html).toContain('Олег');
    expect(html).toContain('больше не в проекте');
  });

  it('своя — «Вы» и прежнее пояснение', () => {
    const html = renderIn([mine], proj);
    expect(html).toContain('>Вы<');
    expect(html).toContain('Уйдёт в работу, когда Claude закончит текущий шаг');
    expect(html).not.toContain('Уйдёт, когда агент ответит');
  });

  it('личный проект и строка без senderUserId — «Вы», как на master', () => {
    expect(renderIn([theirs], { id: 'p1', rootPath: '/p' } as typeof proj)).toContain('>Вы<');
    expect(renderIn([item('x', { kind: 'user' })], proj)).toContain('>Вы<');
  });
});
