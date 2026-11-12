// Степпер «Добавить привязку» (фича persona-bindings): пикер «Плагин» (ADR-031).
//  • выбор плагина + навыка формирует POST bindings с type 'skill' и целью plugin:{id}/{skill}
//    (бэку PersonaBindingType типа plugin не знает — иначе 400 «Неизвестный тип привязки»);
//  • сбой сохранения (400/сеть) — текст ошибки в степпере, панель не закрывается.
//
// Гейт по роли в проекте (W3-F1f, разрез docs/research/shared-projects-personas-cut-2026-11.md
// §2/§3.4, И-4): читатель — список только для чтения; редактор — без типа «Плагин» и mcp:-целей,
// без правки/удаления привязок к целям вне этого проекта, без «Найти навык» и без mcp:-целей
// в пресете «Минимум»; owner/без роли — как раньше.
//
// Окружение node: минимальный фейк-DOM (createRoot + act, паттерн fakeDom).

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Persona, Project, ProjectRoleView } from '../../types';

// onMessage — заглушка (realtime-перезагрузку в этом тесте не сдвигаем); onReconnected — оригинал
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {} };
});

// bumpPersonas — заглушка (realtime-бамп персоны); остальные экспорты (usePersonasVersion) — оригинал
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return { ...orig, bumpPersonas: vi.fn() };
});

// showToast — заглушка (в степпере ошибка теперь инлайн, не тостом)
vi.mock('../../lib/toast', () => ({
  showToast: vi.fn(),
}));

// api.* — фиксированные ответы; значения подменяются в тесте
vi.mock('../../lib/api', () => ({
  api: {
    personas: {
      list: vi.fn(),
      bindings: vi.fn(),
      addBinding: vi.fn(),
      updateBinding: vi.fn(),
      removeBinding: vi.fn(),
      setBindings: vi.fn(),
      bindingTargets: vi.fn(),
      aiBindingCondition: vi.fn(),
      suggestBindings: vi.fn(),
      generateBindings: vi.fn(),
      applyDefaultBindings: vi.fn(),
      update: vi.fn(),
    },
    plugins: {
      list: vi.fn(),
    },
    skills: {
      suggest: vi.fn(),
      installForPersona: vi.fn(),
    },
  },
}));

const { api } = await import('../../lib/api');
const { installFakeDom, mount, textNodes, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom'
);
const { PersonaBindingsPanel } = await import('./PersonaBindingsPanel');
const { invalidateBindingTargets } = await import('./bindingMeta');

// === Фиксированные данные ===
const PERSONA: Persona = {
  id: 'persona-1',
  name: 'Тест',
  role: 'test',
  description: '',
  scope: 'global',
  systemPrompt: '',
  contract: null,
  bindings: [],
  allProjectsAccess: false,
  specialty: 'none',
} as Persona;

const PLUGIN = {
  id: 'local-1/my-plugin',
  version: '1.0.0',
  sha256: 'abc123',
  requested: [],
  granted: [],
  enabled: true,
  source: 'upload:my-plugin',
  secrets: [],
};

// === Фикстуры гейта по роли (W3-F1f) ===
// Роль читается только через personaRole(persona, projects) — по myRole проекта персоны.
const PROJECT_ID = 'p-shared';
const sharedProject = (myRole: ProjectRoleView): Project =>
  ({ id: PROJECT_ID, name: 'Общий проект', myRole }) as Project;
// Проектная персона этого проекта (свой id на каждый случай: кэш каталогов целей
// в bindingMeta ключён id'ом персоны/типом — разные кейсы не должны его делить)
const projectPersona = (id: string): Persona =>
  ({ ...PERSONA, id, scope: 'project', projectId: PROJECT_ID } as Persona);

// Привязки: к личным заметкам владельца и к заметкам этого проекта (цель = id проекта,
// NotesService.cs:254). Порядок фикстуры — порядок списка: гейт его не меняет.
const BINDINGS_NOTES = [
  { id: 'b-note-personal', type: 'notes', target: 'personal', path: null, condition: 'личные заметки владельца', mode: 'auto', createdAt: '', updatedAt: '' },
  { id: 'b-note-project', type: 'notes', target: PROJECT_ID, path: null, condition: 'заметки этого проекта', mode: 'auto', createdAt: '', updatedAt: '' },
];

// Каталог инструментов: обычный ключ + сервер личного MCP-реестра ('mcp:' — личное владельца)
const TOOL_TARGETS = [
  { id: 'tasks', label: 'Задачи', hint: null, meta: null },
  { id: 'mcp:foo', label: 'Foo MCP сервер', hint: null, meta: null },
];

// === Хелперы (типы нод fakeDom не экспортируются — через сигнатуру textNodes) ===
type FNode = Parameters<typeof textNodes>[0];

// Весь текст поддерева
function textOf(n: FNode): string {
  let out = '';
  const walk = (x: FNode) => {
    if (x.nodeType === 3) out += x.nodeValue;
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return out;
}

// Кнопка/элемент с onClick, чей ПОЛНЫЙ текст равен label (React держит props под __reactProps$)
function findButton(container: FNode, label: string): { onClick: () => void } | null {
  let hit: { onClick: () => void } | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1) {
      const p = Object.keys(x).find(k => k.startsWith('__reactProps$'));
      if (p) {
        const props = (x as unknown as Record<string, unknown>)[p] as Record<string, unknown> | undefined;
        if (props?.onClick && textOf(x) === label) hit = { onClick: props.onClick as () => void };
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// Элемент с onClick, чей текст СОДЕРЖИТ label (для кнопок с иконкой+названием+подсказкой)
function findButtonByIncludes(container: FNode, label: string): { onClick: () => void } | null {
  let hit: { onClick: () => void } | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1) {
      const p = Object.keys(x).find(k => k.startsWith('__reactProps$'));
      if (p) {
        const props = (x as unknown as Record<string, unknown>)[p] as Record<string, unknown> | undefined;
        if (props?.onClick && textOf(x).includes(label)) hit = { onClick: props.onClick as () => void };
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// Поле ввода (input) по placeholder/aria-label: ставит значение и шлёт onChange
function typeInto(container: FNode, match: (n: FNode) => boolean, value: string) {
  let done = false;
  const walk = (x: FNode) => {
    if (done) return;
    if (x.nodeType === 1 && match(x)) {
      const p = Object.keys(x).find(k => k.startsWith('__reactProps$'));
      if (p) {
        const props = (x as unknown as Record<string, unknown>)[p] as Record<string, unknown> | undefined;
        if (props?.onChange) {
          props.onChange({ target: { value } });
          done = true;
          return;
        }
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
}

// Кнопки «⋯» (меню действий карточки) — по aria-label: текст «⋯» носит и обёртка с
// stopPropagation, считать по тексту — дважды на карточку
function countActionMenus(container: FNode): number {
  let n = 0;
  const walk = (x: FNode) => {
    if (x.nodeType === 1 && x.getAttribute('aria-label') === 'Действия') n++;
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return n;
}

// Монтирует панель и даёт первичной загрузке (bindings) дойти.
// Без opts — своя глобальная персона (роли нет → «как сейчас», старые кейсы не меняются);
// с opts — персона и проекты для кейсов гейта (W3-F1f).
async function renderPanel(opts?: { persona?: Persona; projects?: Project[] }) {
  installFakeDom();
  // getNav (nav.ts) читает window.history.state; useContextPersona подписывается на window;
  // joinUserGroup (personas.ts) — localStorage
  const g = globalThis as { window?: Record<string, unknown>; localStorage?: unknown; sessionStorage?: unknown };
  const w = g.window;
  if (w) {
    w.history = { state: null, pushState: () => {}, replaceState: () => {} };
    if (!w.addEventListener) w.addEventListener = () => {};
    if (!w.removeEventListener) w.removeEventListener = () => {};
  }
  if (!g.localStorage) g.localStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
  // joinUserGroup (personas.ts:25) читает cc_user_id из sessionStorage, если его нет в localStorage
  if (!g.sessionStorage) g.sessionStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
  const { container } = mount(createElement(PersonaBindingsPanel, {
    // eslint-disable-next-line design/no-raw-color -- фикстура: accent-цвет передаётся как есть, не стиль
    persona: opts?.persona ?? PERSONA, projects: opts?.projects ?? [], accent: '#D97757', isMobile: false,
  }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return { container, text: () => textNodes(container).join(' ') };
}

beforeEach(() => {
  vi.mocked(api.personas.list).mockReset().mockResolvedValue([PERSONA]);
  vi.mocked(api.personas.bindings).mockReset().mockResolvedValue([]);
  vi.mocked(api.personas.addBinding).mockReset();
  vi.mocked(api.personas.bindingTargets).mockReset().mockResolvedValue([]);
  vi.mocked(api.plugins.list).mockReset().mockResolvedValue([PLUGIN]);
  // Кэш каталогов целей — модульный (bindingMeta): кейсы гейта подменяют bindingTargets
  // разными каталогами, их промисы не должны переживать границу теста
  invalidateBindingTargets('tool');
  invalidateBindingTargets('knowledge');
});

afterAll(uninstallFakeDom);

// === Гейт по роли (W3-F1f, разрез §2/§3.4, И-4) ===
describe('Гейт по роли в PersonaBindingsPanel', () => {
  it('reader — список только для чтения: нет «Добавить», правки и удаления', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue(BINDINGS_NOTES as never);
    const { container, text } = await renderPanel({
      persona: projectPersona('persona-reader-1'), projects: [sharedProject('reader')],
    });

    // сам список виден (право Read) и порядок фикстуры не тронут
    expect(text()).toContain('Заметки · personal');
    expect(text()).toContain('Заметки · p-shared');
    // ни одной кнопки действий (меню «⋯») — ни на своих, ни на чужих карточках
    expect(countActionMenus(container), 'читателю открыто меню карточки').toBe(0);
    // нет ни одного текста добавления/удаления/массовых записей
    expect(text()).not.toContain('Добавить');
    expect(text()).not.toContain('Удалить');
    expect(text()).not.toContain('Минимум');
    expect(text()).not.toContain('✨');
    expect(text()).not.toContain('Найти навык');
    // карточка не раскрывается: строка без onClick → нет и тела правки с «Удалить привязку»
    expect(findButtonByIncludes(container, 'Заметки · personal'), 'карточка читателю доступна').toBeNull();
    expect(text()).not.toContain('Когда пользоваться');
  });

  it('reader — пустое состояние: констатация без приглашения добавить', async () => {
    const { text } = await renderPanel({
      persona: projectPersona('persona-reader-2'), projects: [sharedProject('reader')],
    });
    expect(text()).toContain('Привязок пока нет');
    expect(text()).toContain('назначает владелец проекта');
    expect(text()).not.toContain('Добавить');
    expect(text()).not.toContain('Подобрать');
    expect(text()).not.toContain('Создать привязку');
    expect(text()).not.toContain('Найти навык');
  });

  it('editor — «Добавить» есть; в выборе типов нет «Плагина»; в целях инструментов нет MCP', async () => {
    vi.mocked(api.personas.bindingTargets).mockImplementation(async (type) =>
      type === 'tool' ? TOOL_TARGETS : []);
    const { container, text } = await renderPanel({
      persona: projectPersona('persona-editor-1'), projects: [sharedProject('editor')],
    });

    // «Добавить привязку» — право bindingWrite открыто редактору
    const add = findButton(container, 'Добавить привязку');
    expect(add, 'кнопка «Добавить привязку» не найдена').not.toBeNull();
    await act(async () => { add!.onClick(); });

    // сетка типов: тип «Плагин» (цель всегда plugin:…) закрыт (bindingMcpPlugin);
    // остальные типы — на месте
    expect(findButtonByIncludes(container, 'Плагин'), 'тип «Плагин» открыт редактору').toBeNull();
    const toolType = findButtonByIncludes(container, 'Инструмент');
    expect(toolType, 'тип «Инструмент» закрыт редактору').not.toBeNull();
    await act(async () => { toolType!.onClick(); });

    // пикер инструментов: серверы личного реестра (mcp:) выпадены, обычные ключи доступны
    expect(text()).not.toContain('Foo MCP');
    expect(text()).not.toContain('Свои MCP-серверы');
    expect(text()).toContain('Задачи');
  });

  it('editor — цели вне этого проекта без правки/удаления; цель этого проекта — с ней', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue(BINDINGS_NOTES as never);
    const { container, text } = await renderPanel({
      persona: projectPersona('persona-editor-2'), projects: [sharedProject('editor')],
    });

    // порядок привязок прежний (гейт прячет органы, список не переставляет)
    const t0 = text();
    expect(t0.indexOf('Заметки · personal')).toBeGreaterThanOrEqual(0);
    expect(t0.indexOf('Заметки · personal')).toBeLessThan(t0.indexOf('Заметки · p-shared'));

    // меню «⋯» ровно одно — на привязке к заметкам этого проекта (цель = id проекта)
    expect(countActionMenus(container), 'лишние меню действий у чужих целей').toBe(1);
    // личная цель владельца: строка без onClick — ни тела правки, ни удаления
    expect(findButtonByIncludes(container, 'Заметки · personal'), 'чужая цель редактируется').toBeNull();
    // цель этого проекта: открывается, есть «Удалить привязку»
    const row = findButtonByIncludes(container, 'Заметки · p-shared');
    expect(row, 'своя цель не открывается').not.toBeNull();
    await act(async () => { row!.onClick(); });
    expect(text()).toContain('Удалить привязку');
  });

  it('editor — знания: датасет этого проекта правится (по meta цели), личный датасет владельца — нет', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue([
      { id: 'b-kn-project', type: 'knowledge', target: 'ds-project', path: null, condition: 'знания проекта', mode: 'auto', createdAt: '', updatedAt: '' },
      { id: 'b-kn-personal', type: 'knowledge', target: 'ds-personal', path: null, condition: 'личный датасет', mode: 'auto', createdAt: '', updatedAt: '' },
    ] as never);
    vi.mocked(api.personas.bindingTargets).mockImplementation(async (type) =>
      type === 'knowledge'
        ? [
          { id: 'ds-project', label: 'База этого проекта', hint: 'База знаний проекта', meta: PROJECT_ID },
          { id: 'ds-personal', label: 'Личная база владельца', hint: 'База знаний', meta: null },
        ]
        : []);
    const { container, text } = await renderPanel({
      persona: projectPersona('persona-editor-3'), projects: [sharedProject('editor')],
    });

    // «цель этого проекта» — по meta (id проекта), не по тексту подписи
    expect(findButtonByIncludes(container, 'Знания «Личная база владельца»'), 'чужой датасет редактируется').toBeNull();
    const row = findButtonByIncludes(container, 'Знания проекта «База этого проекта»');
    expect(row, 'свой датасет не открывается').not.toBeNull();
    await act(async () => { row!.onClick(); });
    expect(text()).toContain('Удалить привязку');
  });

  it('editor — «Минимум» не трогает mcp:-цели: существующую оставляет, новой не заводит', async () => {
    // Привязки: обычный ключ (auto) и сервер личного MCP-реестра (auto) — mcp: для
    // редактора закрыт (bindingMcpPlugin), пресет обязан оставить его как есть.
    vi.mocked(api.personas.bindings).mockResolvedValue([
      { id: 'b-tool-tasks', type: 'tool', target: 'tasks', path: null, condition: 'задачи', mode: 'auto', createdAt: '', updatedAt: '' },
      { id: 'b-tool-mcp', type: 'tool', target: 'mcp:foo', path: null, condition: 'свой сервер', mode: 'auto', createdAt: '', updatedAt: '' },
    ] as never);
    // Каталог шире привязок: mcp:bar без привязки — новый off на нём заводить нельзя
    vi.mocked(api.personas.bindingTargets).mockImplementation(async (type) =>
      type === 'tool' ? [...TOOL_TARGETS, { id: 'mcp:bar', label: 'Bar MCP сервер', hint: null, meta: null }] : []);
    // PUT-замена как на бэке: возвращает тот же набор с ids
    vi.mocked(api.personas.setBindings).mockReset().mockImplementation(async (_id, dtos) =>
      dtos.map((d, i) => ({ ...d, id: `b-min-${i}`, createdAt: '', updatedAt: '' })) as never);
    const { container } = await renderPanel({
      persona: projectPersona('persona-editor-4'), projects: [sharedProject('editor')],
    });

    const preset = findButton(container, 'Пресет «Минимум»');
    expect(preset, 'кнопка пресета «Минимум» не найдена').not.toBeNull();
    await act(async () => { preset!.onClick(); await new Promise(r => setTimeout(r, 0)); });
    // подтверждение — ConfirmDialog (портал на document.body)
    const body = (globalThis as { document: { body: unknown } }).document.body as FNode;
    const confirm = findButton(body, 'Выключить');
    expect(confirm, 'кнопка подтверждения пресета не найдена').not.toBeNull();
    await act(async () => { confirm!.onClick(); await new Promise(r => setTimeout(r, 0)); });

    expect(api.personas.setBindings).toHaveBeenCalledTimes(1);
    const payload = vi.mocked(api.personas.setBindings).mock.calls[0][1];
    const mcp = payload.filter(p => p.type === 'tool' && p.target.toLowerCase().startsWith('mcp:'));
    expect(mcp.length, 'mcp:-привязок в наборе больше, чем было').toBe(1);
    expect(mcp[0].mode, 'пресет изменил режим mcp:-привязки').toBe('auto');
    // свои ключи пресет выключает по-прежнему
    expect(payload.find(p => p.type === 'tool' && p.target === 'tasks')?.mode, 'обычный ключ не выключен').toBe('off');
  });

  it('editor — «Добавить» есть, «Найти навык» нет (skills — отдельное право)', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue(BINDINGS_NOTES as never);
    const { container, text } = await renderPanel({
      persona: projectPersona('persona-editor-5'), projects: [sharedProject('editor')],
    });
    // «Добавить привязку» — bindingWrite открыт (строка кнопок под списком)
    expect(findButton(container, 'Добавить привязку'), 'кнопка «Добавить привязку» не найдена').not.toBeNull();
    // «Найти навык» — закрыта: maySkills ≠ mayBind (мутация maySkills = mayBind краснеет)
    expect(text(), '«Найти навык» открыта редактору').not.toContain('Найти навык');
  });

  it('owner/без роли — как раньше: «Плагин» и mcp:-цели доступны (граница гейта)', async () => {
    vi.mocked(api.personas.bindingTargets).mockImplementation(async (type) =>
      type === 'tool' ? TOOL_TARGETS : []);
    const { container, text } = await renderPanel(); // своя глобальная персона, projects=[] → роли нет

    const add = findButton(container, 'Добавить привязку');
    expect(add, 'кнопка «Добавить привязку» не найдена').not.toBeNull();
    await act(async () => { add!.onClick(); });
    const pluginType = findButtonByIncludes(container, 'Плагин');
    expect(pluginType, 'тип «Плагин» закрыт владельцу').not.toBeNull();
    const toolType = findButtonByIncludes(container, 'Инструмент');
    expect(toolType, 'тип «Инструмент» закрыт владельцу').not.toBeNull();
    await act(async () => { toolType!.onClick(); });
    expect(text()).toContain('Foo MCP');
    expect(text()).toContain('Свои MCP-серверы');
  });
});

// === Тип «Плагин» → POST bindings с type 'skill' + цель plugin:{id}/{skill} ===
describe('PluginTargetPicker → commit', () => {
  it('выбор плагина + навыка → addBinding с type "skill" и целью plugin:{id}/{skill}', async () => {
    vi.mocked(api.personas.addBinding).mockResolvedValue({
      id: 'b1', type: 'skill', target: 'plugin:local-1/my-plugin/qapkg',
      path: null, condition: '', mode: 'auto', createdAt: '', updatedAt: '',
    } as never);

    const { container } = await renderPanel();

    // ① «Добавить привязку» → сетка типов
    const add = findButton(container, 'Добавить привязку');
    expect(add, 'кнопка «Добавить привязку» не найдена').not.toBeNull();
    await act(async () => { add!.onClick(); });

    // ① → тип «Плагин» (кнопка с иконкой+названием+подсказкой — по включению)
    const pluginType = findButtonByIncludes(container, 'Плагин');
    expect(pluginType, 'кнопка типа «Плагин» не найдена').not.toBeNull();
    await act(async () => { pluginType!.onClick(); });

    // ② пикер: выбрать плагин (строка-кнопка с id)
    const pluginRow = findButtonByIncludes(container, 'local-1/my-plugin');
    expect(pluginRow, 'строка плагина не найдена').not.toBeNull();
    await act(async () => { pluginRow!.onClick(); });

    // ② → ввести имя навыка (IconField, placeholder "weekly-report")
    await act(async () => {
      typeInto(container, n => n.getAttribute('placeholder') === 'weekly-report', 'qapkg');
    });
    // «Далее» (кнопка активна, когда навык введён)
    const next = findButton(container, 'Далее');
    expect(next, 'кнопка «Далее» не найдена').not.toBeNull();
    await act(async () => { next!.onClick(); });

    // ③ «Добавить привязку» → POST
    const commit = findButton(container, 'Добавить привязку');
    expect(commit, 'кнопка «Добавить привязку» (шаг 3) не найдена').not.toBeNull();
    await act(async () => { commit!.onClick(); await new Promise(r => setTimeout(r, 0)); });

    expect(api.personas.addBinding).toHaveBeenCalledTimes(1);
    const dto = vi.mocked(api.personas.addBinding).mock.calls[0][1] as { type: string; target: string };
    expect(dto.type).toBe('skill');
    expect(dto.target).toBe('plugin:local-1/my-plugin/qapkg');
  });

  it('ответ 400 → текст ошибки в степпере, панель не закрыта (мутация: вернуть type "plugin")', async () => {
    vi.mocked(api.personas.addBinding).mockRejectedValue(new Error('Неизвестный тип привязки: plugin'));

    const { container, text } = await renderPanel();

    const add = findButton(container, 'Добавить привязку');
    await act(async () => { add!.onClick(); });
    const pluginType = findButtonByIncludes(container, 'Плагин');
    await act(async () => { pluginType!.onClick(); });
    const pluginRow = findButtonByIncludes(container, 'local-1/my-plugin');
    await act(async () => { pluginRow!.onClick(); });
    await act(async () => {
      typeInto(container, n => n.getAttribute('placeholder') === 'weekly-report', 'qapkg');
    });
    const next = findButton(container, 'Далее');
    await act(async () => { next!.onClick(); });

    const commit = findButton(container, 'Добавить привязку');
    await act(async () => { commit!.onClick(); await new Promise(r => setTimeout(r, 0)); });

    // текст ошибки сервера виден в степпере
    expect(text()).toContain('Неизвестный тип привязки: plugin');
    // панель не закрыта: сетка/крошка и кнопка «Добавить привязку» ещё на месте
    expect(findButton(container, 'Добавить привязку'), 'панель закрылась после ошибки').not.toBeNull();
  });
});
