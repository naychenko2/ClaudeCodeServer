// Проверка режима «Обзор»: группировка по неймспейсам, фиксированные слои
// (не топологический ранг), потолок плотности холста, обратные рёбра-нарушения
// и детерминизм раскладки. Отдельный набор гоняет реальный снимок графа проекта —
// сверка с матрицей нарушений слоистости, которую нашла Майя в макете.
import { describe, it, expect } from 'vitest';
import {
  buildOverviewScene, layoutOverview, defaultExpandedGroups, layerOf, fqnIndex, pathToType,
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
  it('фиксированный порядок: Tests раньше точек входа раньше Services раньше Models/Protocol', () => {
    expect(layerOf('A.Tests')).toBe(0);
    expect(layerOf('A.Controllers')).toBe(1);
    expect(layerOf('A.Hubs')).toBe(1);
    expect(layerOf('A.Services')).toBe(2);
    expect(layerOf('A.Models')).toBe(3);
    expect(layerOf('A.Protocol')).toBe(3);
    expect(layerOf('A.Telemetry')).toBe(OTHER_LAYER);
  });

  it('точки входа лежат в одном слое с Controllers: WebDav, Tray, Filters', () => {
    // Слой — роль в потоке зависимостей, а не «сервис или нет»: у всех троих нет
    // входящих связей из нашего кода, дёргают их снаружи (HTTP, клик по трею, pipeline)
    expect(layerOf('ClaudeHomeServer.WebDav')).toBe(1);
    expect(layerOf('ClaudeHomeServer.Tray')).toBe(1);
    expect(layerOf('ClaudeHomeServer.Filters')).toBe(1);
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

describe('pathToType — путь от корня к группе типа (сквозной вход в «Фокус»)', () => {
  // Сквозной вход (поиск, god-список) обходит ручное раскрытие «Обзора»: цепочка
  // группа-шагов строится из namespaceOf узла заново, чтобы «назад» из «Фокуса»
  // приводил в «Обзор», раскрытый ровно до этой группы (см. lib/codeGraph.ts).
  it('строит цепочку префиксов после автоматически раскрытого корня', () => {
    const g = makeLayeredGraph();
    const fqns = fqnIndex(g.nodes);
    const auto = defaultExpandedGroups(g.nodes);   // {'A'}
    const s2 = g.nodes.find(n => n.id === 's2')!;  // A.Services.Sub.Bar
    expect(pathToType(s2, fqns, auto)).toEqual(['A.Services', 'A.Services.Sub']);
  });

  it('лист без более глубокого раскрытия — один элемент цепочки', () => {
    const g = makeLayeredGraph();
    const fqns = fqnIndex(g.nodes);
    const auto = defaultExpandedGroups(g.nodes);
    const c1 = g.nodes.find(n => n.id === 'c1')!;  // A.Controllers.FooController
    expect(pathToType(c1, fqns, auto)).toEqual(['A.Controllers']);
  });
});

describe('buildOverviewScene — вложенные типы', () => {
  // FQN вложенного типа выглядит как «неймспейс + внешний класс + имя», и наивный
  // разбор до последней точки делал из внешнего класса отдельную группу.
  function makeNestedGraph(): CodeGraph {
    const nodes = [
      node('o', 'A.B.Outer', 'Outer.cs'),
      node('i', 'A.B.Outer.Inner', 'Outer.cs'),        // вложенный
      node('d', 'A.B.Outer.Inner.Deep', 'Outer.cs'),   // вложенный вдвойне
      node('s', 'A.B.Sub.Real', 'Real.cs'),            // настоящий поднеймспейс
    ];
    return { nodes, edges: [], godNodes: [], metadata: { nodeCount: nodes.length, edgeCount: 0, fileCount: 2, isStale: false } };
  }

  it('вложенный тип относится к неймспейсу внешнего типа, а не порождает группу из класса', () => {
    const g = makeNestedGraph();
    const scene = buildOverviewScene(g, { expanded: new Set(['A', 'A.B']), typesGroup: null });
    const groups = scene.items.map(it => it.group).sort();
    expect(groups).toEqual(['A.B', 'A.B.Sub']);   // группы 'A.B.Outer' быть не должно
    const ab = scene.items.find(it => it.group === 'A.B')!;
    expect(ab.count).toBe(3);                     // Outer + Inner + Deep — один класс с вложенными
    expect(ab.hasChildren).toBe(false);           // вложенность класса — не «уровень глубже»
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

  // Раскрытые подгруппы всегда мельче групп верхнего уровня, поэтому отбор по одному
  // размеру уносил в заглушку именно то, ради чего пользователь кликал по группе
  it('щадит раскрытую ветку: её подгруппы остаются на холсте, схлопывается посторонняя мелочь', () => {
    const nodes: CodeGraphNode[] = [];
    for (let i = 0; i < 20; i++) {
      for (let j = 0; j < 10; j++) nodes.push(node(`b${i}_${j}`, `Root.Big${i}.Type${j}`, `b${i}.cs`));
    }
    for (let i = 0; i < 8; i++) nodes.push(node(`s${i}`, `Root.Small.Sub${i}.Type`, `s${i}.cs`));
    const g: CodeGraph = { nodes, edges: [], godNodes: [], metadata: { nodeCount: nodes.length, edgeCount: 0, fileCount: 28, isStale: false } };

    const scene = buildOverviewScene(g, { expanded: new Set(['Root', 'Root.Small']), typesGroup: null });
    expect(scene.items.length).toBeLessThanOrEqual(26);        // потолок соблюдён
    for (let i = 0; i < 8; i++) {
      expect(scene.items.some(it => it.group === `Root.Small.Sub${i}`)).toBe(true);
    }
    expect(scene.items.some(it => it.kind === 'small')).toBe(true);  // схлопнулись крупные, но нераскрытые
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

  it('раскрытие Services реально показывает её подгруппы, а не заглушку', () => {
    const g = loadRealSnapshot();
    const base = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, {
      expanded: new Set([...base, 'ClaudeHomeServer.Services']), typesGroup: null,
    });
    const groups = new Set(scene.items.map(it => it.group));
    for (const sub of ['Llm', 'CodeGraph', 'Backup', 'Memory', 'Spend', 'Execution', 'TriggerSources']) {
      expect(groups.has(`ClaudeHomeServer.Services.${sub}`)).toBe(true);
    }
    // Внешние классы с вложенными типами (SessionManager, NotesService) — это типы,
    // а не подгруппы: их содержимое лежит в самой ClaudeHomeServer.Services
    expect(groups.has('ClaudeHomeServer.Services.SessionManager')).toBe(false);
    expect(groups.has('ClaudeHomeServer.Services.NotesService')).toBe(false);
    expect(scene.items.length).toBeLessThanOrEqual(26);
  });

  it('находит те же нарушения слоистости, что Майя нашла в матрице, и в ту же сторону', () => {
    const g = loadRealSnapshot();
    const expanded = defaultExpandedGroups(g.nodes);
    const scene = buildOverviewScene(g, { expanded, typesGroup: null });
    const back = scene.bundles.filter(b => b.isBack);
    expect(back.length).toBeGreaterThan(0);

    // Сверка ОРИЕНТИРОВАННАЯ: у нарушения важно не только «эта пара групп связана»,
    // но и кто кого зовёт снизу вверх — иначе ошибка в маппинге слоёв переворачивает
    // стрелку молча (так WebDav, попав в «Прочее», выдавал нарушением нормальный
    // поток WebDav → Services).
    const dirSet = new Set(back.map(b =>
      `${scene.byKey.get(b.fromKey)!.label}→${scene.byKey.get(b.toKey)!.label}`));
    for (const dir of ['Services→Hubs', 'Services→Controllers', 'Models→Services',
                       'Protocol→Services', 'Services→WebDav']) {
      expect(dirSet.has(dir)).toBe(true);
    }

    // WebDav — точка входа (0 входящих из нашего кода), поэтому нарушение ровно одно:
    // Services.UserStore → WebDav.NtlmHelper. Обратный поток WebDav → Services идёт
    // сверху вниз и пунктиром помечаться не должен.
    expect(dirSet.has('WebDav→Services')).toBe(false);
    const webDav = back.find(b => scene.byKey.get(b.fromKey)!.label === 'Services'
      && scene.byKey.get(b.toKey)!.label === 'WebDav')!;
    expect(webDav.weight).toBe(1);
  });
});
