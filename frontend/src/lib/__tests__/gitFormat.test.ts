import { describe, it, expect } from 'vitest';
import type { ChangedBySession } from '../../types';
import { fileChatBadge } from '../gitFormat';

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
