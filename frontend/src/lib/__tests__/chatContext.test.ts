// Чистые функции контекста чата (фича chat-context): ключ записи и проверка
// «уже в контексте» — нормализация разделителей путей, которую не видно на
// поверхностный взгляд (дерево отдаёт posix-путь, лента чата — с обратными),
// плюс поведение стора при серверном событии context_updated.
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { Me, SessionContextEntry } from '../../types';
import type { ChatContextRef } from '../chatContext/types';

// Реальный SignalR не нужен (стор me подписывается через onMessage); транспорт
// разрешённой детской ручки — мок: тест сверяет выбор ручки, а не сеть
vi.mock('../api', () => ({ api: { sessions: { getContext: vi.fn() } } }));
vi.mock('../signalr', () => ({ onMessage: vi.fn(() => () => {}) }));
vi.mock('../chatContext/api', () => ({ chatContextApi: { get: vi.fn() } }));

import {
  contextKey, inChatContext, applyContextUpdated, getChatContext, loadChatContext,
  setActiveChatForContext,
} from '../chatContext';
import { api } from '../api';
import { chatContextApi } from '../chatContext/api';
import { clearMe, setMeFromServer } from '../defaultPersona';

const getContext = vi.mocked(api.sessions.getContext);
const childGet = vi.mocked(chatContextApi.get);

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

describe('выбор ручки чтения по признаку детства (Х-W4-38)', () => {
  beforeEach(() => { getContext.mockReset(); childGet.mockReset(); clearMe(); setActiveChatForContext(null); });

  const me = (isChild: boolean): Me => ({ userId: 'u1', username: 'test', role: 'user', isChild });

  const refItem = (over: Partial<ChatContextRef> = {}): ChatContextRef => ({
    id: 'r1', kind: 'project-file', ref: { path: 'docs/a.md' }, by: 'human',
    addedAt: '2026-11-20T00:00:00Z', label: 'a.md', version: null, thumb: null,
    missing: false, role: null, usedBy: [], ...over,
  });

  it('ребёнок: только разрешённая ручка, проектная не зовётся', async () => {
    setMeFromServer(me(true));
    childGet.mockResolvedValue({ revision: 1, primary: null, refs: [refItem()] });
    await loadChatContext('p1', 'kid-1');
    expect(childGet).toHaveBeenCalledWith('kid-1');
    expect(getContext).not.toHaveBeenCalled();
    expect(getChatContext('kid-1')).toEqual([{ type: 'file', id: 'docs/a.md', title: 'a.md', missing: false }]);
  });

  it('взрослый: только проектная ручка, разрешённая не зовётся', async () => {
    setMeFromServer(me(false));
    getContext.mockResolvedValue([file()]);
    await loadChatContext('p1', 'adult-1');
    expect(getContext).toHaveBeenCalledWith('p1', 'adult-1');
    expect(childGet).not.toHaveBeenCalled();
  });

  it('в список берутся только файлы проекта: чужой вид и ref без пути в прежнюю модель не кладутся', async () => {
    setMeFromServer(me(true));
    childGet.mockResolvedValue({
      revision: 2,
      // основной объект — файл (role у primary никогда нет), референсы: файл и картинка
      primary: {
        id: 'p0', kind: 'project-file', ref: { path: 'docs/keep.md' }, by: 'human',
        addedAt: '2026-11-20T00:00:00Z', label: 'keep.md', version: null, thumb: null,
        missing: false, role: null,
      },
      refs: [refItem({ id: 'r1' }), { ...refItem({ id: 'r2' }), kind: 'image', ref: { threadId: 't1' } }],
    });
    await loadChatContext('p1', 'kid-2');
    expect(getChatContext('kid-2')).toEqual([
      { type: 'file', id: 'docs/keep.md', title: 'keep.md', missing: false },
      { type: 'file', id: 'docs/a.md', title: 'a.md', missing: false },
    ]);
  });

  it('ребёнок: GET за missing после события context_updated — тоже с разрешённой ручки', () => {
    setMeFromServer(me(true));
    setActiveChatForContext({ projectId: 'p1', sessionId: 'kid-3' });
    childGet.mockResolvedValue({ revision: 1, primary: null, refs: [] });
    applyContextUpdated({ type: 'context_updated', sessionId: 'kid-3', entries: [file()] } as never);
    expect(childGet).toHaveBeenCalledWith('kid-3');
    expect(getContext).not.toHaveBeenCalled();
  });
});
