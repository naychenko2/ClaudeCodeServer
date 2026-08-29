// Чистые функции контекста чата (фича chat-context): ключ записи и проверка
// «уже в контексте» — нормализация разделителей путей, которую не видно на
// поверхностный взгляд (дерево отдаёт posix-путь, лента чата — с обратными),
// плюс поведение стора при серверном событии context_updated.
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { SessionContextEntry } from '../../types';

vi.mock('../api', () => ({ api: { sessions: { getContext: vi.fn() } } }));

import {
  contextKey, inChatContext, applyContextUpdated, getChatContext,
} from '../chatContext';
import { api } from '../api';

const getContext = vi.mocked(api.sessions.getContext);

const file = (over: Partial<SessionContextEntry> = {}): SessionContextEntry =>
  ({ type: 'file', id: 'docs/a.md', ...over });

describe('contextKey: нормализация записи', () => {
  it('файлы с разными разделителями — один ключ', () => {
    expect(contextKey('file', 'docs\\a.md')).toBe(contextKey('file', 'docs/a.md'));
  });

  it('разделители не нормализуются у url и task (там их не бывает, а идентификатор свят)', () => {
    expect(contextKey('url', 'https://x/\\y')).not.toContain('/y');
    expect(contextKey('task', 'a\\b')).toBe('task:a\\b');
  });

  it('разные типы с одинаковым id — разные ключи', () => {
    expect(contextKey('file', 't1')).not.toBe(contextKey('task', 't1'));
  });
});

describe('inChatContext', () => {
  it('undefined-состав — «нет» (кнопка предложит добавить; PUT идемпотентен)', () => {
    expect(inChatContext(undefined, 'file', 'docs/a.md')).toBe(false);
  });

  it('совпадение с нормализацией разделителей', () => {
    const list = [file({ id: 'docs/a.md' })];
    expect(inChatContext(list, 'file', 'docs\\a.md')).toBe(true);
  });

  it('отсутствующий материал — false', () => {
    const list = [file({ id: 'docs/other.md' })];
    expect(inChatContext(list, 'file', 'docs/a.md')).toBe(false);
  });
});

describe('applyContextUpdated: серверное событие', () => {
  beforeEach(() => getContext.mockReset());

  const msg = (entries: SessionContextEntry[], sessionId = 's1') =>
    ({ type: 'context_updated', sessionId, entries }) as never;

  it('кеш обновляется полным составом события без перезагрузки', () => {
    applyContextUpdated(msg([file()]));
    expect(getChatContext('s1')).toEqual([file()]);
  });

  it('пустой состав события — пустой кеш (не undefined: «грузили и он пуст»)', () => {
    applyContextUpdated(msg([]));
    expect(getChatContext('s1')).toEqual([]);
  });

  it('событие по чужому чату не трогает кеш этого', () => {
    applyContextUpdated(msg([file()], 's2'));
    expect(getChatContext('s2')).toEqual([file()]);
    expect(getChatContext('s1')).toEqual([]);
  });

  it('без активного чата GET за missing не уходит — состав из события уже в кеше', async () => {
    // activeChat в тесте не выставлен (это поле стора WorkspacePage): признак
    // missing должен догонять только ОТКРЫТЫЙ чат
    getContext.mockResolvedValue([file({ missing: true })]);
    applyContextUpdated(msg([file()]));
    expect(getContext).not.toHaveBeenCalled();
    expect(getChatContext('s1')).toEqual([file()]);
  });
});
