import { describe, it, expect } from 'vitest';
import { groupBySubjects } from '../chatGroups';
import { rootResolver } from '../chatTree';
import type { ProjectSubject, Session } from '../../types';

// Фабрика минимальной сессии: важны id/updatedAt/isPinned/subjectId/parentSessionId
function mk(id: string, over: Partial<Session> = {}): Session {
  return {
    id,
    mode: 'auto',
    status: 'finished',
    messageCount: 0,
    createdAt: '2026-07-20T10:00:00Z',
    updatedAt: '2026-07-20T10:00:00Z',
    origin: 'manual',
    ...over,
  } as Session;
}

const subj = (id: string, name: string, order: number, createdBy: 'ai' | 'human' = 'ai'): ProjectSubject =>
  ({ id, name, order, createdBy, createdAt: '2026-07-01T00:00:00Z' });

// Реестр нарочно не по порядку: секции идут по order, а не по позиции массива
const REGISTRY = [subj('t2', 'Фронт', 1), subj('t1', 'Деплой', 0), subj('t3', 'Пустая', 2)];

const ids = (g: { items: Session[] }) => g.items.map(s => s.id);

describe('groupBySubjects', () => {
  it('секции — в порядке реестра, хвост «Без темы» последним', () => {
    const groups = groupBySubjects([
      mk('a', { subjectId: 't2' }),
      mk('b', { subjectId: 't1' }),
      mk('c'),
    ], REGISTRY);
    expect(groups.map(g => g.title)).toEqual(['Деплой', 'Фронт', 'Без темы']);
    expect(groups[2].subject).toBeNull();
  });

  it('висячий subjectId (темы нет в реестре) — «Без темы»', () => {
    const groups = groupBySubjects([mk('a', { subjectId: 'deleted' }), mk('b', { subjectId: 't1' })], REGISTRY);
    expect(groups.map(g => g.title)).toEqual(['Деплой', 'Без темы']);
    expect(ids(groups[1])).toEqual(['a']);
  });

  it('тема без чатов секцией не рисуется', () => {
    const groups = groupBySubjects([mk('a', { subjectId: 't1' })], REGISTRY);
    expect(groups.map(g => g.subject?.id)).toEqual(['t1']);
  });

  it('закреплённый — первым внутри своей секции, остальные по дате', () => {
    const groups = groupBySubjects([
      mk('old', { subjectId: 't1', updatedAt: '2026-07-01T00:00:00Z' }),
      mk('new', { subjectId: 't1', updatedAt: '2026-07-10T00:00:00Z' }),
      mk('pinned', { subjectId: 't1', updatedAt: '2026-06-01T00:00:00Z', isPinned: true }),
    ], REGISTRY);
    expect(ids(groups[0])).toEqual(['pinned', 'new', 'old']);
  });

  it('плоский режим: дочерний чат — в секции своего корня, его собственный subjectId не читается', () => {
    const chats = [
      mk('root', { subjectId: 't2' }),
      mk('child', { parentSessionId: 'root', subjectId: 't1' }),
      mk('grandchild', { parentSessionId: 'child' }),
    ];
    const groups = groupBySubjects(chats, REGISTRY, 'newest', rootResolver(chats));
    expect(groups.map(g => g.title)).toEqual(['Фронт']);
    expect(ids(groups[0]).sort()).toEqual(['child', 'grandchild', 'root']);
  });

  it('без резолвера корня чат раскладывается по своему subjectId (вход дерева — одни корни)', () => {
    const groups = groupBySubjects([mk('child', { parentSessionId: 'root', subjectId: 't1' })], REGISTRY);
    expect(groups.map(g => g.title)).toEqual(['Деплой']);
  });
});

describe('rootResolver', () => {
  it('родителя нет в списке — чат сам себе корень', () => {
    const c = mk('c', { parentSessionId: 'gone' });
    expect(rootResolver([c])(c).id).toBe('c');
  });

  it('цикл в данных не вешает подъём', () => {
    const a = mk('a', { parentSessionId: 'b' });
    const b = mk('b', { parentSessionId: 'a' });
    expect(rootResolver([a, b])(a).id).toBe('a');
  });
});
