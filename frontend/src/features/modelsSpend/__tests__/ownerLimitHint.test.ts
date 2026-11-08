// TAIL-SUBST-UI-HINT (DEC-SUBST-UI, п.1): статическая строка-подсказка под полем бюджета
// на вкладке «Цепочки» — «В общих проектах других владельцев действует лимит владельца
// проекта». Видна только участнику чужого общего проекта (myRole editor/reader).
// Признак — чистая предикатная функция по уже загруженному списку проектов (тот же
// isParticipant, новой ручки нет); показ — рендер HeaderBlock в fake-DOM (ветка
// canEdit=false: без контролов ui-кита, как в participantMode.test.ts).
import { readFileSync } from 'node:fs';
import { createElement } from 'react';
import type { ReactElement } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { describe, expect, it, vi } from 'vitest';
import type { Project } from '../../../types';

const { FakeNode } = vi.hoisted(() => {
  class _FakeNode {
    nodeType = 1;
    nodeName = 'div';
    childNodes: _FakeNode[] = [];
    parentElement: _FakeNode | null = null;
    _attrs: Record<string, string> = {};
    // dataset — для themeMode: document.documentElement.dataset.theme на уровне модуля
    dataset: Record<string, string> = {};
    style: any = { setProperty: () => {} };
    textContent = '';
    setAttribute(k: string, v: string) { this._attrs[k] = String(v); }
    getAttribute(k: string) { return this._attrs[k] ?? null; }
    removeAttribute(k: string) { delete this._attrs[k]; }
    get firstChild() { return this.childNodes[0] ?? null; }
    get parentNode() { return this.parentElement; }
    appendChild(c: _FakeNode) { c.parentElement = this; this.childNodes.push(c); return c; }
    insertBefore(c: _FakeNode, ref: _FakeNode | null) {
      c.parentElement = this;
      if (ref == null) this.childNodes.push(c);
      else { const i = this.childNodes.indexOf(ref); this.childNodes.splice(i < 0 ? this.childNodes.length : 0, i, c); }
      return c;
    }
    removeChild(c: _FakeNode) { const i = this.childNodes.indexOf(c); if (i >= 0) this.childNodes.splice(i, 1); c.parentElement = null; return c; }
    get ownerDocument() { return _doc; }
    get tagName() { return this.nodeName.toUpperCase(); }
    addEventListener() {}
    removeEventListener() {}
  }
  const _doc: any = {
    createElement: (t: string) => { const n = new _FakeNode(); n.nodeName = t; return n; },
    createElementNS: (_ns: string, t: string) => { const n = new _FakeNode(); n.nodeName = t; return n; },
    createTextNode: (t: string) => { const n = new _FakeNode(); n.nodeType = 3; n.textContent = t; return n; },
    createComment: (t: string) => { const n = new _FakeNode(); n.nodeType = 8; n.textContent = t; return n; },
    body: new _FakeNode(),
    // head нужен на импорте: IconButton/Tabs вшивают <style> в document.head (модульный сайд-эффект)
    head: new _FakeNode(),
    documentElement: new _FakeNode(),
    getElementById: () => null,
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  class _FakeIframe {}
  const _win: any = {
    document: _doc, defaultView: null,
    // React в getActiveElementDeep проверяет instanceof window.HTMLIFrameElement
    HTMLIFrameElement: _FakeIframe,
    addEventListener: () => {}, removeEventListener: () => {},
    location: { href: '' }, navigator: { userAgent: 'test' },
    setTimeout: () => 0, clearTimeout: () => {},
    innerWidth: 1024,
    matchMedia: (_q: string) => ({ matches: false, media: _q, addEventListener: () => {}, removeEventListener: () => {}, onchange: null, dispatchEvent: () => true }),
  };
  (globalThis as any).document = _doc;
  (globalThis as any).window = _win;
  (globalThis as any).IS_REACT_ACT_ENVIRONMENT = true;
  _doc.defaultView = _win;
  return { FakeNode: _FakeNode as typeof _FakeNode };
});

import { HeaderBlock, hasForeignOwnedProject, OWNER_LIMIT_HINT_TEXT } from '../ChainsTab';

type FN = InstanceType<typeof FakeNode>;
function texts(n: FN, acc: string[] = []): string[] {
  if (n.nodeType === 3) { acc.push(n.textContent); return acc; }
  if (n.childNodes.length === 0 && n.textContent) acc.push(n.textContent);
  for (const c of n.childNodes) texts(c, acc);
  return acc;
}
async function mount(el: ReactElement): Promise<FN> {
  const container = new FakeNode();
  const root = createRoot(container);
  await act(async () => { root.render(el); });
  return container;
}

const base = { id: 'p', name: 'П', rootPath: '/p' } as Project;
const personal = base;                                   // личный проект: myRole нет
const ownShared = { ...base, id: 'p2', ownerId: 'me', myRole: 'owner' } as Project;
const foreignEditor = { ...base, id: 'p3', ownerId: 'other', myRole: 'editor' } as Project;
const foreignReader = { ...base, id: 'p4', ownerId: 'other', myRole: 'reader' } as Project;

describe('признак «состою в чужом общем проекте»', () => {
  it('пустой список, личные проекты и свои общие — признака нет', () => {
    expect(hasForeignOwnedProject([])).toBe(false);
    expect(hasForeignOwnedProject([personal])).toBe(false);
    expect(hasForeignOwnedProject([personal, ownShared])).toBe(false);
  });

  it('редактор или читатель чужого общего проекта — признак есть', () => {
    expect(hasForeignOwnedProject([personal, foreignEditor])).toBe(true);
    expect(hasForeignOwnedProject([ownShared, foreignReader])).toBe(true);
  });
});

describe('подсказка под полем бюджета', () => {
  it('текст — дословный, без числа лимита', () => {
    expect(OWNER_LIMIT_HINT_TEXT).toBe('В общих проектах других владельцев действует лимит владельца проекта');
  });

  it('без признака (c1) — строки нет', async () => {
    const el = createElement(HeaderBlock, { budget: 2, canEdit: false, showOwnerLimitHint: false, onBudgetChange: () => {} });
    const dom = await mount(el);
    expect(texts(dom).join('')).not.toContain('лимит владельца проекта');
  });

  it('с признаком (c2) — строка показана дословно', async () => {
    const el = createElement(HeaderBlock, { budget: 2, canEdit: false, showOwnerLimitHint: true, onBudgetChange: () => {} });
    const dom = await mount(el);
    expect(texts(dom).join('')).toContain(OWNER_LIMIT_HINT_TEXT);
  });

  // Вкладка обязана передавать признак в шапку: иначе тесты выше зелёны, а строки никто не показывает
  it('ChainsTab собирает признак из уже загруженного списка проектов', () => {
    const src = readFileSync(new URL('../ChainsTab.tsx', import.meta.url), 'utf8');
    expect(src).toContain('hasForeignOwnedProject(useAllProjects())');
    expect(src).toContain('showOwnerLimitHint={showOwnerLimitHint}');
  });
});
