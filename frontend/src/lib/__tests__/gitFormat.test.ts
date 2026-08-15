import { describe, it, expect } from 'vitest';
import type { ChangedBySession } from '../../types';
import { fileChatBadge, fileMatchesChatFilter, countChatFilter } from '../gitFormat';

// external=false: в changedBy (стор lib/git) внешние правки не попадают вовсе
const entry = (sessionId: string, name: string): ChangedBySession => ({ sessionId, name, external: false });

describe('fileChatBadge — бейдж «кто менял файл» в строке файла панели «Изменения»', () => {
  // --- один чат (single / MessageSquare, без цифры) ---

  it('менял только активный чат (чужих 0) → single mine=true (контраст, без цифры)', () => {
    const sessionFiles = new Set(['src/a.ts']);
    expect(fileChatBadge('src/a.ts', undefined, sessionFiles)).toEqual({ kind: 'single', mine: true });
  });

  it('менял ровно один чужой чат, активный не трогал → single mine=false, имя чужого', () => {
    const changedBy = new Map([['src/a.ts', [entry('s1', 'Чат 1')]]]);
    const sessionFiles = new Set(['src/b.ts']);
    expect(fileChatBadge('src/a.ts', changedBy, sessionFiles)).toEqual({ kind: 'single', mine: false, name: 'Чат 1' });
  });

  // --- 2+ чатов (multi / MessagesSquare, с цифрой) ---

  it('активный + 1 чужой (2 чата) → multi mine=true count=1 (контраст, «+1»)', () => {
    const changedBy = new Map([['src/a.ts', [entry('s1', 'Чат 1')]]]);
    const sessionFiles = new Set(['src/a.ts']);
    expect(fileChatBadge('src/a.ts', changedBy, sessionFiles)).toEqual({ kind: 'multi', mine: true, count: 1, names: ['Чат 1'] });
  });

  it('активный + 2 чужих (3 чата) → multi mine=true count=2 (контраст, «+2»)', () => {
    const changedBy = new Map([['src/a.ts', [entry('s1', 'Чат 1'), entry('s2', 'Чат 2')]]]);
    const sessionFiles = new Set(['src/a.ts']);
    const badge = fileChatBadge('src/a.ts', changedBy, sessionFiles);
    expect(badge).toEqual({ kind: 'multi', mine: true, count: 2, names: ['Чат 1', 'Чат 2'] });
  });

  it('2 чужих, активный не трогал → multi mine=false count=2 (бледно, «2» без плюса)', () => {
    const changedBy = new Map([['src/a.ts', [entry('s1', 'Чат 1'), entry('s2', 'Чат 2')]]]);
    const sessionFiles = new Set(['src/b.ts']);
    expect(fileChatBadge('src/a.ts', changedBy, sessionFiles)).toEqual({ kind: 'multi', mine: false, count: 2, names: ['Чат 1', 'Чат 2'] });
  });

  // --- ничей / неопределённость ---

  it('чужих нет и файл не в sessionFiles → outside (правка мимо чатов)', () => {
    const sessionFiles = new Set(['src/b.ts']);
    expect(fileChatBadge('src/a.ts', undefined, sessionFiles)).toEqual({ kind: 'outside' });
  });

  it('sessionFiles === undefined и чужих нет → null («мой» от «ничей» не отличить)', () => {
    expect(fileChatBadge('src/a.ts', undefined, undefined)).toBeNull();
    expect(fileChatBadge('src/a.ts', new Map(), undefined)).toBeNull();
  });

  // --- sessionFiles неизвестен, но чужие есть → трактуем как «не участвую» (mine=false) ---

  it('sessionFiles undefined, 1 чужой → single mine=false (бледно)', () => {
    const changedBy = new Map([['src/a.ts', [entry('s1', 'Чат 1')]]]);
    expect(fileChatBadge('src/a.ts', changedBy, undefined)).toEqual({ kind: 'single', mine: false, name: 'Чат 1' });
  });

  it('sessionFiles undefined, 2 чужих → multi mine=false count=2 (бледно, без плюса)', () => {
    const changedBy = new Map([['src/a.ts', [entry('s1', 'Чат 1'), entry('s2', 'Чат 2')]]]);
    expect(fileChatBadge('src/a.ts', changedBy, undefined)).toEqual({ kind: 'multi', mine: false, count: 2, names: ['Чат 1', 'Чат 2'] });
  });

  // --- краевые ---

  it('путь из git status сопоставляется с sessionFiles по lowercase → мой, single mine=true', () => {
    const sessionFiles = new Set(['src/a.ts']);
    expect(fileChatBadge('SRC/A.TS', undefined, sessionFiles)).toEqual({ kind: 'single', mine: true });
  });

  it('пустой массив чужих чатов по пути (запись есть, но пуста) не считается чатом', () => {
    const changedBy = new Map<string, ChangedBySession[]>([['src/a.ts', []]]);
    expect(fileChatBadge('src/a.ts', changedBy, undefined)).toBeNull();
    expect(fileChatBadge('src/a.ts', changedBy, new Set(['src/b.ts']))).toEqual({ kind: 'outside' });
    expect(fileChatBadge('src/a.ts', changedBy, new Set(['src/a.ts']))).toEqual({ kind: 'single', mine: true });
  });
});

// Фильтр списка «Изменений» по авторству правки: пункты меню «кто менял файл».
// Корзины НЕ разбиение — файл, который трогали и активный чат, и чужой, честно
// попадает и в «этот чат», и в «другие чаты»
describe('fileMatchesChatFilter — фильтр «кто менял файл»', () => {
  const changedBy = new Map([
    ['src/mine-and-other.ts', [entry('s1', 'Чужой')]],  // мой + чужой (пересечение)
    ['src/other.ts', [entry('s1', 'Чужой')]],           // только чужой
  ]);
  const sessionFiles = new Set(['src/mine-and-other.ts', 'src/mine.ts']);

  it('«все файлы» пропускает всё, включая неопределённость (нет активного чата)', () => {
    expect(fileMatchesChatFilter('src/x.ts', undefined, undefined, 'all')).toBe(true);
    expect(fileMatchesChatFilter('src/other.ts', changedBy, sessionFiles, 'all')).toBe(true);
  });

  it('«этот чат» — файлы активного чата, включая те, что трогали и чужие', () => {
    expect(fileMatchesChatFilter('src/mine.ts', changedBy, sessionFiles, 'mine')).toBe(true);
    expect(fileMatchesChatFilter('src/mine-and-other.ts', changedBy, sessionFiles, 'mine')).toBe(true);
    expect(fileMatchesChatFilter('src/other.ts', changedBy, sessionFiles, 'mine')).toBe(false);
  });

  it('«другие чаты» — файлы с чужими правками, включая пересечение с моими', () => {
    expect(fileMatchesChatFilter('src/other.ts', changedBy, sessionFiles, 'others')).toBe(true);
    expect(fileMatchesChatFilter('src/mine-and-other.ts', changedBy, sessionFiles, 'others')).toBe(true);
    expect(fileMatchesChatFilter('src/mine.ts', changedBy, sessionFiles, 'others')).toBe(false);
  });

  it('«несколько чатов» — файл трогали 2+ чатов (то же условие, что у бейджа MessagesSquare)', () => {
    // мой + один чужой = 2 чата
    expect(fileMatchesChatFilter('src/mine-and-other.ts', changedBy, sessionFiles, 'shared')).toBe(true);
    // ровно один чат — не «несколько», ни в моём варианте, ни в чужом
    expect(fileMatchesChatFilter('src/mine.ts', changedBy, sessionFiles, 'shared')).toBe(false);
    expect(fileMatchesChatFilter('src/other.ts', changedBy, sessionFiles, 'shared')).toBe(false);
    expect(fileMatchesChatFilter('src/hand.ts', changedBy, sessionFiles, 'shared')).toBe(false);
  });

  it('«несколько чатов» ловит и двух чужих без моего участия', () => {
    const two = new Map([['src/x.ts', [entry('s1', 'Чат 1'), entry('s2', 'Чат 2')]]]);
    expect(fileMatchesChatFilter('src/x.ts', two, new Set(), 'shared')).toBe(true);
    // и без активного чата тоже: чужих видно и так
    expect(fileMatchesChatFilter('src/x.ts', two, undefined, 'shared')).toBe(true);
  });

  it('«вне чатов» — файл, которого не касался ни один чат (правка руками/Bash)', () => {
    expect(fileMatchesChatFilter('src/hand.ts', changedBy, sessionFiles, 'outside')).toBe(true);
    expect(fileMatchesChatFilter('src/mine.ts', changedBy, sessionFiles, 'outside')).toBe(false);
    expect(fileMatchesChatFilter('src/other.ts', changedBy, sessionFiles, 'outside')).toBe(false);
  });

  it('путь сопоставляется с sessionFiles по lowercase (как в бейдже)', () => {
    expect(fileMatchesChatFilter('SRC/MINE.TS', changedBy, sessionFiles, 'mine')).toBe(true);
  });

  it('без активного чата (sessionFiles undefined) «мой» и «вне чатов» не определены', () => {
    expect(fileMatchesChatFilter('src/hand.ts', changedBy, undefined, 'mine')).toBe(false);
    expect(fileMatchesChatFilter('src/hand.ts', changedBy, undefined, 'outside')).toBe(false);
    // а вот чужие определимы и без него
    expect(fileMatchesChatFilter('src/other.ts', changedBy, undefined, 'others')).toBe(true);
  });
});

describe('countChatFilter — счётчики пунктов меню фильтра', () => {
  it('считает по полному списку скоупа; пересечение попадает в обе корзины', () => {
    const changedBy = new Map([
      ['src/mine-and-other.ts', [entry('s1', 'Чужой')]],
      ['src/other.ts', [entry('s1', 'Чужой')]],
    ]);
    const sessionFiles = new Set(['src/mine-and-other.ts', 'src/mine.ts']);
    const paths = ['src/mine.ts', 'src/mine-and-other.ts', 'src/other.ts', 'src/hand.ts'];

    expect(countChatFilter(paths, changedBy, sessionFiles)).toEqual({
      all: 4,      // весь список
      mine: 2,     // mine.ts + пересечение
      others: 2,   // other.ts + пересечение
      shared: 1,   // только пересечение (2 чата)
      outside: 1,  // hand.ts
    });
  });

  it('пустой список даёт нули', () => {
    expect(countChatFilter([], new Map(), new Set())).toEqual({ all: 0, mine: 0, others: 0, shared: 0, outside: 0 });
  });
});
