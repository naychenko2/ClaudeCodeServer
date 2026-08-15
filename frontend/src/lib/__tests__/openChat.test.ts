// Открытие чата по id: проектный чат уходит диплинком в воркспейс проекта,
// чат вне проектов — прежним каналом cc-open-chat, пропавший — тостом.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { Session } from '../../types';

const chatsGet = vi.fn();
vi.mock('../api', () => ({ api: { chats: { get: (id: string) => chatsGet(id) } } }));

// Окружение тестов — node (см. vitest.config.ts): подменяем window шиной событий
(globalThis as { window?: EventTarget }).window = new EventTarget();

import { openChatById } from '../openChat';

function chat(over: Partial<Session> = {}): Session {
  return {
    id: 's1', mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-08-15T10:00:00Z', updatedAt: '2026-08-15T10:00:00Z',
    ...over,
  } as Session;
}

let events: { type: string; detail: unknown }[] = [];
const capture = (e: Event) => events.push({ type: e.type, detail: (e as CustomEvent).detail });

beforeEach(() => {
  events = [];
  chatsGet.mockReset();
  window.addEventListener('cc-open-url', capture);
  window.addEventListener('cc-open-chat', capture);
  window.addEventListener('cc-local-toast', capture);
});
afterEach(() => {
  window.removeEventListener('cc-open-url', capture);
  window.removeEventListener('cc-open-chat', capture);
  window.removeEventListener('cc-local-toast', capture);
});

describe('openChatById', () => {
  it('чат проекта — диплинк #/project/{pid}/chat/{id}', async () => {
    chatsGet.mockResolvedValue(chat({ id: 'c1', projectId: 'p1' }));
    await openChatById('c1');
    expect(events).toEqual([{ type: 'cc-open-url', detail: { url: '#/project/p1/chat/c1' } }]);
  });

  // По этому флагу модалка деталей задачи закрывается: переход состоялся — уходим с глаз
  it('отвечает, состоялся ли переход', async () => {
    chatsGet.mockResolvedValue(chat({ id: 'c1', projectId: 'p1' }));
    expect(await openChatById('c1')).toBe(true);
    chatsGet.mockResolvedValue(null);
    expect(await openChatById('c1')).toBe(false);
  });

  it('чат вне проектов — прежний канал cc-open-chat', async () => {
    chatsGet.mockResolvedValue(chat({ id: 'c2' }));
    await openChatById('c2');
    expect(events).toEqual([{ type: 'cc-open-chat', detail: { chatId: 'c2' } }]);
  });

  it('чат не найден — тост, а не молчание', async () => {
    chatsGet.mockRejectedValue(new Error('404'));
    await openChatById('c3', { missingTitle: 'Чат исполнителя', missingBody: 'Чат исполнителя удалён' });
    expect(events).toEqual([{
      type: 'cc-local-toast',
      detail: { title: 'Чат исполнителя', body: 'Чат исполнителя удалён', kind: 'info' },
    }]);
  });
});
