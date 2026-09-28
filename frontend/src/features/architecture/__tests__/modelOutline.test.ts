import { describe, it, expect } from 'vitest';
import { buildOutlineTree, outlinePath, parseOutline, type ArchElement } from '../modelOutline';

const el = (id: string, level: ArchElement['level'], parentId: string | null, extra: Partial<ArchElement> = {}): ArchElement =>
  ({ id, name: id, level, parentId, connections: 0, ...extra });

describe('buildOutlineTree', () => {
  it('строит иерархию система → контейнер → компонент', () => {
    const tree = buildOutlineTree([
      el('sys', 'system', null),
      el('c1', 'container', 'sys'),
      el('k1', 'component', 'c1'),
      el('k2', 'component', 'c1'),
    ]);
    expect(tree).toHaveLength(1);
    expect(tree[0].children.map(n => n.el.id)).toEqual(['c1']);
    expect(tree[0].children[0].children.map(n => n.el.id)).toEqual(['k1', 'k2']);
  });

  it('схлопывает единственного ребёнка-двойника по имени или описанию', () => {
    const tree = buildOutlineTree([
      el('sys', 'system', null),
      el('c1', 'container', 'sys', { name: 'Tasks' }),
      el('k1', 'component', 'c1', { name: 'Tasks' }),
      el('c2', 'container', 'sys', { name: 'Tts', description: 'Озвучка' }),
      el('k2', 'component', 'c2', { name: 'Services/Tts', description: 'Озвучка' }),
      el('c3', 'container', 'sys', { name: 'Images' }),
      el('k3', 'component', 'c3', { name: 'Services' }),
    ]);
    const [c1, c2, c3] = tree[0].children;
    expect(c1.children).toEqual([]);
    expect(c2.children).toEqual([]);
    expect(c3.children.map(n => n.el.id)).toEqual(['k3']);
  });

  it('элемент с висящим или собственным parentId становится корнем', () => {
    const tree = buildOutlineTree([
      el('sys', 'system', null),
      el('lost', 'container', 'nope'),
      el('self', 'container', 'self'),
    ]);
    expect(tree.map(n => n.el.id)).toEqual(['sys', 'lost', 'self']);
  });
});

describe('outlinePath', () => {
  it('собирает имена предков через «›»', () => {
    const els = [
      el('sys', 'system', null, { name: 'CCS' }),
      el('c1', 'container', 'sys', { name: 'Notes' }),
      el('k1', 'component', 'c1', { name: 'Controllers' }),
    ];
    expect(outlinePath(els, 'k1')).toBe('CCS › Notes');
    expect(outlinePath(els, 'sys')).toBe('');
  });
});

describe('parseOutline', () => {
  it('читает признак external', () => {
    const doc = { state: { model: { systems: [{ id: 'a', name: 'A', external: true }, { id: 'b', name: 'B' }] } } };
    const [a, b] = parseOutline(JSON.stringify(doc));
    expect(a.external).toBe(true);
    expect(b.external).toBeUndefined();
  });
});
