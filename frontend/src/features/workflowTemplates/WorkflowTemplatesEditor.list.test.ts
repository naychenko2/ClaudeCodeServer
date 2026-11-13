// F22: список экрана 1 — шапка «Процессы» + «+ Новый», секции «Глобальные · правит админ»
// (сверху) и «Проект «Имя» · правит владелец», мета-строки B08 у элементов, черновик нового
// с тегом «не сохранён», пустой проект — «Других своих процессов…» + «Копия глобального…».
// Окружение node: минимальный фейк-DOM (createRoot + act, паттерн fakeDom); api и хуки —
// заглушки (в списочном состоянии редакторные хуки не вызываются, но импорт их тянет).

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
const projectOwn: WorkflowTemplateEntry = {
  scope: 'project', projectId: 'p1',
  template: { id: 'p1t', name: 'Бэк без дизайна', version: 5, basedOn: { templateId: 'g1', version: 3 }, stages: stages(7) },
  stageCount: 7, basedOn: { templateId: 'g1', name: 'Полный цикл', version: 3 },
};

let mounted: { root: ReturnType<typeof mount>['root'] } | null = null;

const mountEditor = (props: Record<string, unknown>) => {
  installFakeDom();
  const m = mount(createElement(WorkflowTemplatesEditor, { canWrite: true, isAdmin: false, mcpChoices: [], ...props }));
  mounted = m;
  return m;
};
// Прогнать цепочку then/catch/finally у load() внутри act — иначе setState после теста.
const flush = async () => { await act(async () => { await new Promise(r => setTimeout(r, 0)); }); };
const allText = (container: ReturnType<typeof mount>['container']) => textNodes(container).join('');
// Кнопка по тексту внутри (у элементов списка нет data-testid — искать иначе нечем)
type Node = NonNullable<ReturnType<typeof findNodeByAttr>>;
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

describe('F22: список редактора — секции и мета-строки', () => {
  it('проектная область: «Глобальные · правит админ» выше «Проект «Имя» · правит владелец», мета-строки B08 у элементов', async () => {
    vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull, projectOwn]);
    const { container } = mountEditor({ projectId: 'p1', projectName: 'AI Home' });
    await flush();
    const text = allText(container);
    const gi = text.indexOf('Глобальные · правит админ');
    const pi = text.indexOf('Проект «AI Home» · правит владелец');
    expect(gi).toBeGreaterThanOrEqual(0);
    expect(pi).toBeGreaterThan(gi);
    expect(text).toContain('8 этапов · v4 · 14 фич');
    expect(text).toContain('7 этапов · v5 · от «Полный цикл» v3');
    // пустого состояния нет: проектный список не пуст
    expect(text).not.toContain('Копия глобального…');
  });

  it('админская область: только секция глобальных, без проектной', async () => {
    vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull]);
    const { container } = mountEditor({});
    await flush();
    const text = allText(container);
    expect(text).toContain('Глобальные · правит админ');
    expect(text).not.toContain('· правит владелец');
  });
});

describe('F22: черновик и пустой проект', () => {
  it('«+ Новый» из шапки → строка «Новый шаблон» с тегом «не сохранён»', async () => {
    vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull, projectOwn]);
    const { container } = mountEditor({ projectId: 'p1', projectName: 'AI Home' });
    await flush();
    const btn = findNodeByAttr(container, 'data-testid', 'new-template');
    expect(btn).not.toBeNull();
    act(() => { (reactProps(btn!)?.onClick as (e: unknown) => void)({}); });
    const text = allText(container);
    expect(text).toContain('Новый шаблон');
    expect(text).toContain('не сохранён');
  });

  it('мобильная раскладка: высота элемента списка ≥56 (T10)', async () => {
    installFakeDom();
    const win = (globalThis as { window: { matchMedia: unknown } }).window;
    const origMql = win.matchMedia;
    // useIsMobile читает matchMedia в начальном состоянии — намеренно «мобильная ширина»
    win.matchMedia = () => ({ matches: true, addEventListener() {}, removeEventListener() {} });
    try {
      vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull, projectOwn]);
      const { container } = mountEditor({ projectId: 'p1', projectName: 'AI Home' });
      await flush();
      const btn = buttonByText(container, 'Полный цикл');
      expect(btn).not.toBeNull();
      // React дописывает 'px' к размерным свойствам — читаем число из значения
      expect(parseFloat(btn!.style.minHeight)).toBeGreaterThanOrEqual(56);
    } finally { win.matchMedia = origMql; }
  });

  it('пустой проект: «Других своих процессов в проекте нет» + «Копия глобального…» (кнопка — только пишущему)', async () => {
    vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([globalFull]);
    const { container } = mountEditor({ projectId: 'p1', projectName: 'AI Home' });
    await flush();
    const text = allText(container);
    expect(text).toContain('Других своих процессов в проекте нет — фичи берут глобальные.');
    expect(text).toContain('Копия глобального…');

    // участник (canWrite=false): текст тот же, кнопки нет
    if (mounted) act(() => mounted!.root.unmount());
    const m2 = mountEditor({ projectId: 'p1', projectName: 'AI Home', canWrite: false });
    mounted = m2;
    await flush();
    const t2 = allText(m2.container);
    expect(t2).toContain('Других своих процессов в проекте нет — фичи берут глобальные.');
    expect(t2).not.toContain('Копия глобального…');
  });
});
