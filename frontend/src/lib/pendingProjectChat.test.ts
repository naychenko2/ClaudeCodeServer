import { describe, expect, it } from 'vitest';
import { chatToRestore, createLatestGuard, encodePendingChat, parsePendingChat, pendingChatOnPop } from './pendingProjectChat';

describe('pendingChatOnPop', () => {
  const snap = { project: { id: 'A' }, chatId: 'X' };
  const pop = (...a: Parameters<typeof pendingChatOnPop>) => {
    const raw = pendingChatOnPop(...a);
    return raw === null ? null : parsePendingChat(raw, 'другой');
  };

  it('«назад» в чат другого проекта кладёт pending — его заберёт новый WorkspacePage', () => {
    expect(pop(snap, 'B', 'projects')).toEqual({ pid: 'A', chatId: 'X', view: { file: null, task: null } });
  });

  it('«назад» из раздела «Чаты» в чат «спящего» проекта — тоже pending', () => {
    expect(pop(snap, 'A', 'chats')).toMatchObject({ pid: 'A', chatId: 'X' });
    expect(pop(snap, null, 'chats')).toMatchObject({ pid: 'A', chatId: 'X' });
  });

  it('файл и задача снимка едут вместе с чатом — перемонтаж не подменит их сохранённым состоянием', () => {
    const withView = { ...snap, file: 'src/a|b.ts', task: 'T1' };
    expect(pop(withView, 'B', 'projects')).toEqual({ pid: 'A', chatId: 'X', view: { file: 'src/a|b.ts', task: 'T1' } });
  });

  it('внутри смонтированного проекта pending не нужен — снимок применит сам WorkspacePage', () => {
    expect(pendingChatOnPop(snap, 'A', 'projects')).toBeNull();
  });

  it('снимок без чата — восстанавливать нечего', () => {
    expect(pendingChatOnPop({ project: { id: 'A' } }, 'B', 'projects')).toBeNull();
    expect(pendingChatOnPop({ project: { id: 'A' }, chatId: null }, 'B', 'chats')).toBeNull();
  });
});

describe('parsePendingChat', () => {
  it('диплинк без вида — только чат', () => {
    expect(parsePendingChat('A|X', 'P')).toEqual({ pid: 'A', chatId: 'X', view: null });
    expect(parsePendingChat(encodePendingChat('A', 'X'), 'P')).toEqual({ pid: 'A', chatId: 'X', view: null });
  });

  it('без «projectId|» значение относится к текущему проекту', () => {
    expect(parsePendingChat('X', 'P')).toEqual({ pid: 'P', chatId: 'X', view: null });
  });

  it('битый вид не мешает восстановить чат', () => {
    expect(parsePendingChat('A|X\n{не json', 'P')).toEqual({ pid: 'A', chatId: 'X', view: null });
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
