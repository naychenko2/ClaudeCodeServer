// D2 (дефект приёмки «Процессы фичи»): тач-цели редактора шаблонов на 360 px ниже 40.
// Состояние — как в замере: «Процессы фичи» (админ), открыт встроенный «Полный цикл» —
// в нём есть все четыре замеренных контрола: «‹ Процессы», «Сбросить к исходному»,
// «+ Этап», «Добавить этап». Высота читается из style отрендеренной кнопки (образец T10
// из list.test): размер md кита Button даёт minHeight 40. Окружение — то же, что у
// WorkflowTemplatesEditor.list.test: fakeDom, api и чужие хуки — заглушки.
// Ф-2.12 (доработка D2): три смежные цели того же редактора, поднятые следом: «Загрузить
// vN…» и «Сохранить мои правки новым шаблоном» (плашка 409) и «Создать проектный»
// (плашка read-only в проектной области). Конфликт здесь — тот же контракт, что в
// workflowEditorLogic.test: rejection saveTemplate с status 409; проектная область —
// projectId у экрана и открытый глобальный шаблон (он read-only, кнопка есть).

import { afterAll, afterEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { WorkflowTemplateEntry } from './types';

vi.mock('./api', () => ({
  workflowTemplatesApi: {
    listTemplates: vi.fn(), saveTemplate: vi.fn(), deleteTemplate: vi.fn(), resetBuiltIn: vi.fn(),
  },
}));
vi.mock('../../lib/specialties', () => ({ useSpecialtyCatalog: () => [] }));
vi.mock('../../lib/personas', () => ({ usePersonas: () => [] }));
vi.mock('../../lib/toast', () => ({ showToast: vi.fn() }));

const { installFakeDom, mount, textNodes, findNodeByAttr, reactProps, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom');
const { WorkflowTemplatesEditor } = await import('./WorkflowTemplatesEditor');
const { workflowTemplatesApi } = await import('./api');

const stages = (n: number) => Array.from({ length: n }, () => ({ role: 'step', presence: 'on' as const }));

const globalFull: WorkflowTemplateEntry = {
  scope: 'global', projectId: null,
  template: { id: 'g1', name: 'Полный цикл', version: 4, builtInKey: 'full-cycle', stages: stages(8) },
  stageCount: 8, usageCount: 14,
};

let mounted: { root: ReturnType<typeof mount>['root'] } | null = null;

const mountEditor = (props: Record<string, unknown>) => {
  installFakeDom();
  const m = mount(createElement(WorkflowTemplatesEditor, { canWrite: true, mcpChoices: [], ...props }));
  mounted = m;
  return m;
};
// Прогнать цепочку then/catch/finally у load() внутри act — иначе setState после теста.
const flush = async () => { await act(async () => { await new Promise(r => setTimeout(r, 0)); }); };

type Node = NonNullable<ReturnType<typeof findNodeByAttr>>;
// Кнопка по тексту внутри (у этих контролов нет data-testid — искать иначе нечем)
const buttonByText = (root: ReturnType<typeof mount>['container'], text: string): Node | null => {
  let hit: Node | null = null;
  const walk = (x: Node) => {
    if (hit) return;
    if (x.nodeName === 'BUTTON' && textNodes(x).join('').includes(text)) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(root);
  return hit;
};
const click = (btn: Node) => { act(() => { (reactProps(btn)?.onClick as (e: unknown) => void)({}); }); };
// Высота — из style отрендеренной кнопки: React дописывает 'px' к размерным свойствам.
const expectHeight = (root: ReturnType<typeof mount>['container'], label: string) => {
  const btn = buttonByText(root, label);
  expect(btn, label).not.toBeNull();
  expect(parseFloat(btn!.style.minHeight), label).toBeGreaterThanOrEqual(40);
};
// useIsMobile читает matchMedia в начальном состоянии — намеренно «мобильная ширина».
const asMobile = async (fn: () => Promise<void>) => {
  const win = (globalThis as { window: { matchMedia: unknown } }).window;
  const origMql = win.matchMedia;
  win.matchMedia = () => ({ matches: true, addEventListener() {}, removeEventListener() {} });
  try { await fn(); } finally { win.matchMedia = origMql; }
};

afterEach(() => {
  if (mounted) act(() => mounted!.root.unmount());
  mounted = null;
});
afterAll(() => { uninstallFakeDom(); });

describe('D2: тач-цели редактора шаблонов на мобильной ширине', () => {
  it('360 px, открыт «Полный цикл»: «‹ Процессы», «Сбросить к исходному», «+ Этап», «Добавить этап» — высота ≥40', async () => {
    installFakeDom();
    await asMobile(async () => {
      vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull]);
      const { container } = mountEditor({ isAdmin: true });
      await flush();
      const row = buttonByText(container, 'Полный цикл');
      expect(row).not.toBeNull();
      click(row!);
      for (const label of ['‹ Процессы', 'Сбросить к исходному', '+ Этап', 'Добавить этап']) {
        expectHeight(container, label);
      }
    });
  });

  it('360 px, 409: «Загрузить v4…» и «Сохранить мои правки новым шаблоном» — высота ≥40', async () => {
    installFakeDom();
    await asMobile(async () => {
      vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull]);
      // Тот же контракт отказа, что на живом REST (api.ts): Error с status 409.
      vi.mocked(workflowTemplatesApi.saveTemplate).mockRejectedValue(
        Object.assign(new Error('Версия шаблона устарела.'), { status: 409 }));
      const { container } = mountEditor({ isAdmin: true });
      await flush();
      click(buttonByText(container, 'Полный цикл')!);
      click(buttonByText(container, 'Сохранить')!);
      await flush(); // отказ → плашка конфликта → перечитывание версии (conflictVersion = 4)
      for (const label of ['Загрузить v4 — мои правки пропадут', 'Сохранить мои правки новым шаблоном']) {
        expectHeight(container, label);
      }
    });
  });

  it('360 px, проектная область, открыт глобальный «Полный цикл»: «Создать проектный» — высота ≥40', async () => {
    installFakeDom();
    await asMobile(async () => {
      vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull]);
      const { container } = mountEditor({ projectId: 'p1', projectName: 'Тест', isAdmin: false });
      await flush();
      click(buttonByText(container, 'Полный цикл')!);
      expectHeight(container, 'Создать проектный');
    });
  });
});
