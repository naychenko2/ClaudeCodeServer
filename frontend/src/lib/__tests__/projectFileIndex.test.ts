import { describe, it, expect } from 'vitest';
import { lookupProjectFile, type ProjectFileIndex } from '../projectFileIndex';

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
