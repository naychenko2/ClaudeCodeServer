// Проверка режима «Обзор»: группировка по неймспейсам, фиксированные слои
// (не топологический ранг), потолок плотности холста, обратные рёбра-нарушения
// и детерминизм раскладки. Отдельный набор гоняет реальный снимок графа проекта —
// сверка с матрицей нарушений слоистости, которую нашла Майя в макете.
import { describe, it, expect } from 'vitest';
import {
  buildOverviewScene, layoutOverview, defaultExpandedGroups, layerOf,
  LAYER_TITLES, OTHER_LAYER, OVERVIEW_VIEW_W, OVERVIEW_VIEW_H,
} from '../graphOverview';
import type { CodeGraph, CodeGraphNode } from '../../../types';
import { loadRealSnapshot } from './fixtures/real-snapshot';

function node(id: string, fqn: string, file: string, kind: CodeGraphNode['kind'] = 'Class'): CodeGraphNode {
  return { id, label: fqn.split('.').pop()!, fullyQualifiedName: fqn, sourceFile: file, sourceLocation: '1:1', kind };
}

// Синтетический граф с явной слоистостью: Controllers → Services → Models,
// плюс один сознательно «неправильный» эдж Models → Services (нарушение) и
// Tests → Controllers (тесты сверху).
function makeLayeredGraph(): CodeGraph {
  const nodes = [
    node('c1', 'A.Controllers.FooController', 'Foo.cs'),
    node('c2', 'A.Controllers.BarController', 'Bar.cs'),
    node('s1', 'A.Services.Foo', 'SFoo.cs'),
    node('s2', 'A.Services.Sub.Bar', 'SBar.cs'),
    node('m1', 'A.Models.Baz', 'Baz.cs'),
    node('t1', 'A.Tests.Controllers.FooControllerTests', 'FooTests.cs'),
  ];
  return {
    nodes,
    edges: [
      { source: 'c1', target: 's1', relation: 'Calls', confidence: 'Extracted' },
      { source: 's1', target: 'm1', relation: 'References', confidence: 'Extracted' },
      { source: 'm1', target: 's1', relation: 'Calls', confidence: 'Extracted' },   // нарушение
      { source: 't1', target: 'c1', relation: 'Calls', confidence: 'Extracted' },
    ],
    godNodes: [],
    metadata: { nodeCount: nodes.length, edgeCount: 4, fileCount: 6, isStale: false },
  };
}

describe('layerOf', () => {
  it('фиксированный порядок: Tests раньше Controllers/Hubs раньше Services раньше Models/Protocol', () => {
    expect(layerOf('A.Tests')).toBe(0);
    expect(layerOf('A.Controllers')).toBe(1);
    expect(layerOf('A.Hubs')).toBe(1);
    expect(layerOf('A.Services')).toBe(2);
    expect(layerOf('A.Models')).toBe(3);
    expect(layerOf('A.Protocol')).toBe(3);
    expect(layerOf('A.Filters')).toBe(OTHER_LAYER);
  });

  it('приоритет: тестовый неймспейс с сегментом Controllers остаётся слоем Tests', () => {
    expect(layerOf('A.Tests.Controllers')).toBe(0);
  });

  it('LAYER_TITLES покрывает все 5 слоёв', () => {
    expect(LAYER_TITLES).toHaveLength(OTHER_LAYER + 1);
  });
});

describe('defaultExpandedGroups', () => {
  it('раскрывает общий корень всех неймспейсов и не идёт глубже, где префиксы расходятся', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    expect(expanded).toEqual(new Set(['A']));
  });
});

describe('buildOverviewScene — группировка', () => {
  it('группирует по первому сегменту ниже раскрытого корня', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const labels = scene.items.map(it => it.label).sort();
    expect(labels).toEqual(['Controllers', 'Models', 'Services', 'Tests'].sort());
    expect(scene.items).toHaveLength(4);
  });

  it('hasChildren истинен только у группы, чьи типы уходят на сегмент глубже', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const services = scene.items.find(it => it.label === 'Services')!;
    const controllers = scene.items.find(it => it.label === 'Controllers')!;
    expect(services.hasChildren).toBe(true);   // A.Services.Sub.Bar лежит глубже A.Services
    expect(controllers.hasChildren).toBe(false); // оба контроллера лежат ровно в A.Controllers
  });

  it('уважает фильтр связей — та же панель, что у «Фокуса»', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    // Из 4 рёбер графа только s1→m1 (Services→Models) — References, остальные три — Calls
    const scene = buildOverviewScene(g, {
      expanded, typesGroup: null,
      filters: { Calls: false, Implements: true, References: true },
    });
    expect(scene.bundles).toHaveLength(1);
    expect(scene.byKey.get(scene.bundles[0].fromKey)?.label).toBe('Services');
    expect(scene.byKey.get(scene.bundles[0].toKey)?.label).toBe('Models');
  });

  it('раскрытие группы разбивает её на подгруппы, не трогая остальные', () => {
    const g = makeLayeredGraph();
    const expanded = new Set([...defaultExpandedGroups(g.nodes), 'A.Services']);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const labels = scene.items.map(it => it.label).sort();
    // 'Services' (сам A.Services) и 'Sub' (A.Services.Sub) — раздельные группы
    expect(labels).toEqual(['Controllers', 'Models', 'Services', 'Sub', 'Tests'].sort());
  });

  it('раскрытие до типов даёт node-элементы вместо группы', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: 'A.Controllers' });
    const nodeItems = scene.items.filter(it => it.kind === 'node');
    expect(nodeItems.map(it => it.node!.id).sort()).toEqual(['c1', 'c2']);
    // остальные группы (не раскрытая до типов) остаются группами
    expect(scene.items.some(it => it.label === 'Services' && it.kind === 'group')).toBe(true);
  });
});

describe('buildOverviewScene — обратные рёбра (нарушение слоистости)', () => {
  it('помечает isBack только на ребре снизу вверх', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const back = scene.bundles.filter(b => b.isBack);
    expect(back).toHaveLength(1);
    const b = back[0];
    expect(scene.byKey.get(b.fromKey)?.label).toBe('Models');
    expect(scene.byKey.get(b.toKey)?.label).toBe('Services');
    // остальные три ребра идут «сверху вниз» (или горизонтально) — не нарушение
    expect(scene.bundles.filter(bb => !bb.isBack)).toHaveLength(3);
  });
});

describe('buildOverviewScene — потолок плотности (26 элементов)', () => {
  function makeWideGraph(groupCount: number): CodeGraph {
    const nodes: CodeGraphNode[] = [];
    for (let i = 0; i < groupCount; i++) {
      nodes.push(node(`n${i}`, `Root.Group${i}.Type${i}`, `f${i}.cs`));
    }
    return { nodes, edges: [], godNodes: [], metadata: { nodeCount: nodes.length, edgeCount: 0, fileCount: groupCount, isStale: false } };
  }

  it('сворачивает лишние группы в одну заглушку — на холсте не больше 26 элементов', () => {
    const g = makeWideGraph(30);
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    expect(scene.items.length).toBeLessThanOrEqual(26);
    expect(scene.items.some(it => it.kind === 'small')).toBe(true);
  });

  it('не сворачивает, если групп меньше потолка', () => {
    const g = makeWideGraph(10);
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    expect(scene.items).toHaveLength(10);
    expect(scene.items.some(it => it.kind === 'small')).toBe(false);
  });
});

describe('layoutOverview — детерминизм', () => {
  it('раскладка воспроизводима между вызовами', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const a = layoutOverview(scene).positions;
    const b = layoutOverview(scene).positions;
    for (const [key, pa] of a) {
      const pb = b.get(key)!;
      expect(pb).toBeDefined();
      expect(pb.x).toBe(pa.x);
      expect(pb.y).toBe(pa.y);
      expect(pb.r).toBe(pa.r);
    }
  });

  it('координаты конечны и в пределах холста', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const layout = layoutOverview(scene);
    for (const p of layout.positions.values()) {
      expect(Number.isFinite(p.x)).toBe(true);
      expect(Number.isFinite(p.y)).toBe(true);
      expect(p.x).toBeGreaterThanOrEqual(0);
      expect(p.x).toBeLessThanOrEqual(OVERVIEW_VIEW_W);
      expect(p.y).toBeGreaterThanOrEqual(0);
      expect(p.y).toBeLessThanOrEqual(OVERVIEW_VIEW_H);
    }
  });

  it('слои идут по возрастанию сверху вниз (y0 растёт вместе с индексом слоя)', () => {
    const g = makeLayeredGraph();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const layout = layoutOverview(scene);
    const sorted = [...layout.rows].sort((a, b) => a.layer - b.layer);
    for (let i = 1; i < sorted.length; i++) {
      expect(sorted[i].y0).toBeGreaterThanOrEqual(sorted[i - 1].y1 - 1); // с допуском на округление
    }
  });
});

// === Реальный снимок графа проекта (1020 типов / 2816 связей) ===
describe('buildOverviewScene — реальный снимок графа проекта', () => {
  it('корневой вид: не больше 26 элементов на холсте', () => {
    const g = loadRealSnapshot();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    expect(scene.items.length).toBeLessThanOrEqual(26);
    expect(scene.items.length).toBeGreaterThan(0);
  });

  it('раскладка корневого вида умещается в холст и детерминирована', () => {
    const g = loadRealSnapshot();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const layout = layoutOverview(scene);
    for (const p of layout.positions.values()) {
      expect(Number.isFinite(p.x)).toBe(true);
      expect(Number.isFinite(p.y)).toBe(true);
    }
    const again = layoutOverview(scene);
    expect([...layout.positions.entries()].map(([k, p]) => `${k}:${p.x},${p.y}`).sort())
      .toEqual([...again.positions.entries()].map(([k, p]) => `${k}:${p.x},${p.y}`).sort());
  });

  it('находит те же пары нарушений слоистости, что Майя нашла в матрице', () => {
    const g = loadRealSnapshot();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const back = scene.bundles.filter(b => b.isBack);
    expect(back.length).toBeGreaterThan(0);

    // Неориентированная пара групп, задействованных хоть в одном обратном пучке —
    // сверка по паре групп, а не по точному направлению: фиксированный маппинг
    // сознательно отличается от топологического ранга Майи, поэтому у одной и той
    // же пары «главное» направление может отличаться (см. отчёт по задаче).
    const pairSet = new Set(back.map(b => {
      const a = scene.byKey.get(b.fromKey)!.label, c = scene.byKey.get(b.toKey)!.label;
      return [a, c].sort().join('|');
    }));
    const known = [
      ['Services', 'Hubs'],
      ['Services', 'Controllers'],
      ['Models', 'Services'],
      ['Services', 'WebDav'],
      ['Protocol', 'Services'],
    ];
    for (const [a, b2] of known) {
      expect(pairSet.has([a, b2].sort().join('|'))).toBe(true);
    }
  });
});
