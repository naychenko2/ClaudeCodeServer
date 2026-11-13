// F23: клик по строке этапа открывает «Настройку этапа», «✕» закрывает, правка поля в панели
// меняет черновик (кнопка «Сохранить» оживает), аккордеона в ленте нет. Реальный рендер
// редактора поверх фейк-DOM (паттерн WorkflowTemplatesEditor.list.test).
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
vi.mock('../../lib/models', async orig => ({ ...(await orig<object>()), useModels: () => [] }));
vi.mock('../../lib/presets', async orig => ({ ...(await orig<object>()), usePresets: () => [] }));

const { installFakeDom, mount, textNodes, findNodeByAttr, reactProps, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom');
const { WorkflowTemplatesEditor } = await import('./WorkflowTemplatesEditor');
const { workflowTemplatesApi } = await import('./api');

const entry: WorkflowTemplateEntry = {
  scope: 'global', projectId: null,
  template: {
    id: 'g1', name: 'Мой процесс', version: 2,
    stages: [{ role: 'cut', presence: 'on' }, { role: 'review', presence: 'on', after: ['cut'] }],
  },
  stageCount: 2,
};

type Mounted = ReturnType<typeof mount>;
type Node = NonNullable<ReturnType<typeof findNodeByAttr>>;
let mounted: Mounted | null = null;
const flush = async () => { await act(async () => { await new Promise(r => setTimeout(r, 0)); }); };
const text = (m: Mounted) => textNodes(m.container).join('');
const click = (n: Node) => act(() => { (reactProps(n)?.onClick as (e: unknown) => void)({}); });
const buttonByText = (root: Node, t: string): Node | null => {
  let hit: Node | null = null;
  const walk = (x: Node) => {
    if (hit) return;
    if (x.nodeName === 'BUTTON' && textNodes(x).join('').includes(t)) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(root);
  return hit;
};

async function openTemplate(): Promise<Mounted> {
  vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([entry]);
  installFakeDom();
  const m = mount(createElement(WorkflowTemplatesEditor, { canWrite: true, isAdmin: true, mcpChoices: [] }));
  mounted = m;
  await flush();
  click(buttonByText(m.container, 'Мой процесс')!);
  return m;
}

afterEach(() => {
  if (mounted) act(() => mounted!.root.unmount());
  mounted = null;
});
afterAll(() => { uninstallFakeDom(); });

describe('F23: настройка этапа в редакторе', () => {
  it('пока этап не выбран — панели нет, строки не раскрыты', async () => {
    const m = await openTemplate();
    expect(findNodeByAttr(m.container, 'data-stage-settings', '')).toBeNull();
    expect(text(m)).not.toContain('Присутствие');
  });

  it('клик по строке «Ревью» открывает панель «Этап «Ревью» review»; «✕» закрывает', async () => {
    const m = await openTemplate();
    click(findNodeByAttr(m.container, 'aria-label', 'Этап «Ревью»')!);
    const panel = findNodeByAttr(m.container, 'data-stage-settings', '');
    expect(panel).not.toBeNull();
    const t = text(m);
    expect(t).toContain('Этап «Ревью»');
    expect(t).toContain('review');
    expect(t).toContain('Присутствие');
    click(findNodeByAttr(m.container, 'aria-label', 'Закрыть настройку этапа')!);
    expect(findNodeByAttr(m.container, 'data-stage-settings', '')).toBeNull();
  });

  it('правка имени в панели меняет черновик: «Сохранить» из серой становится активной', async () => {
    const m = await openTemplate();
    const save = () => buttonByText(m.container, 'Сохранить')!;
    expect(reactProps(save())?.disabled).toBe(true);
    click(findNodeByAttr(m.container, 'aria-label', 'Этап «Ревью»')!);
    const input = findNodeByAttr(m.container, 'placeholder', 'Ревью')!;
    act(() => { (reactProps(input)?.onChange as (e: unknown) => void)({ target: { value: 'Проверка кода' } }); });
    expect(text(m)).toContain('Этап «Проверка кода»');
    expect(reactProps(save())?.disabled).toBe(false);
  });
});
