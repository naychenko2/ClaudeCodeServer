// Хвост Р-Ф-2.39 (найдено ревью Глеба f65888bb на bbe8620ff): ключ привязки ошибки к строке
// этапа в редакторе — адресная метка (Title → роль → «роль (id)» при повторе роли), тот же,
// что строит stageLabel для текста ошибки и stageLabelParts для строки ленты. Раньше ключ
// брался из первой группы регулярки (Title, если он есть) и искался по id/роли: при двух
// этапах одной роли строки показывали чужую ошибку, при заданном Title — теряли свою.
// Здесь: реальная цепочка редактора поверх фейк-DOM (паттерн WorkflowTemplatesEditor.settings.test)
// — и подсказки модели (stageErrors), и отказ сервера 400 (SaveFailure.stageErrors).
import { afterAll, afterEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { WorkflowTemplateEntry } from './types';

// Текст — дословный серверный (WorkflowRules.ValidateTitleGroups): «имя (Title) обязательно»
// фронтовый валидатор не моделирует вовсе — такой отказ до правки доезжал только сводкой.
const TITLE_REQUIRED = 'этап делит роль и линию с другим этапом — имя (Title) обязательно.';

vi.mock('./api', () => ({
  workflowTemplatesApi: {
    listTemplates: vi.fn(),
    deleteTemplate: vi.fn(),
    resetBuiltIn: vi.fn(),
    // 400 по контракту api.ts: { error, errors: string[] } с адресными метками этапов.
    saveTemplate: vi.fn(async () => {
      throw Object.assign(new Error('шаблон не прошёл проверку.'), {
        status: 400,
        body: {
          error: 'шаблон не прошёл проверку.',
          errors: [`этап «qa» (qa-2): ${TITLE_REQUIRED}`],
        },
      });
    }),
  },
}));
vi.mock('../../lib/specialties', () => ({ useSpecialtyCatalog: () => [] }));
vi.mock('../../lib/personas', () => ({ usePersonas: () => [] }));
vi.mock('../../lib/toast', () => ({ showToast: vi.fn() }));

const { installFakeDom, mount, textNodes, findNodeByAttr, reactProps, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom');
const { WorkflowTemplatesEditor } = await import('./WorkflowTemplatesEditor');
const { workflowTemplatesApi } = await import('./api');

type Mounted = ReturnType<typeof mount>;
type Node = NonNullable<ReturnType<typeof findNodeByAttr>>;
let mounted: Mounted | null = null;

const flush = async () => { await act(async () => { await new Promise(r => setTimeout(r, 0)); }); };
const click = (n: Node) => act(() => { (reactProps(n)?.onClick as (e: unknown) => void)({}); });
const buttonByText = (root: Node, t: string): Node | null => {
  let hit: Node | null = null;
  const walk = (x: Node) => {
    if (hit) return;
    if (x.nodeName === 'BUTTON' && textNodes(x).join('').includes(t)) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  }
  walk(root);
  return hit;
};
// Текст строки ленты по её номеру (data-stage-index — индекс этапа в списке, тот же, что у
// errorsFor): подсказка у поля живёт внутри строки, сводка EditorNotices — вне её.
const rowText = (root: Node, index: number): string => {
  const row = findNodeByAttr(root, 'data-stage-index', String(index));
  expect(row, `строка этапа №${index}`).not.toBeNull();
  return textNodes(row!).join('');
};
const firstInput = (root: Node): Node => {
  let hit: Node | null = null;
  const walk = (x: Node) => {
    if (hit) return;
    if (x.nodeName === 'INPUT') { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  }
  walk(root);
  expect(hit, 'поле «Название» (первый input редактора)').not.toBeNull();
  return hit!;
};

async function openTemplate(stages: WorkflowTemplateEntry['template']['stages']): Promise<Mounted> {
  vi.mocked(workflowTemplatesApi.listTemplates).mockResolvedValue([{
    scope: 'global', projectId: null,
    template: { id: 'g1', name: 'Мой процесс', version: 2, stages },
    stageCount: stages.length,
  }]);
  installFakeDom();
  const m = mount(createElement(WorkflowTemplatesEditor, { canWrite: true, isAdmin: true, mcpChoices: [] }));
  mounted = m;
  await flush();
  click(buttonByText(m.container, 'Мой процесс')!);
  await flush();
  return m;
}

afterEach(() => {
  if (mounted) act(() => mounted!.root.unmount());
  mounted = null;
});
afterAll(() => { uninstallFakeDom(); });

describe('Хвост Р-Ф-2.39: подсказка об ошибке — у своего поля', () => {
  it('два этапа одной роли без Title: ошибка каждой — у своей строки, не у чужой', async () => {
    // Роль «qa» не одиночная — повтор легален, у этапа свой id. Ошибка одна, на втором этапе.
    const m = await openTemplate([
      { id: 'qa', role: 'qa', presence: 'on' },
      { id: 'qa-2', role: 'qa', presence: 'bad', after: ['qa'] },
    ]);
    expect(rowText(m.container, 1)).toContain('неизвестное Presence');
    expect(rowText(m.container, 0)).not.toContain('неизвестное Presence');
  });

  it('один этап: поведение как раньше — ошибка у своей строки', async () => {
    const m = await openTemplate([{ id: 'qa', role: 'qa', presence: 'bad' }]);
    expect(rowText(m.container, 0)).toContain('неизвестное Presence');
  });

  it('заданный Title: подсказка привязана к своей строке, а не потерялась', async () => {
    const m = await openTemplate([
      { id: 'qa', role: 'qa', title: 'Приёмка', presence: 'bad', after: [] },
      { id: 'qa-2', role: 'qa', presence: 'on', after: ['qa'] },
    ]);
    expect(rowText(m.container, 0)).toContain('неизвестное Presence');
    expect(rowText(m.container, 1)).not.toContain('неизвестное Presence');
  });

  it('отказ сервера 400 по повторяющейся роли: его ошибка — у своей строки', async () => {
    // Фронтовый валидатор здесь чист (повтор «qa» леголен), отказ говорит только сервер.
    const m = await openTemplate([
      { id: 'qa', role: 'qa', presence: 'on', after: [] },
      { id: 'qa-2', role: 'qa', presence: 'on', after: ['qa'] },
    ]);
    act(() => {
      (reactProps(firstInput(m.container))!.onChange as (e: unknown) => void)(
        { target: { value: 'Мой процесс (моя правка)' } });
    });
    click(buttonByText(m.container, 'Сохранить')!);
    await flush();
    expect(rowText(m.container, 1)).toContain(TITLE_REQUIRED);
    expect(rowText(m.container, 0)).not.toContain(TITLE_REQUIRED);
  });
});
