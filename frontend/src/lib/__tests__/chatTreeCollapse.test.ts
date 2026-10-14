import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — ни DOM, ни testing-library нет. Хук гоняется настоящий: useState,
// useRef и useEffect подменены крошечным прогонщиком с той же семантикой, что у React
// для одного компонента — setState в рендере перезапускает рендер, эффекты идут после
// рендера и только при смене зависимостей, setState из эффекта даёт новый рендер.
const h = vi.hoisted(() => {
  type Effect = { deps?: readonly unknown[]; cleanup?: unknown };
  let slots: unknown[] = [];
  let effects: Effect[] = [];
  let pending: (() => void)[] = [];
  let idx = 0;
  let eIdx = 0;
  let dirty = false;

  const useState = (init: unknown) => {
    const i = idx++;
    if (!(i in slots)) slots[i] = typeof init === 'function' ? (init as () => unknown)() : init;
    const set = (v: unknown) => {
      slots[i] = typeof v === 'function' ? (v as (p: unknown) => unknown)(slots[i]) : v;
      dirty = true;
    };
    return [slots[i], set];
  };
  const useRef = (init: unknown) => {
    const i = idx++;
    if (!(i in slots)) slots[i] = { current: init };
    return slots[i];
  };
  const useEffect = (fn: () => unknown, deps?: readonly unknown[]) => {
    const i = eIdx++;
    const prev = effects[i];
    const changed = !prev || !deps || !prev.deps
      || deps.length !== prev.deps.length || deps.some((d, k) => !Object.is(d, prev.deps![k]));
    if (!changed) return;
    pending.push(() => {
      if (typeof prev?.cleanup === 'function') (prev.cleanup as () => void)();
      effects[i] = { deps, cleanup: fn() };
    });
  };

  function render<T>(hook: () => T): T {
    for (let guard = 0; guard < 20; guard++) {
      dirty = false; idx = 0; eIdx = 0; pending = [];
      const out = hook();
      // setState прямо в рендере — React выбрасывает этот рендер и перерисовывает сразу
      if (dirty) continue;
      pending.forEach(f => f());
      if (!dirty) return out;
    }
    throw new Error('бесконечный цикл рендеров');
  }
  const reset = () => { slots = []; effects = []; pending = []; };

  return { useState, useRef, useEffect, render, reset };
});

vi.mock('react', async importOriginal => ({
  ...(await importOriginal<typeof import('react')>()),
  useState: h.useState,
  useRef: h.useRef,
  useEffect: h.useEffect,
}));

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, String(v)); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;

const { useTreeCollapse } = await import('../chatTree');
type Known = Parameters<typeof useTreeCollapse>[1];

const collapsedKey = (scope: string) => `cc_chat_tree_collapsed:${scope}`;
const expandedKey = (scope: string) => `cc_chat_tree_expanded:${scope}`;
const stored = (key: string) => JSON.parse(store.get(key) ?? '[]') as string[];
const seed = (scope: string, collapsed: string[], expanded: string[] = []) => {
  store.set(collapsedKey(scope), JSON.stringify(collapsed));
  store.set(expandedKey(scope), JSON.stringify(expanded));
};
const run = (scope: string, known?: Known) => h.render(() => useTreeCollapse(scope, known));

beforeEach(() => {
  store.clear();
  h.reset();
});

describe('useTreeCollapse', () => {
  it('переключение ветки сохраняется в память своей области', () => {
    let r = run('p1');
    r.toggleCollapse('a', false);
    r = run('p1');
    expect([...r.collapsedIds]).toEqual(['a']);
    expect(stored(collapsedKey('p1'))).toEqual(['a']);

    r.toggleCollapse('a', true);
    r = run('p1');
    expect([...r.collapsedIds]).toEqual([]);
    expect([...r.expandedIds]).toEqual(['a']);
    expect(stored(expandedKey('p1'))).toEqual(['a']);
  });

  it('по загруженному списку своей области вычищает удалённые чаты', () => {
    seed('p1', ['a', 'gone'], ['gone2']);
    run('p1', { scope: 'p1', ids: ['a', 'b'] });
    expect(stored(collapsedKey('p1'))).toEqual(['a']);
    expect(stored(expandedKey('p1'))).toEqual([]);
  });

  it('пустой список не чистит память — он ещё не загрузился', () => {
    seed('p1', ['a']);
    run('p1', { scope: 'p1', ids: [] });
    run('p1');
    expect(stored(collapsedKey('p1'))).toEqual(['a']);
  });

  it('смена проекта: список старого проекта не стирает память нового', () => {
    seed('p1', ['a1']);
    seed('p2', ['b1'], ['b2']);
    run('p1', { scope: 'p1', ids: ['a1'] });

    // Первый рендер после смены: project.id уже новый, а sessions — ещё старого
    let r = run('p2', { scope: 'p1', ids: ['a1'] });
    expect([...r.collapsedIds]).toEqual(['b1']);
    expect([...r.expandedIds]).toEqual(['b2']);
    expect(stored(collapsedKey('p2'))).toEqual(['b1']);
    expect(stored(expandedKey('p2'))).toEqual(['b2']);

    // Приехал свежий список нового проекта — чистка уже по нему
    r = run('p2', { scope: 'p2', ids: ['b1', 'b2', 'b3'] });
    expect([...r.collapsedIds]).toEqual(['b1']);
    expect(stored(collapsedKey('p2'))).toEqual(['b1']);
    // Память старого проекта не тронута
    expect(stored(collapsedKey('p1'))).toEqual(['a1']);
  });

  it('переключение после чистки не возвращает удалённые чаты в память', () => {
    seed('p1', ['a', 'gone']);
    const r = run('p1', { scope: 'p1', ids: ['a', 'x'] });
    r.toggleCollapse('x', false);
    run('p1', { scope: 'p1', ids: ['a', 'x'] });
    expect(stored(collapsedKey('p1')).sort()).toEqual(['a', 'x']);
  });
});
