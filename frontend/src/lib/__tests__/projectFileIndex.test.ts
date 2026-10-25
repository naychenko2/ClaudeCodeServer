import { describe, it, expect, vi, afterEach } from 'vitest';
import { api } from '../api';
import { lookupProjectFile, locateFile, type LocatedFile, type ProjectFileIndex } from '../projectFileIndex';

const ROOT = 'C:\\Sources\\MyProject';
const INDEX: ProjectFileIndex = new Map([
  ['frontend/src/lib/design.ts', 'frontend/src/lib/design.ts'],
  ['docs/design-guidelines.md', 'docs/design-guidelines.md'],
  ['readme.md', 'README.md'],
]);

describe('lookupProjectFile', () => {
  it('относительный путь существующего файла → путь как в дереве', () => {
    expect(lookupProjectFile(INDEX, 'frontend/src/lib/design.ts', ROOT)).toBe('frontend/src/lib/design.ts');
    expect(lookupProjectFile(INDEX, './docs/design-guidelines.md', ROOT)).toBe('docs/design-guidelines.md');
  });

  it('регистр не важен — возвращается путь из дерева', () => {
    expect(lookupProjectFile(INDEX, 'Readme.md', ROOT)).toBe('README.md');
  });

  it('абсолютный путь внутри проекта → относительный', () => {
    expect(lookupProjectFile(INDEX, 'C:\\Sources\\MyProject\\frontend\\src\\lib\\design.ts', ROOT))
      .toBe('frontend/src/lib/design.ts');
  });

  it('несуществующий файл, путь вне проекта и выход за корень → null', () => {
    expect(lookupProjectFile(INDEX, 'какой-то-несуществующий.ts', ROOT)).toBeNull();
    expect(lookupProjectFile(INDEX, 'D:\\Other\\design.ts', ROOT)).toBeNull();
    expect(lookupProjectFile(INDEX, '../design.ts', ROOT)).toBeNull();
  });

  it('внешняя ссылка → null', () => {
    expect(lookupProjectFile(INDEX, 'https://claude.ai', ROOT)).toBeNull();
  });

  it('якорь и URL-экранирование снимаются', () => {
    expect(lookupProjectFile(INDEX, 'docs/design-guidelines.md#токены', ROOT)).toBe('docs/design-guidelines.md');
    expect(lookupProjectFile(INDEX, 'docs/design-guidelines%2Emd', ROOT)).toBe('docs/design-guidelines.md');
  });

  it('пустой индекс (дерево ещё не загружено) → null', () => {
    expect(lookupProjectFile(new Map(), 'frontend/src/lib/design.ts', ROOT)).toBeNull();
  });
});

describe('lookupProjectFile — голое имя и частичный путь-суффикс (B1)', () => {
  it('уникальное имя файла → путь', () => {
    expect(lookupProjectFile(INDEX, 'design.ts', ROOT)).toBe('frontend/src/lib/design.ts');
  });

  it('регистр имени не важен', () => {
    expect(lookupProjectFile(INDEX, 'Design.TS', ROOT)).toBe('frontend/src/lib/design.ts');
  });

  it('два файла с одинаковым именем → null (не гадаем)', () => {
    const idx: ProjectFileIndex = new Map([
      ['frontend/src/components/chat/chatitemview.tsx', 'frontend/src/components/chat/ChatItemView.tsx'],
      ['frontend/src/components/tasks/chatitemview.tsx', 'frontend/src/components/tasks/ChatItemView.tsx'],
    ]);
    expect(lookupProjectFile(idx, 'ChatItemView.tsx', ROOT)).toBeNull();
  });

  it('однозначный суффикс «chat/X.tsx» среди одноимённых файлов → путь', () => {
    const idx: ProjectFileIndex = new Map([
      ['frontend/src/components/chat/chatitemview.tsx', 'frontend/src/components/chat/ChatItemView.tsx'],
      ['frontend/src/components/tasks/chatitemview.tsx', 'frontend/src/components/tasks/ChatItemView.tsx'],
    ]);
    expect(lookupProjectFile(idx, 'chat/ChatItemView.tsx', ROOT)).toBe('frontend/src/components/chat/ChatItemView.tsx');
    expect(lookupProjectFile(idx, 'tasks/ChatItemView.tsx', ROOT)).toBe('frontend/src/components/tasks/ChatItemView.tsx');
  });

  it('суффикс без границы сегмента (не совпадает целым сегментом) → null', () => {
    const idx: ProjectFileIndex = new Map([
      ['frontend/src/components/somechat/chatitemview.tsx', 'frontend/src/components/somechat/ChatItemView.tsx'],
    ]);
    expect(lookupProjectFile(idx, 'hat/ChatItemView.tsx', ROOT)).toBeNull();
  });

  it('версия («v1.2.3») и домен («example.com») при отсутствии в индексе → null', () => {
    expect(lookupProjectFile(INDEX, 'v1.2.3', ROOT)).toBeNull();
    expect(lookupProjectFile(INDEX, 'example.com', ROOT)).toBeNull();
  });

  it('полный относительный путь приоритетнее суффиксного совпадения', () => {
    const idx: ProjectFileIndex = new Map([
      ['frontend/src/lib/design.ts', 'frontend/src/lib/design.ts'],
      ['other/design.ts', 'other/design.ts'],
    ]);
    expect(lookupProjectFile(idx, 'frontend/src/lib/design.ts', ROOT)).toBe('frontend/src/lib/design.ts');
  });
});

// Пакетный кэш locate (F1b): дебаунс 50 мс, пакет ≤ 50, TTL 60 с, отрицательные
// ответы тоже кэшируются, ключ — путь + sessionId. Каждый тест берёт свой projectId,
// чтобы состояние модуля не перетекло между кейсами.
function spyLocate(responder: (paths: string[]) => LocatedFile[]) {
  const calls: string[][] = [];
  vi.spyOn(api.files, 'locate').mockImplementation((projectId: string, paths: string[]) => {
    calls.push(paths);
    return Promise.resolve(responder(paths));
  });
  return calls;
}

describe('locateFile — пакетный кэш', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('10 упоминаний за 50 мс → один вызов locate', async () => {
    vi.useFakeTimers();
    const calls = spyLocate(() => []);
    const promises: Promise<LocatedFile | null>[] = [];
    for (let i = 0; i < 10; i++) {
      promises.push(locateFile('proj-debounce', `docs/a${i}.md`, 'sess-1'));
      vi.advanceTimersByTime(5); // упоминания приходят в течение 50 мс
    }
    vi.advanceTimersByTime(50); // дебаунс доливает все 10 в один пакет
    await Promise.all(promises);
    expect(calls.length).toBe(1);
    expect(calls[0].length).toBe(10);
  });

  it('60 путей → два вызова: полный пакет уходит сразу, остаток — после дебаунса', async () => {
    vi.useFakeTimers();
    const calls = spyLocate(() => []);
    const promises: Promise<LocatedFile | null>[] = [];
    for (let i = 0; i < 60; i++) promises.push(locateFile('proj-batch', `docs/b${i}.md`, 'sess-2'));
    vi.advanceTimersByTime(50);
    await Promise.all(promises);
    expect(calls.length).toBe(2);
    expect(calls[0].length).toBe(50);
    expect(calls[1].length).toBe(10);
  });

  it('отрицательный ответ повторно не запрашивается в пределах 60 с, после — снова', async () => {
    vi.useFakeTimers();
    const calls = spyLocate(() => []);
    const p1 = locateFile('proj-neg', 'docs/missing.md', 'sess-3');
    vi.advanceTimersByTime(50); // пакет уходит
    await p1;
    await locateFile('proj-neg', 'docs/missing.md', 'sess-3'); // из кэша «не найдено»
    expect(calls.length).toBe(1);
    vi.advanceTimersByTime(60_000); // TTL истёк
    const p2 = locateFile('proj-neg', 'docs/missing.md', 'sess-3');
    vi.advanceTimersByTime(50);
    await p2;
    expect(calls.length).toBe(2);
  });

  it('ключ кэша включает sessionId: один путь в двух чатах — два запроса', async () => {
    vi.useFakeTimers();
    const calls = spyLocate(() => []);
    const p1 = locateFile('proj-sess', 'docs/c.md', 'sess-a');
    const p2 = locateFile('proj-sess', 'docs/c.md', 'sess-b');
    vi.advanceTimersByTime(50);
    await Promise.all([p1, p2]);
    expect(calls.length).toBe(2);
    await locateFile('proj-sess', 'docs/c.md', 'sess-a'); // тот же чат — из кэша
    expect(calls.length).toBe(2);
  });

  it('найденный путь возвращается из кэша без второго вызова, регистр ключа не важен', async () => {
    vi.useFakeTimers();
    const found: LocatedFile = { path: 'docs/real.md', branch: 'feat/x', isAnchor: false, alternatives: 1 };
    const calls = spyLocate(() => [found]);
    const p1 = locateFile('proj-pos', 'docs/real.md', 'sess-4');
    vi.advanceTimersByTime(50);
    const first = await p1;
    expect(first).toEqual(found);
    const second = await locateFile('proj-pos', 'docs/REAL.md', 'sess-4'); // тот же ключ — из кэша
    expect(second).toEqual(found);
    expect(calls.length).toBe(1);
  });
});
