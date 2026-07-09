import { describe, expect, it } from 'vitest';
import { matchNode, parseQuery, type QueryableNode } from './graphQuery';

const note = (over: Partial<QueryableNode> = {}): QueryableNode => ({
  title: 'План рефакторинга', source: 'personal', sourceLabel: 'Личный', ghost: false, tags: ['идея', 'Работа'], ...over,
});

describe('parseQuery', () => {
  it('разбирает голые слова как подстроки заголовка', () => {
    expect(parseQuery('план рефакт')).toEqual([
      { kind: 'title', value: 'план', negate: false },
      { kind: 'title', value: 'рефакт', negate: false },
    ]);
  });

  it('разбирает tag: и source: (регистр префикса не важен, # у тега опционален)', () => {
    expect(parseQuery('TAG:#Идея source:personal')).toEqual([
      { kind: 'tag', value: 'идея', negate: false },
      { kind: 'source', value: 'personal', negate: false },
    ]);
  });

  it('поддерживает фразы в кавычках и отрицание', () => {
    expect(parseQuery('"два слова" -tag:архив')).toEqual([
      { kind: 'title', value: 'два слова', negate: false },
      { kind: 'tag', value: 'архив', negate: true },
    ]);
  });

  it('пропускает пустые значения', () => {
    expect(parseQuery('  ')).toEqual([]);
    expect(parseQuery('""')).toEqual([]);
  });
});

describe('matchNode', () => {
  it('AND по всем термам', () => {
    expect(matchNode(note(), parseQuery('план tag:идея'))).toBe(true);
    expect(matchNode(note(), parseQuery('план tag:другое'))).toBe(false);
  });

  it('заголовок — подстрока без учёта регистра', () => {
    expect(matchNode(note(), parseQuery('РЕФАКТОР'))).toBe(true);
  });

  it('тег — точное совпадение, не подстрока', () => {
    expect(matchNode(note(), parseQuery('tag:иде'))).toBe(false);
    expect(matchNode(note(), parseQuery('tag:работа'))).toBe(true);
  });

  it('источник — ключ целиком или подстрока подписи', () => {
    expect(matchNode(note(), parseQuery('source:personal'))).toBe(true);
    expect(matchNode(note(), parseQuery('source:личн'))).toBe(true);
    expect(matchNode(note(), parseQuery('source:pers'))).toBe(false);
  });

  it('отрицание инвертирует терм', () => {
    expect(matchNode(note(), parseQuery('-tag:архив'))).toBe(true);
    expect(matchNode(note(), parseQuery('-tag:идея'))).toBe(false);
  });

  it('ghost: только заголовок; tag:/source: ложны, их отрицание истинно', () => {
    const g = note({ ghost: true, tags: undefined });
    expect(matchNode(g, parseQuery('план'))).toBe(true);
    expect(matchNode(g, parseQuery('tag:идея'))).toBe(false);
    expect(matchNode(g, parseQuery('source:personal'))).toBe(false);
    expect(matchNode(g, parseQuery('-tag:идея'))).toBe(true);
  });
});
