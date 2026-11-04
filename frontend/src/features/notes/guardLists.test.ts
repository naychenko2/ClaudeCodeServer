// Сторожа границы модуля «Заметки»: запрет значений из pages/workspace/panelStackState и
// panelCatalog (разрез ac431663) — вердикт ESLint-барьера и контракт списка, который
// читает и плагин сборки (modules/notes/vite.config.ts).
import { describe, expect, it } from 'vitest';
import { ESLint } from 'eslint';
import tseslint from 'typescript-eslint';
import { notesStoreImportGuard, notesWikiImportGuard } from '../../../eslint.config.js';
import { GUARD_AREAS, PAGES_FORBIDDEN } from '../../../modules/notes/guardLists.mjs';

const eslint = new ESLint({
  cwd: process.cwd(),
  overrideConfigFile: true,
  overrideConfig: [
    { files: ['**/*.{ts,tsx}'], languageOptions: { parser: tseslint.parser } },
    notesStoreImportGuard,
    notesWikiImportGuard,
  ],
});

async function messages(filePath: string, code: string) {
  const [res] = await eslint.lintText(code, { filePath });
  return res.messages.filter((m) => m.ruleId === 'no-restricted-imports');
}

describe('барьер pages/workspace для модуля «Заметки»', () => {
  it('обе области несут оба запретных модуля', () => {
    expect(PAGES_FORBIDDEN).toEqual(['workspace/panelStackState', 'workspace/panelCatalog']);
    for (const a of GUARD_AREAS) expect(a.forbiddenPages).toEqual(PAGES_FORBIDDEN);
  });

  it.each([
    ['src/features/notes/NotesPage.tsx', "import { notesPanels } from '../../pages/workspace/panelStackState';"],
    ['src/features/notes/NotesPage.tsx', "import { PANELS } from '../../pages/workspace/panelCatalog';"],
    ['src/features/notes/wiki/WikiTab.tsx', "import { notesPanels } from '../../../pages/workspace/panelStackState';"],
    ['src/features/notes/wiki/WikiTab.tsx', "import { PANELS } from '../../../pages/workspace/panelCatalog';"],
  ])('значение из %s запрещено: %s', async (file, code) => {
    expect(await messages(file, code)).toHaveLength(1);
  });

  it('import type разрешён, кит и прочие страницы не задеты', async () => {
    const f = 'src/features/notes/wiki/WikiTab.tsx';
    expect(await messages(f, "import type { PanelZone } from '../../../pages/workspace/panelStackState';")).toHaveLength(0);
    expect(await messages(f, "import { notesPanels } from 'aihome_shell/kit';")).toHaveLength(0);
    expect(await messages('src/features/notes/NotesPage.tsx', "import { x } from '../../pages/workspace/PanelZone';")).toHaveLength(0);
  });

  it('вне модуля «Заметки» барьер не действует', async () => {
    expect(await messages('src/pages/Other.tsx', "import { notesPanels } from './workspace/panelStackState';")).toHaveLength(0);
  });
});
