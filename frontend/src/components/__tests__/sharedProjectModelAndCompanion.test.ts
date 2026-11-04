// S2a-F3a: модель и собеседник в общем проекте (п.16–17 карты v2). Композер целиком в
// fake-DOM не поднять (сотни хуков и рефов), поэтому проверяются сами два компонента:
// они монтируются в fake-DOM (механизм как в SessionList.test.ts — React 19 вешает
// делегированный клик на корневой контейнер, «клик» = fireClick по узлу), каталог моделей
// — мок стора (два провайдера, чтобы заголовки групп были как в жизни), аватар персон —
// заглушка. Меню и список раскрываются настоящим кликом по триггеру: проверяется то, что
// человек видит в раскрытом меню, а не аргументы, переданные ребёнку.
import { createElement } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { describe, expect, it, vi } from 'vitest';
import type { AgentInfo, Persona } from '../../types';
import type { ModelOption } from '../../lib/models';

const { FakeNode } = vi.hoisted(() => {
  class _FakeNode {
    nodeType = 1;
    nodeName = 'div';
    childNodes: _FakeNode[] = [];
    parentElement: _FakeNode | null = null;
    _attrs: Record<string, string> = {};
    style: any = { setProperty: () => {} };
    textContent = '';
    _listeners: Record<string, ((e: any) => void)[]> = {};
    setAttribute(k: string, v: string) { this._attrs[k] = String(v); }
    getAttribute(k: string) { return this._attrs[k] ?? null; }
    removeAttribute(k: string) { delete this._attrs[k]; }
    get firstChild() { return this.childNodes[0] ?? null; }
    get parentNode() { return this.parentElement; }
    appendChild(c: _FakeNode) { c.parentElement = this; this.childNodes.push(c); return c; }
    insertBefore(c: _FakeNode, ref: _FakeNode | null) {
      c.parentElement = this;
      if (ref == null) this.childNodes.push(c);
      else { const i = this.childNodes.indexOf(ref); this.childNodes.splice(i < 0 ? this.childNodes.length : i, 0, c); }
      return c;
    }
    removeChild(c: _FakeNode) { const i = this.childNodes.indexOf(c); if (i >= 0) this.childNodes.splice(i, 1); c.parentElement = null; return c; }
    contains(c: _FakeNode): boolean { return c === this || this.childNodes.some(n => n.contains(c)); }
    get ownerDocument() { return _doc; }
    get tagName() { return this.nodeName.toUpperCase(); }
    addEventListener = (type: string, h: (e: any) => void) => { (this._listeners[type] ??= []).push(h); };
    removeEventListener = (type: string, h: (e: any) => void) => { const a = this._listeners[type]; if (a) { const i = a.indexOf(h); if (i >= 0) a.splice(i, 1); } };
    // Позиция триггера: нужна меню модели, когда оно открыто (потолок высоты)
    getBoundingClientRect = () => ({ top: 100, bottom: 132, left: 0, right: 220, width: 220, height: 32, x: 0, y: 100 });
    // React 19 вешает слушатели на корневой контейнер: «клик» = вызов их с событием,
    // чей target — кликнутый узел (синтетическая диспетчеризация идёт по target)
    fireClick(target: _FakeNode) {
      const ev: any = { type: 'click', target, currentTarget: this, bubbles: true, cancelable: true,
        preventDefault() {}, stopPropagation() {}, stopImmediatePropagation() {}, nativeEvent: null };
      ev.nativeEvent = ev;
      for (const h of this._listeners['click'] ?? []) h(ev);
    }
  }
  const _doc: any = {
    createElement: (t: string) => { const n = new _FakeNode(); n.nodeName = t; return n; },
    createElementNS: (_ns: string, t: string) => { const n = new _FakeNode(); n.nodeName = t; return n; },
    createTextNode: (t: string) => { const n = new _FakeNode(); n.nodeType = 3; n.textContent = t; return n; },
    createComment: (t: string) => { const n = new _FakeNode(); n.nodeType = 8; n.textContent = t; return n; },
    body: new _FakeNode(),
    documentElement: new _FakeNode(),
    getElementById: () => null,
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  class _FakeIframe {}
  const _win: any = {
    document: _doc, defaultView: null,
    // React в getActiveElementDeep меряет элемент через defaultView.HTMLIFrameElement
    HTMLIFrameElement: _FakeIframe, HTMLInputElement: class {}, HTMLSelectElement: class {}, HTMLTextAreaElement: class {},
    addEventListener: () => {}, removeEventListener: () => {},
    location: { href: '' }, navigator: { userAgent: 'test' },
    innerHeight: 900, innerWidth: 1280,
    setTimeout: () => 0, clearTimeout: () => {},
    matchMedia: (q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {}, onchange: null, dispatchEvent: () => true }),
  };
  (globalThis as any).document = _doc;
  (globalThis as any).window = _win;
  (globalThis as any).IS_REACT_ACT_ENVIRONMENT = true;
  _doc.defaultView = _win;
  return { FakeNode: _FakeNode as typeof _FakeNode };
});

// Два провайдера — чтобы заголовки групп и порядок списков были такие же, как в жизни
const MODELS = vi.hoisted((): ModelOption[] => ([
  { value: '', label: 'По умолчанию' },
  { value: 'opus', label: 'Opus', provider: 'claude', contextWindow: 200000 },
  { value: 'deepseek-v3', label: 'DeepSeek V3', provider: 'deepseek', contextWindow: 128000 },
]));
vi.mock('../../lib/models', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/models')>();
  return { ...orig, useModels: () => MODELS, getModels: () => MODELS };
});
vi.mock('../../lib/api', () => ({ api: {} }));
vi.mock('../../features/personas/PersonaAvatar', () => ({ PersonaAvatar: () => createElement('span') }));

import { CompanionSelector } from '../CompanionSelector';
import { ComposerModelPicker } from '../ComposerModelPicker';

type FN = InstanceType<typeof FakeNode>;

const OWNER_NOTE = 'Модель выбирает владелец проекта';
const GLOBALS_NOTE = 'Глобальные персоны в общем проекте недоступны';

const polina: Persona = { id: 'pp', handle: 'polina', name: 'Полина', role: 'Frontend', scope: 'project', projectId: 'p1' } as Persona;
const alisa: Persona = { id: 'pg', handle: 'alisa', name: 'Алиса', role: 'Помощник', scope: 'global' } as Persona;
const scout: AgentInfo = { name: 'Разведчик', description: 'ищет по коду', fileName: 'scout', tools: [] };

// === helpers ===

const textOf = (n: FN): string => (n.textContent ?? '') + n.childNodes.map(textOf).join('');

// Отдельные текстовые куски дерева: заголовок группы «Глобальные» и пояснение
// «Глобальные персоны…» — разные строки, их нельзя путать подстрокой
const textsOf = (root: FN): string[] => {
  const out: string[] = [];
  const walk = (n: FN) => {
    if (n.textContent) out.push(n.textContent);
    for (const c of n.childNodes) walk(c);
  };
  walk(root);
  return out;
};

function findByAttr(n: FN, name: string, match: (v: string) => boolean): FN | null {
  const v = n._attrs[name];
  if (v !== undefined && match(v)) return n;
  for (const c of n.childNodes) { const r = findByAttr(c, name, match); if (r) return r; }
  return null;
}

function findButtonByText(root: FN, text: string): FN | null {
  const walk = (n: FN): FN | null => {
    if (n.nodeName === 'button' && textOf(n).includes(text)) return n;
    for (const c of n.childNodes) { const r = walk(c); if (r) return r; }
    return null;
  }
  return walk(root);
}

// Выключенность строки: React ставит disabled то атрибутом, то свойством узла
const isDisabled = (n: FN): boolean => n.disabled === true || n._attrs['disabled'] !== undefined;

async function mount(component: any, props: Record<string, unknown>) {
  const container = new FakeNode();
  const root = createRoot(container);
  await act(async () => { root.render(createElement(component, props)); });
  return { container, root };
}

// Открыть меню/список настоящим кликом по его триггеру
async function openMenu(container: FN, trigger: FN | null) {
  expect(trigger, 'триггер не найден').not.toBeNull();
  await act(async () => { container.fireClick(trigger!); });
}

const pickerTrigger = (container: FN) => findByAttr(container, 'aria-label', v => v.startsWith('Модель:'));
const companionTrigger = (container: FN) => findButtonByText(container, 'Собеседник');

// === пикер модели ===

describe('Пикер модели в общем проекте (п.16)', () => {
  const rows = ['Opus', 'DeepSeek V3', 'По умолчанию'];

  it('участнику: все строки моделей выключены, вверху пояснение про владельца', async () => {
    const { container } = await mount(ComposerModelPicker, { onChange: () => {}, participant: true });
    await openMenu(container, pickerTrigger(container));

    const texts = textsOf(container);
    expect(texts, 'нет пояснения о том, чья это модель').toContain(OWNER_NOTE);
    for (const label of rows) {
      const row = findButtonByText(container, label);
      expect(row, `нет строки модели «${label}»`).not.toBeNull();
      expect(isDisabled(row!), `строка «${label}» не выключена`).toBe(true);
    }
  });

  it('участнику: выбор модели не уходит в onChange', async () => {
    const onChange = vi.fn();
    const { container } = await mount(ComposerModelPicker, { onChange, participant: true });
    await openMenu(container, pickerTrigger(container));
    const row = findButtonByText(container, 'Opus')!;
    await act(async () => { container.fireClick(row); });
    expect(onChange, 'участник сменил модель проекта').not.toHaveBeenCalled();
  });

  it('владельцу общего проекта и в личном проекте — как прежде: строки активны, пояснения нет', async () => {
    const onChange = vi.fn();
    const { container } = await mount(ComposerModelPicker, { onChange });
    await openMenu(container, pickerTrigger(container));

    expect(textsOf(container), 'пояснение появилось там, где его быть не должно').not.toContain(OWNER_NOTE);
    for (const label of rows) expect(isDisabled(findButtonByText(container, label)!), `строка «${label}» выключена`).toBe(false);
    await act(async () => { container.fireClick(findButtonByText(container, 'Opus')!); });
    expect(onChange).toHaveBeenCalledWith('opus');
  });
});

// === селектор собеседника ===

describe('Собеседник в общем проекте (п.17)', () => {
  const companionProps = (extra: Record<string, unknown> = {}) => ({
    personas: [polina, alisa], agents: [scout],
    selectedPersona: null, selectedAgentName: null, onSelect: () => {}, ...extra,
  });

  it('общий проект: группы «Глобальные» нет, её персона не выбирается, внизу пояснение', async () => {
    const { container } = await mount(CompanionSelector, companionProps({ sharedProject: true }));
    await openMenu(container, companionTrigger(container));

    const texts = textsOf(container);
    expect(texts, 'группа «Глобальные» осталась в общем проекте').not.toContain('Глобальные');
    expect(textOf(container), 'глобальная персона доступна в общем проекте').not.toContain('Алиса');
    expect(textOf(container), 'нет команды проекта').toContain('Полина');
    expect(texts, 'нет пояснения об убранной группе').toContain(GLOBALS_NOTE);
    // Пояснение — НИЖО списка: после групп персон и агентов
    const flat = textOf(container);
    expect(flat.indexOf(GLOBALS_NOTE), 'пояснение не внизу списка').toBeGreaterThan(flat.indexOf('Агенты AI'));
  });

  it('общий проект без глобальных персон: пояснения нет (нечего объяснять)', async () => {
    const { container } = await mount(CompanionSelector, companionProps({ personas: [polina], sharedProject: true }));
    await openMenu(container, companionTrigger(container));
    expect(textsOf(container)).not.toContain(GLOBALS_NOTE);
  });

  it('личный проект — снимок как на master: «Глобальные» на месте, пояснения нет', async () => {
    const { container } = await mount(CompanionSelector, companionProps());
    await openMenu(container, companionTrigger(container));

    const texts = textsOf(container);
    expect(texts, 'в личном проекте пропала группа «Глобальные»').toContain('Глобальные');
    expect(textOf(container), 'в личном проекта пропала глобальная персона').toContain('Алиса');
    expect(texts, 'пояснение общего проекта появилось в личном').not.toContain(GLOBALS_NOTE);
  });
});
