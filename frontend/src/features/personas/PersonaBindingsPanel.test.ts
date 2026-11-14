// Степпер «Добавить привязку» (фича persona-bindings): пикер «Плагин» (ADR-031).
//  • выбор плагина + навыка формирует POST bindings с type 'skill' и целью plugin:{id}/{skill}
//    (бэку PersonaBindingType типа plugin не знает — иначе 400 «Неизвестный тип привязки»);
//  • сбой сохранения (400/сеть) — текст ошибки в степпере, панель не закрывается.
//
// Гейт по роли в проекте (W3-F1f, разрез docs/research/shared-projects-personas-cut-2026-11.md
// §2/§3.4, И-4): читатель и редактор — список только для чтения (редактор — решение Андрея 12.11);
// owner/без роли — как раньше.
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
    // Каталог специальностей и её типовой профиль (skillNames) — панель читает их
    // для секций вкладки «Умения» (useSpecialtyCatalog / loadPromptSectionsCatalog)
    specialties: {
      list: vi.fn(),
      promptSectionsCatalog: vi.fn(),
    },
  },
}));

const { api } = await import('../../lib/api');
const { showToast } = await import('../../lib/toast');
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

// === Фикстуры вкладки «Умения» (A3b) ===
// Каталог ролей (подпись «QA-тестер» для шапки секции) и skill-профиль специальности —
// defaultBindings каталога prompt-sections, эффективные вызывающему
// (SpecialtiesController.cs:52-54,82). По этому профилю фронт опознаёт «отключено
// вами»: бэк исключает унаследованную копию при своей off-записи с тем же именем
// (PersonaBindingsService.cs:696) — без профиля такая строка неотличима от своей.
const SPECIALTY_QA = { key: 'qa-tester', label: 'QA-тестер', executorFamily: false, template: null };
const PROMPT_CATALOG = {
  textLimit: 1024,
  sections: [],
  specialties: {
    'qa-tester': {
      sections: [],
      defaultBindings: [
        { type: 'skill', mode: 'auto', condition: 'проверка UI', skillName: 'playwright-e2e' },
        { type: 'skill', mode: 'auto', condition: 'матрица регрессий', skillName: 'regression-matrix' },
        { type: 'skill', mode: 'auto', condition: 'аудит дизайна UI', skillName: 'web-design-guidelines' },
      ],
    },
  },
};

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

// Диалог (ConfirmDialog → Modal) рендерится порталом в document.body — вне контейнера
// панели: его текст и кнопки читаются по body, а не по container
function bodyRoot(): FNode | null {
  const doc = (globalThis as { document?: { body: FNode } }).document;
  return doc?.body ?? null;
}

// Все элементы с таким aria-label («×» — «Убрать»)
function countByAriaLabel(container: FNode, label: string): number {
  let n = 0;
  const walk = (x: FNode) => {
    if (x.nodeType === 1 && x.getAttribute('aria-label') === label) n++;
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return n;
}

// ВСЕ кнопки с полным текстом == label, в порядке DOM. Для диалога искать по bodyRoot():
// Modal рендерится порталом в body, и «Вернуть» строки (в container) ему не помеха
function findAllButtons(container: FNode, label: string): { onClick: () => void }[] {
  const hits: { onClick: () => void }[] = [];
  const walk = (x: FNode) => {
    if (x.nodeType === 1) {
      const p = Object.keys(x).find(k => k.startsWith('__reactProps$'));
      if (p) {
        const props = (x as unknown as Record<string, unknown>)[p] as Record<string, unknown> | undefined;
        if (props?.onClick && textOf(x) === label) hits.push({ onClick: props.onClick as () => void });
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hits;
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
  const { root, container } = mount(createElement(PersonaBindingsPanel, {
    // eslint-disable-next-line design/no-raw-color -- фикстура: accent-цвет передаётся как есть, не стиль
    persona: opts?.persona ?? PERSONA, projects: opts?.projects ?? [], accent: '#D97757', isMobile: false,
  }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  // root — для кейсов смены персоны (re-render того же экземпляра панели)
  return { root, container, text: () => textNodes(container).join(' ') };
}

beforeEach(() => {
  vi.mocked(api.personas.list).mockReset().mockResolvedValue([PERSONA]);
  vi.mocked(api.personas.bindings).mockReset().mockResolvedValue([]);
  vi.mocked(api.personas.addBinding).mockReset();
  vi.mocked(api.personas.bindingTargets).mockReset().mockResolvedValue([]);
  vi.mocked(api.plugins.list).mockReset().mockResolvedValue([PLUGIN]);
  // Каталог ролей и профиль типовых умений — модульный кэш (lib/specialties): одна
  // фикстура на весь файл, повторных запросов после первой загрузки нет
  vi.mocked(api.specialties.list).mockReset().mockResolvedValue([SPECIALTY_QA] as never);
  vi.mocked(api.specialties.promptSectionsCatalog).mockReset().mockResolvedValue(PROMPT_CATALOG as never);
  vi.mocked(showToast).mockClear();
  // Кэш каталогов целей — модульный (bindingMeta): кейсы гейта подменяют bindingTargets
  // разными каталогами, их промисы не должны переживать границы теста
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

  it('editor — как читатель: список только для чтения, без «Добавить», правки, «Минимум» и «Найти навык»', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue(BINDINGS_NOTES as never);
    const { container, text } = await renderPanel({
      persona: projectPersona('persona-editor-1'), projects: [sharedProject('editor')],
    });

    // список виден (право Read), порядок фикстуры не тронут
    expect(text()).toContain('Заметки · personal');
    expect(text()).toContain('Заметки · p-shared');
    // правка привязок — Configure (решение Андрея 12.11): ни меню «⋯», ни входов записи
    expect(countActionMenus(container), 'редактору открыто меню карточки').toBe(0);
    expect(findButton(container, 'Добавить привязку'), 'кнопка «Добавить привязку» у редактора').toBeNull();
    expect(findButton(container, 'Пресет «Минимум»'), 'пресет «Минимум» у редактора').toBeNull();
    expect(text()).not.toContain('Добавить');
    expect(text()).not.toContain('Удалить');
    expect(text()).not.toContain('✨');
    expect(text()).not.toContain('Найти навык');
    // даже привязка к цели этого проекта не раскрывается
    expect(findButtonByIncludes(container, 'Заметки · p-shared'), 'карточка редактору доступна').toBeNull();
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

// === A3b: унаследованные умения (inherited: true из эффективного списка бэка, A2) ===
describe('Унаследованные умения', () => {
  const INHERITED = {
    id: '', type: 'skill', target: 'review-pr', path: null, condition: 'на ревью', mode: 'auto',
    createdAt: '', updatedAt: '', inherited: true,
  };

  it('пометка «от специальности»; «Отключить» создаёт Skill-привязку Mode=Off с тем же именем, а не удаляет', async () => {
    vi.mocked(api.personas.bindings).mockReset().mockResolvedValue([INHERITED] as never);
    vi.mocked(api.personas.addBinding).mockReset().mockResolvedValue({ ...INHERITED, id: 'own-1', mode: 'off', inherited: false } as never);

    const { container, text } = await renderPanel({ persona: { ...PERSONA, id: 'persona-inh' } as Persona });
    expect(text()).toContain('от специальности');
    // у унаследованного нет меню «⋯» — править/удалять нечего
    expect(countActionMenus(container)).toBe(0);

    const off = findButton(container, 'Отключить');
    expect(off, 'кнопка «Отключить» не найдена').not.toBeNull();
    await act(async () => { off!.onClick(); await new Promise(r => setTimeout(r, 0)); });

    expect(api.personas.addBinding).toHaveBeenCalledTimes(1);
    expect(api.personas.addBinding).toHaveBeenCalledWith('persona-inh',
      { type: 'skill', target: 'review-pr', condition: 'на ревью', mode: 'off' });
    expect(api.personas.removeBinding).not.toHaveBeenCalled();
  });
});

// === A3b: секции вкладки «Умения» (макет persona-inheritance.html P29–P37,
// сверка reconciliation-report.md §2.1; счётчик по факту — §2.4 п.2; кнопки по
// состоянию строки — §2.4 п.6) + конкурентность «Отключить»/«Вернуть» ===
describe('Секции вкладки «Умения»', () => {
  const QA_PERSONA = { ...PERSONA, id: 'persona-skills', specialty: 'qa-tester' } as Persona;

  // Унаследованное умение профиля (mode auto) — строка с «Отключить»
  const INHERIT_AUTO = {
    id: '', type: 'skill', target: 'playwright-e2e', path: null, condition: 'проверка UI',
    mode: 'auto', createdAt: '', updatedAt: '', inherited: true,
  };
  // Своя off-запись к умению профиля: бэк исключил унаследованную копию — это
  // «отключено вами», строка с «Вернуть»
  const OFF_YOU = {
    id: 'b-off-you', type: 'skill', target: 'web-design-guidelines', path: null,
    condition: 'аудит дизайна UI', mode: 'off', createdAt: '', updatedAt: '',
  };
  // Эффективный список как его отдаёт бэк (EffectiveBindings): два негашёных умения
  // профиля, своя off-запись, своё умение вне профиля, свой инструмент, одна
  // проектная копия — всего 6 строк трёх секций
  const SKILLS_FIXTURE = [
    INHERIT_AUTO,
    { id: '', type: 'skill', target: 'regression-matrix', path: null, condition: 'матрица регрессий', mode: 'auto', createdAt: '', updatedAt: '', inherited: true },
    OFF_YOU,
    { id: 'b-own-skill', type: 'skill', target: 'my-skill', path: null, condition: 'своё', mode: 'auto', createdAt: '', updatedAt: '' },
    { id: 'b-own-tool', type: 'tool', target: 'tasks', path: null, condition: 'задачи', mode: 'auto', createdAt: '', updatedAt: '' },
    { id: 'b-copy', type: 'project', target: 'p-ai', path: null, condition: 'файлы проекта', mode: 'auto', createdAt: '', updatedAt: '' },
  ];

  it('три секции с тегами; счётчик шапки = фактическое число строк; «×» у своих и копий', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue(SKILLS_FIXTURE as never);
    const { container, text } = await renderPanel({ persona: QA_PERSONA });
    const t = text();
    // §2.4 п.2: шесть строк → «Умения 6», не «7» из макета
    expect(t).toMatch(/Умения\s+6/);
    expect(findButton(container, '+ Умение'), 'кнопка «+ Умение» не найдена').not.toBeNull();
    // «От специальности «QA-тестер»» + тег «наследуются»
    expect(t).toContain('От специальности «QA-тестер»');
    expect(t).toContain('наследуются');
    // «Свои» и «Подобраны ИИ под проект» + тег «копия, не наследуются»
    expect(t).toContain('Свои');
    expect(t).toContain('Подобраны ИИ под проект');
    expect(t).toContain('копия, не наследуются');
    // Отключённое унаследованное: тег «отключено вами» и кнопка «Вернуть» (§2.4 п.6)
    expect(t).toContain('отключено вами');
    expect(findButton(container, 'Вернуть'), 'кнопка «Вернуть» не найдена').not.toBeNull();
    expect(findButton(container, 'Отключить'), 'кнопка «Отключить» не найдена').not.toBeNull();
    expect(findButtonByIncludes(container, 'Подобрать ещё под проект'), 'кнопка «Подобрать ещё под проект» не найдена').not.toBeNull();
    // «×» — свои (умение + инструмент) и проектная копия: 3; у унаследованных строк его нет
    expect(countByAriaLabel(container, 'Убрать')).toBe(3);
    // Подсказка о наследовании (макет P34)
    expect(t).toContain('Новое умение специальности появится у');
    expect(t).toContain('гасит');
  });

  it('без специальности — «типовых умений нет — только свои», секции «От специальности» нет', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue([
      { id: 'b-own-skill', type: 'skill', target: 'my-skill', path: null, condition: 'своё', mode: 'auto', createdAt: '', updatedAt: '' },
    ] as never);
    const { text } = await renderPanel({ persona: { ...PERSONA, id: 'persona-nospec' } as Persona });
    expect(text()).toContain('Без специальности типовых умений нет — только свои.');
    expect(text()).not.toContain('От специальности');
    expect(text()).toMatch(/Умения\s+1/);
  });

  it('быстрый двойной клик «Отключить» — ровно один запрос (guard по id умения, не по экземпляру)', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue([INHERIT_AUTO] as never);
    let resolveAdd: (v: unknown) => void = () => {};
    vi.mocked(api.personas.addBinding).mockReturnValue(new Promise(r => { resolveAdd = r; }) as never);

    const { container } = await renderPanel({ persona: QA_PERSONA });
    const off = findButton(container, 'Отключить');
    expect(off, 'кнопка «Отключить» не найдена').not.toBeNull();
    await act(async () => {
      off!.onClick();
      off!.onClick(); // второй клик — пока первый запрос не завершён
      resolveAdd(undefined);
      await new Promise(r => setTimeout(r, 0));
    });
    // Мутация «снять guard» (if (skillReq.current.has(key)) return;) — здесь 2 вызова
    expect(api.personas.addBinding).toHaveBeenCalledTimes(1);
  });

  it('«Вернуть» — подтверждение и ровно один DELETE; отказ запроса — ошибка без необработанного исключения', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue([OFF_YOU] as never);
    vi.mocked(api.personas.removeBinding).mockReset();
    let resolveDel: (v: unknown) => void = () => {};
    vi.mocked(api.personas.removeBinding).mockReturnValue(new Promise(r => { resolveDel = r; }) as never);

    const { container, text } = await renderPanel({ persona: QA_PERSONA });
    expect(text()).toContain('отключено вами');
    const row = findButton(container, 'Вернуть');
    expect(row, 'кнопка «Вернуть» не найдена').not.toBeNull();
    await act(async () => { row!.onClick(); await new Promise(r => setTimeout(r, 0)); });
    // Тот же путь подтверждения, что у пресета «Минимум»: диалог «Вернуть «…»?»
    // (Modal — портал в document.body, его текст вне контейнера панели)
    const body = bodyRoot();
    expect(body, 'диалог подтверждения не открылся').not.toBeNull();
    expect(textNodes(body!).join(' ')).toContain('Вернуть «');
    const confirms = findAllButtons(body!, 'Вернуть');
    expect(confirms.length, 'кнопка «Вернуть» в диалоге не найдена').toBe(1);
    await act(async () => {
      confirms[0].onClick();
      confirms[0].onClick(); // второй клик — пока первый DELETE не завершён
      resolveDel(undefined);
      await new Promise(r => setTimeout(r, 0));
    });
    // Мутация «снять guard» — здесь 2 вызова
    expect(api.personas.removeBinding).toHaveBeenCalledTimes(1);
    expect(api.personas.removeBinding).toHaveBeenCalledWith('persona-skills', 'b-off-you');

    // Отказ — текст сервера в тосте, без необработанного исключения (иначе act его выбросит);
    // guard снят в finally: следующая попытка проходит
    vi.mocked(api.personas.removeBinding).mockReset().mockRejectedValue(new Error('Сервер отказал'));
    const row2 = findButton(container, 'Вернуть');
    expect(row2, 'после отказа кнопка «Вернуть» пропала').not.toBeNull();
    await act(async () => { row2!.onClick(); await new Promise(r => setTimeout(r, 0)); });
    const confirms2 = findAllButtons(body!, 'Вернуть');
    expect(confirms2.length, 'диалог не открылся второй раз').toBe(1);
    await act(async () => {
      confirms2[0].onClick();
      await new Promise(r => setTimeout(r, 0));
    });
    expect(showToast).toHaveBeenCalledWith('Умения', 'Сервер отказал');
  });

  it('устаревший ответ после смены персоны игнорируется: перечитывания старой персоны нет', async () => {
    vi.mocked(api.personas.bindings).mockResolvedValue([INHERIT_AUTO] as never);
    let resolveAdd: (v: unknown) => void = () => {};
    vi.mocked(api.personas.addBinding).mockReturnValue(new Promise(r => { resolveAdd = r; }) as never);

    const { root, container } = await renderPanel({ persona: { ...QA_PERSONA, id: 'persona-A' } as Persona });
    const off = findButton(container, 'Отключить');
    expect(off, 'кнопка «Отключить» не найдена').not.toBeNull();
    await act(async () => { off!.onClick(); }); // запрос «persona-A» висит

    // Смена персоны, пока ждём ответ: панель перечитывает новую (это легитимно)
    await act(async () => {
      root.render(createElement(PersonaBindingsPanel, {
        // eslint-disable-next-line design/no-raw-color -- фикстура: accent-цвет передаётся как есть, не стиль
        persona: { ...QA_PERSONA, id: 'persona-B' } as Persona, projects: [], accent: '#D97757', isMobile: false,
      }));
      await new Promise(r => setTimeout(r, 0));
    });
    await act(async () => { resolveAdd(undefined); await new Promise(r => setTimeout(r, 0)); });

    // Мутация (убрать проверку personaIdRef) — появляется третий вызов 'persona-A'
    expect(vi.mocked(api.personas.bindings).mock.calls.map(c => c[0])).toEqual(['persona-A', 'persona-B']);
  });
});
