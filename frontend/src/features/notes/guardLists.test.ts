// Сторожа границы модуля «Заметки»: запрет значений из всего pages/** и относительного
// lib/shell-kit. Одни и те же пары гоняются через ОБА барьера — ESLint и матчер плагина
// сборки (findViolation из guardLists.mjs) — и вердикты обязаны совпасть.
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { ESLint } from 'eslint';
import tseslint from 'typescript-eslint';
import { notesStoreImportGuard, notesWikiImportGuard } from '../../../eslint.config.js';
import { GUARD_AREAS, SRC_FORBIDDEN, findViolation } from '../../../modules/notes/guardLists.mjs';

const eslint = new ESLint({
  cwd: process.cwd(),
  overrideConfigFile: true,
  overrideConfig: [
    { files: ['**/*.{ts,tsx}'], languageOptions: { parser: tseslint.parser } },
    notesStoreImportGuard,
    notesWikiImportGuard,
  ],
});

async function eslintBlocks(filePath: string, code: string) {
  const [res] = await eslint.lintText(code, { filePath });
  return res.messages.filter((m) => m.ruleId === 'no-restricted-imports').length === 1;
}

// Вердикт плагина сборки: тот же файл, абсолютный путь, как его видит resolveId.
function buildBlocks(filePath: string, source: string) {
  return findViolation(path.resolve(process.cwd(), filePath), source) !== null;
}

// Пара проверяется обоими барьерами; возвращает вердикт, если он совпал.
async function bothBlock(filePath: string, source: string, typeOnly = false) {
  const code = `import ${typeOnly ? 'type ' : ''}{ x } from '${source}';`;
  const lint = await eslintBlocks(filePath, code);
  // import type в бандл не попадает — плагин сборки его не видит (esbuild стирает до resolveId)
  const build = typeOnly ? false : buildBlocks(filePath, source);
  expect(build, `ESLint=${lint}, сборка=${build}: ${source}`).toBe(lint);
  return lint;
}

const MODULE = 'src/features/notes/NotesPage.tsx';
const WIKI = 'src/features/notes/wiki/WikiTab.tsx';

describe('барьер pages/** и lib/shell-kit для модуля «Заметки»', () => {
  it('обе области несут один и тот же список префиксов', () => {
    expect(SRC_FORBIDDEN).toEqual(['pages', 'lib/shell-kit']);
    for (const a of GUARD_AREAS) expect(a.forbiddenSrc).toEqual(SRC_FORBIDDEN);
  });

  it.each([
    ['../../pages/workspace/PanelZone'],
    ['../../pages/workspace/PanelSlot'],
    ['../../pages/workspace/panelStackState'],
    ['../../pages/workspace/panelStackState.js'],
    ['../../pages/workspace/panelCatalog'],
    ['../../lib/shell-kit'],
    ['../../lib/shell-kit/index'],
  ])('значение из %s запрещено обоими барьерами', async (source) => {
    expect(await bothBlock(MODULE, source)).toBe(true);
    const wikiSource = source.replace('../../', '../../../');
    expect(await bothBlock(WIKI, wikiSource)).toBe(true);
  });

  it('import type, кит хоста и код вне модуля разрешены обоими барьерами', async () => {
    expect(await bothBlock(WIKI, '../../../pages/workspace/panelStackState', true)).toBe(false);
    expect(await bothBlock(WIKI, 'aihome_shell/kit')).toBe(false);
    expect(await bothBlock('src/pages/Other.tsx', './workspace/panelStackState')).toBe(false);
  });
});
