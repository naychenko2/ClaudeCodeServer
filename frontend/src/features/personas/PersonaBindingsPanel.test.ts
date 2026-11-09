// Степпер «Добавить привязку» (фича persona-bindings): пикер «Плагин» (ADR-031).
//  • выбор плагина + навыка формирует POST bindings с type 'skill' и целью plugin:{id}/{skill}
//    (бэку PersonaBindingType типа plugin не знает — иначе 400 «Неизвестный тип привязки»);
//  • сбой сохранения (400/сеть) — текст ошибки в степпере, панель не закрывается.
//
// Окружение node: минимальный фейк-DOM (createRoot + act, паттерн fakeDom).

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Persona, ServerMessage } from '../../types';

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

// Монтирует панель и даёт первичной загрузке (bindings) дойти
async function renderPanel() {
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
    persona: PERSONA, accent: '#D97757', isMobile: false,
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
});

afterAll(uninstallFakeDom);

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
