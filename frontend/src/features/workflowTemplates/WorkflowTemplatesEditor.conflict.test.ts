// Ф-2.19 (дефект приёмки 8be0b1a5, QA-2 п.9): параллельная внешняя правка не должна
// молча затираться сохранением редактора. Контракт (api.ts:31-41, WorkflowTemplateStore.cs:236):
// PUT несёт expectedVersion той версии, с которой редактор открыл шаблон; не совпала —
// 409 и плашка «Загрузить vN…» / «Сохранить мои правки новым шаблоном»; чужое содержимое
// остаётся на сервере.
// Здесь прогоняется реальная цепочка (редактор → api.ts) поверх фейкового сервера,
// повторяющего контракт стора: несовпадение версии → 409 с живым текстом отказа,
// совпадение (или null) → Version+1. Раньше этот случай не был защищён нигде;
// acceptance-прогон открыл редактор ПОСЛЕ внешней правки (редактор был на последней
// версии — конфликта нет, сохранение законно), и «затирание» оказалось артефактом
// порядка шагов, а не поведением кода. Порядок здесь — из карточки дефекта:
// открыть (v1) → правка в UI → внешняя правка (v2) → «Сохранить» → 409.

import { afterAll, afterEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';

// Состояние фейкового сервера и тела PUT — в vi.hoisted: фабрика vi.mock для lib/offline
// исполняется раньше тела модуля и не видит обычные let.
const h = vi.hoisted(() => ({
  server: {
    id: 'g1', name: 'Мой процесс', description: '', version: 1, builtInKey: null,
    stages: [{ role: 'cut', presence: 'on' as const }, { role: 'review', presence: 'on' as const }],
  },
  puts: [] as Record<string, unknown>[],
}));

vi.mock('../../lib/offline', () => ({
  // Копия контракта живого REST: GET отдаёт строки-обёртки (scope + template + мета),
  // PUT с expectedVersion ≠ текущей версии — тот же текст 409, что у стора
  // («шаблон изменился: версия N, ожидалась M»); expectedVersion отсутствует —
  // без проверки (правило стора «null — без проверки»: именно так и затирают чужое).
  request: async (url: string, options?: { method?: string; body?: string }) => {
    const method = (options?.method ?? 'GET').toUpperCase();
    if (method === 'GET') {
      return [{
        scope: 'global', projectId: null,
        template: { ...h.server, stages: [...h.server.stages] },
        stageCount: h.server.stages.length,
      }];
    }
    const body = JSON.parse(options!.body!) as Record<string, unknown>;
    h.puts.push(body);
    const expected = body.expectedVersion as number | null | undefined;
    if (expected !== null && expected !== undefined && expected !== h.server.version) {
      throw Object.assign(
        new Error(`шаблон изменился: версия ${h.server.version}, ожидалась ${expected}.`), { status: 409 });
    }
    h.server = { ...h.server, name: body.name as string, version: h.server.version + 1 };
    return { ...h.server, stages: [...h.server.stages] };
  },
}));
vi.mock('../../lib/specialties', () => ({ useSpecialtyCatalog: () => [] }));
vi.mock('../../lib/personas', () => ({ usePersonas: () => [] }));
vi.mock('../../lib/toast', () => ({ showToast: vi.fn() }));

const { installFakeDom, mount, textNodes, findNodeByAttr, reactProps, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom');
const { WorkflowTemplatesEditor } = await import('./WorkflowTemplatesEditor');
const { request } = await import('../../lib/offline');

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
// Поле «Название» — первый INPUT редактора: список — кнопки, «Описание» — textarea,
// лента этапов без открытой настройки — кнопок-полей не имеет (тот же приём, что в e2e-спеке).
const firstInput = (root: Node): Node => {
  let hit: Node | null = null;
  const walk = (x: Node) => {
    if (hit) return;
    if (x.nodeName === 'INPUT') { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(root);
  expect(hit, 'поле «Название» (первый input редактора)').not.toBeNull();
  return hit!;
};

afterEach(() => {
  if (mounted) act(() => mounted!.root.unmount());
  mounted = null;
  h.server = {
    id: 'g1', name: 'Мой процесс', description: '', version: 1, builtInKey: null,
    stages: [{ role: 'cut', presence: 'on' as const }, { role: 'review', presence: 'on' as const }],
  };
  h.puts = [];
});
afterAll(() => { uninstallFakeDom(); });

describe('Ф-2.19: параллельная внешняя правка — 409, а не затирание', () => {
  it('внешняя правка → «Сохранить»: PUT с версией открытия, плашка «теперь это v2», чужое содержимое цело', async () => {
    installFakeDom();
    const m = mount(createElement(WorkflowTemplatesEditor, { canWrite: true, isAdmin: true, mcpChoices: [] }));
    mounted = m;
    await flush(); // GET → v1, список показывает «Мой процесс» v1

    // 1. Открыли шаблон (v1) и правим имя — «Сохранить» активна.
    click(buttonByText(m.container, 'Мой процесс')!);
    act(() => {
      (reactProps(firstInput(m.container))!.onChange as (e: unknown) => void)(
        { target: { value: 'Мой процесс (моя правка)' } });
    });

    // 2. Внешняя правка тем же PUT (expectedVersion=1) — сервер отвечает v2, редактор про неё не знает.
    await request('/workflow-templates/g1', {
      method: 'PUT',
      body: JSON.stringify({ ...h.server, name: 'Мой процесс (внешняя правка)', expectedVersion: 1 }),
    });
    expect(h.server.version, 'внешняя правка дала v2').toBe(2);

    // 3. «Сохранить» в редакторе.
    click(buttonByText(m.container, 'Сохранить')!);
    await flush(); // PUT expectedVersion=1 → 409 → плашка + перечитывание (conflictVersion=2)

    // 4a. Редактор отправил версию, с которой открыл шаблон (не «последнюю известную»),
    // и сделал ровно одну попытку: PUT всего два — внешняя правка и она.
    expect(h.puts.length, 'два PUT: внешняя правка + одна попытка редактора').toBe(2);
    expect(h.puts[1].expectedVersion, 'expectedVersion — версия открытия (1), не v2').toBe(1);

    // 4b. Показана плашка конфликта с версией сервера и обею выходами из него.
    const t = text(m);
    expect(t).toContain('Шаблон изменён в другом окне — теперь это v2');
    expect(t).toContain('Загрузить v2 — мои правки пропадут');
    expect(t).toContain('Сохранить мои правки новым шаблоном');

    // 4c. Содержимое внешней правки не затёрто: сервер остался на v2 с чужим именем,
    // и список показывает его. Свои правки человека при этом не потеряны (черновик цел).
    expect(h.server.version).toBe(2);
    expect(h.server.name).toBe('Мой процесс (внешняя правка)');
    expect(t).toContain('Мой процесс (внешняя правка)');
    expect(t).toContain('Мой процесс (моя правка)');
  });
});
