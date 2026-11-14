// D2 (дефект приёмки «Процессы фичи»): тач-цели редактора шаблонов на 360 px ниже 40.
// Состояние — как в замере: «Процессы фичи» (админ), открыт встроенный «Полный цикл» —
// в нём есть все четыре замеренных контрола: «‹ Процессы», «Сбросить к исходному»,
// «+ Этап», «Добавить этап». Высота читается из style отрендеренной кнопки (образец T10
// из list.test): размер md кита Button даёт minHeight 40. Окружение — то же, что у
// WorkflowTemplatesEditor.list.test: fakeDom, api и чужие хуки — заглушки.

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

afterEach(() => {
  if (mounted) act(() => mounted!.root.unmount());
  mounted = null;
});
afterAll(() => { uninstallFakeDom(); });

describe('D2: тач-цели редактора шаблонов на мобильной ширине', () => {
  it('360 px, открыт «Полный цикл»: «‹ Процессы», «Сбросить к исходному», «+ Этап», «Добавить этап» — высота ≥40', async () => {
    installFakeDom();
    const win = (globalThis as { window: { matchMedia: unknown } }).window;
    const origMql = win.matchMedia;
    // useIsMobile читает matchMedia в начальном состоянии — намеренно «мобильная ширина»
    win.matchMedia = () => ({ matches: true, addEventListener() {}, removeEventListener() {} });
    try {
      vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull]);
      const { container } = mountEditor({ isAdmin: true });
      await flush();
      const row = buttonByText(container, 'Полный цикл');
      expect(row).not.toBeNull();
      act(() => { (reactProps(row!)?.onClick as (e: unknown) => void)({}); });
      for (const label of ['‹ Процессы', 'Сбросить к исходному', '+ Этап', 'Добавить этап']) {
        const btn = buttonByText(container, label);
        expect(btn, label).not.toBeNull();
        // React дописывает 'px' к размерным свойствам — читаем число из значения
        expect(parseFloat(btn!.style.minHeight), label).toBeGreaterThanOrEqual(40);
      }
    } finally { win.matchMedia = origMql; }
  });
});
