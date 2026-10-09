import { describe, expect, it } from 'vitest';
import { chatToRestore, createLatestGuard, pendingChatOnPop } from './pendingProjectChat';

describe('pendingChatOnPop', () => {
  const snap = { project: { id: 'A' }, chatId: 'X' };

  it('«назад» в чат другого проекта кладёт pending — его заберёт новый WorkspacePage', () => {
    expect(pendingChatOnPop(snap, 'B', 'projects')).toBe('A|X');
  });

  it('«назад» из раздела «Чаты» в чат «спящего» проекта — тоже pending', () => {
    expect(pendingChatOnPop(snap, 'A', 'chats')).toBe('A|X');
    expect(pendingChatOnPop(snap, null, 'chats')).toBe('A|X');
  });

  it('внутри смонтированного проекта pending не нужен — снимок применит сам WorkspacePage', () => {
    expect(pendingChatOnPop(snap, 'A', 'projects')).toBeNull();
  });

  it('снимок без чата — восстанавливать нечего', () => {
    expect(pendingChatOnPop({ project: { id: 'A' } }, 'B', 'projects')).toBeNull();
    expect(pendingChatOnPop({ project: { id: 'A' }, chatId: null }, 'B', 'chats')).toBeNull();
  });
});

describe('chatToRestore', () => {
  it('снимок с уже открытым чатом не восстанавливает — так «назад» снимает запись шторки', () => {
    expect(chatToRestore('X', 'X')).toBeNull();
  });

  it('снимок с другим чатом восстанавливает его', () => {
    expect(chatToRestore('X', 'Y')).toBe('X');
    expect(chatToRestore('X', null)).toBe('X');
  });

  it('снимок без чата — восстанавливать нечего', () => {
    expect(chatToRestore(undefined, 'X')).toBeNull();
    expect(chatToRestore(null, null)).toBeNull();
  });
});

describe('createLatestGuard', () => {
  // Модель consumePendingProjectChat: запрос списка чатов, после await — проверка поколения
  const consume = async (guard: ReturnType<typeof createLatestGuard>, list: Promise<string>, applied: string[]) => {
    const gen = guard.next();
    const id = await list;
    if (!guard.isCurrent(gen)) return;
    applied.push(id);
  };
  const deferred = () => {
    let resolve!: (v: string) => void;
    const promise = new Promise<string>(r => { resolve = r; });
    return { promise, resolve };
  };

  it('ответ на устаревшее событие не применяется, даже если пришёл последним', async () => {
    const guard = createLatestGuard();
    const applied: string[] = [];
    const first = deferred(), second = deferred();
    const p1 = consume(guard, first.promise, applied);
    const p2 = consume(guard, second.promise, applied);
    second.resolve('Y');
    await p2;
    first.resolve('X');
    await p1;
    expect(applied).toEqual(['Y']);
  });

  it('после снятия стража (размонтирование) ничего не применяется', async () => {
    const guard = createLatestGuard();
    const applied: string[] = [];
    const list = deferred();
    const p = consume(guard, list.promise, applied);
    guard.dispose();
    list.resolve('X');
    await p;
    expect(applied).toEqual([]);
  });

  it('единственное событие применяется', async () => {
    const guard = createLatestGuard();
    const applied: string[] = [];
    await consume(guard, Promise.resolve('X'), applied);
    expect(applied).toEqual(['X']);
  });
});
