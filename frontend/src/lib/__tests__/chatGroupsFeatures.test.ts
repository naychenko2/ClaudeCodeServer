// Режим «Фичи» (groupByFeatures): группировка чатов по индексу фич.
//
// Окружение node (без jsdom): тестируем ЧИСТУЮ функцию groupByFeatures,
// не React-хук. Контракт — «Контракт индекса фич — утверждён (Александр, 18.10)»,
// §3 «Правило дерева в «Фичах» — исправлено»:
//   1. Группа чата — первая фича из chats[id].
//   2. У чата без фичи — группа родителя (рекурсивно); корень без фичи — «Без фичи».
//   3. Ребро живёт только внутри группы; через границу фич ребёнок всплывает в корень.
//   4. ParentOverrideId не обрабатывается отдельно (уже в parentSessionId).
//   5. Мультифичевый чат — в группе первой фичи, строка одна.
//
// Тесты независимы от пояса: время — только ISO-строки фикстуры.

import { describe, it, expect } from 'vitest';
import { groupByFeatures, featureChipCounts, filterFeaturesByChip } from '../chatGroups';
import type { FeatureBucket, FeatureChatLink, FeatureChip, FeatureIndexFeature, FeaturesIndex } from '../../types/featureIndex';
import type { Session } from '../../types';

// Минимальная строка фичи: для группировки важны featureId, title, bucket.
function feature(id: string, bucket: FeatureBucket = 'running'): FeatureIndexFeature {
  return {
    featureId: id, title: `Фича ${id}`, projectId: 'p1', bucket,
    attention: null,
    rail: [
      { key: 'cut', state: 'done', tone: null },
      { key: 'plan', state: 'done', tone: null },
      { key: 'prod', state: 'active', tone: 'run' },
      { key: 'review', state: 'pending', tone: null },
      { key: 'qa', state: 'pending', tone: null },
      { key: 'final', state: 'pending', tone: null },
      { key: 'merge', state: 'pending', tone: null },
    ],
    chips: bucket === 'closed' ? ['closed'] : ['open'],
    stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
  };
}

// Минимальный чат: важны id, parentSessionId, updatedAt/createdAt (для порядка внутри группы).
function chat(id: string, over: Partial<Session> = {}): Session {
  return {
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual', ...over,
  } as Session;
}

const link = (featureId: string, kind: FeatureChatLink['kind'] = 'executor'): FeatureChatLink =>
  ({ featureId, kind, taskRole: null, stepRef: null, lane: null });

function index(features: FeatureIndexFeature[], chats: Record<string, FeatureChatLink[]>): FeaturesIndex {
  return { features, chats };
}

// Все ids чатов по всем группам (для проверки «без дублей»).
const allChatIds = (groups: ReturnType<typeof groupByFeatures>): string[] =>
  groups.flatMap(g => g.sessions.map(c => c.id));

describe('groupByFeatures: порядок групп', () => {
  it('порядок групп = порядок features[] бэка (фронт не пересортировывает)', () => {
    // Порядок намерено НЕ алфавитный и НЕ по bucket: attention → running → idle → closed
    const feats = [feature('Z', 'attention'), feature('M', 'running'), feature('A', 'idle'), feature('C', 'closed')];
    const idx = index(feats, {
      'z': [link('Z')], 'm': [link('M')], 'a': [link('A')], 'c': [link('C')],
    });
    // Чаты поданы в перемешанном порядке — на порядок групп не влияют
    const groups = groupByFeatures([chat('a'), chat('c'), chat('z'), chat('m')], idx);
    expect(groups.map(g => g.feature?.featureId)).toEqual(['Z', 'M', 'A', 'C']);
  });

  it('фича без чатов в видимом множестве — секцией не рисуется', () => {
    const feats = [feature('A', 'running'), feature('B', 'running')];
    const idx = index(feats, { 'a': [link('A')] });
    const groups = groupByFeatures([chat('a')], idx);
    // B нет в chats — её секции нет
    expect(groups.map(g => g.feature?.featureId)).toEqual(['A']);
  });
});

describe('groupByFeatures: мультифичевый чат', () => {
  it('стоит в группе первой фичи, строка одна (без дублей)', () => {
    const feats = [feature('X', 'running'), feature('Y', 'running')];
    const idx = index(feats, {
      // cur — мультифичевый: X (key) и Y (curator); первая — X
      'cur': [link('X', 'key'), link('Y', 'curator')],
    });
    const groups = groupByFeatures([chat('cur')], idx);
    // одна группа (X); Y без чатов — секции нет
    expect(groups).toHaveLength(1);
    expect(groups[0].feature?.featureId).toBe('X');
    // чат ровно в одной строке, без дубля
    expect(allChatIds(groups)).toEqual(['cur']);
  });

  it('перестановка chats[id] в индексе меняет группу (фронт следует за бэком, сам не выбирает)', () => {
    const feats = [feature('X', 'running'), feature('Y', 'running')];
    // cur — мультифичевый: X (key) и Y (curator).
    // Порядок [X, Y] → первая фича X → группа X.
    const idxXY = index(feats, { 'cur': [link('X', 'key'), link('Y', 'curator')] });
    expect(groupByFeatures([chat('cur')], idxXY)[0].feature?.featureId).toBe('X');
    // Переставляем chats['cur'] на [Y, X] → первая фича Y → группа Y (фронт не «чинит»
    // порядок, а следует за порядком, который отдал бэк).
    const idxYX = index(feats, { 'cur': [link('Y', 'curator'), link('X', 'key')] });
    expect(groupByFeatures([chat('cur')], idxYX)[0].feature?.featureId).toBe('Y');
  });
});

describe('groupByFeatures: «Без фичи»', () => {
  it('хвост «Без фичи» — последняя группа', () => {
    const feats = [feature('A', 'running')];
    const idx = index(feats, { 'a': [link('A')] });
    const groups = groupByFeatures([chat('a'), chat('orphan')], idx);
    expect(groups.map(g => g.title)).toEqual(['Фича A', 'Без фичи']);
    expect(groups[1].sessions.map(c => c.id)).toEqual(['orphan']);
  });

  it('чат без фичи наследует группу родителя (ребро внутри группы живёт)', () => {
    const feats = [feature('A', 'running')];
    const idx = index(feats, { 'root': [link('A', 'key')] });
    // child без фичи, но родитель root — в A → child наследует A
    const groups = groupByFeatures([chat('root'), chat('child', { parentSessionId: 'root' })], idx);
    expect(groups).toHaveLength(1);
    expect(groups[0].feature?.featureId).toBe('A');
    // ребро root→child в одной группе — сохранено
    expect(groups[0].sessions.find(c => c.id === 'child')?.parentSessionId).toBe('root');
  });

  it('висячий featureId (нет в features[]) тоже уходит в «Без фичи»', () => {
    const feats = [feature('A', 'running')];
    // чат ссылается на фичу 'GHOST', которой нет в features[]
    const idx = index(feats, { 'ghost': [link('GHOST')] });
    const groups = groupByFeatures([chat('ghost'), chat('a', { /* a не в chats — без фичи */ })], idx);
    // ghost (висячий) + a (без фичи) — оба в «Без фичи»
    const noFeat = groups.find(g => g.feature === null);
    expect(noFeat).toBeDefined();
    expect(noFeat!.sessions.map(c => c.id).sort()).toEqual(['a', 'ghost']);
  });
});

describe('groupByFeatures: закрытые фичи', () => {
  it('закрытые — отдельная секция: в конце, перед «Без фичи»', () => {
    // OrderRuns: running перед closed; закрытая — последняя из фич
    const feats = [feature('A', 'running'), feature('B', 'closed')];
    const idx = index(feats, { 'a': [link('A')], 'b': [link('B')] });
    const groups = groupByFeatures([chat('a'), chat('b')], idx);
    expect(groups.map(g => g.feature?.bucket)).toEqual(['running', 'closed']);
    // «Без фичи» нет (все чаты во фичах)
    expect(groups.some(g => g.feature === null)).toBe(false);
  });
});

describe('groupByFeatures: правило дерева (контракт §3)', () => {
  it('два исполнителя разных фич под одним оркестратором — каждый в своей группе', () => {
    // orch — оркестратор фичи A; execA (ребёнок orch) — в A; execB (ребёнок orch) — в B.
    // Ребро orch→execB через границу фич (A→B) обрезано: execB всплывает в корень B.
    const feats = [feature('A', 'running'), feature('B', 'running')];
    const idx = index(feats, {
      'orch': [link('A', 'orchestrator')],
      'execA': [link('A', 'executor')],
      'execB': [link('B', 'executor')],
    });
    const chats = [
      chat('orch'),
      chat('execA', { parentSessionId: 'orch' }),
      chat('execB', { parentSessionId: 'orch' }),
    ];
    const groups = groupByFeatures(chats, idx);
    // orch и execA — в A; execB — в B (ребро через границу обрезано)
    const gA = groups.find(g => g.feature?.featureId === 'A');
    const gB = groups.find(g => g.feature?.featureId === 'B');
    expect(gA?.sessions.map(c => c.id).sort()).toEqual(['execA', 'orch']);
    expect(gB?.sessions.map(c => c.id)).toEqual(['execB']);
    // execB стал корнем своей группы: parent обрезан
    expect(gB!.sessions[0].parentSessionId).toBeNull();
    // execA в той же группе что orch — ребро живёт
    expect(gA!.sessions.find(c => c.id === 'execA')?.parentSessionId).toBe('orch');
  });

  it('ParentOverrideId (в parentSessionId) подчиняется правилу: внутри фичи живёт, через границу всплывает', () => {
    // Ручной родитель: child «перетащен» под manualParent (это и есть parentSessionId).
    // manualParent — в фиче A, child — в фиче B → ребро через границу → child всплывает.
    const feats = [feature('A', 'running'), feature('B', 'running')];
    const idx = index(feats, {
      'manualParent': [link('A', 'key')],
      'child': [link('B', 'executor')],
    });
    const chats = [
      chat('manualParent'),
      chat('child', { parentSessionId: 'manualParent' }),
    ];
    const groups = groupByFeatures(chats, idx);
    const gB = groups.find(g => g.feature?.featureId === 'B');
    expect(gB?.sessions.map(c => c.id)).toEqual(['child']);
    // parent (manualParent) в другой фиче — ребро обрезано
    expect(gB!.sessions[0].parentSessionId).toBeNull();
  });

  it('мультифичевый корень: строка одна, в группе первой фичи', () => {
    const feats = [feature('X', 'running'), feature('Y', 'running')];
    const idx = index(feats, {
      'root': [link('X', 'key'), link('Y', 'curator')],
    });
    const groups = groupByFeatures([chat('root')], idx);
    expect(groups).toHaveLength(1);
    expect(groups[0].feature?.featureId).toBe('X');
    expect(allChatIds(groups)).toEqual(['root']);
  });
});

describe('groupByFeatures: мутация к порядку', () => {
  it('пересортировка features[] бэком меняет порядок групп (фронт её не «чинит»)', () => {
    const mk = (order: FeatureIndexFeature[]) =>
      index(order, {
        'a': [link('A')], 'b': [link('B')], 'c': [link('C')],
      });
    // Нормальный порядок OrderRuns: A, B, C
    const normal = groupByFeatures([chat('a'), chat('b'), chat('c')], mk([feature('A'), feature('B'), feature('C')]));
    expect(normal.map(g => g.feature?.featureId)).toEqual(['A', 'B', 'C']);
    // Мутация: бэк отдаёт тот же набор, но в другом порядке — фронт следует за бэком
    const mutated = groupByFeatures([chat('a'), chat('b'), chat('c')], mk([feature('C'), feature('A'), feature('B')]));
    expect(mutated.map(g => g.feature?.featureId)).toEqual(['C', 'A', 'B']);
  });
});

// Фича с произвольными chips[] (базовый feature() привязывает chips к bucket).
const withChips = (id: string, chips: FeatureChip[]): FeatureIndexFeature => ({ ...feature(id), chips });

describe('featureChipCounts: счётчики по chips[]', () => {
  it('считает вхождения каждого чипа по features[].chips', () => {
    const feats = [
      withChips('A', ['open', 'waiting']),
      withChips('B', ['open', 'stopped']),
      withChips('C', ['closed']),
    ];
    expect(featureChipCounts(feats)).toEqual({ open: 2, waiting: 1, stopped: 1, merge: 0, closed: 1 });
  });

  it('пустой список — все нули', () => {
    expect(featureChipCounts([])).toEqual({ open: 0, waiting: 0, stopped: 0, merge: 0, closed: 0 });
  });
});

describe('filterFeaturesByChip: фильтр по чипу', () => {
  it('возвращает фичи, чьи chips[] содержат чип', () => {
    const feats = [
      withChips('A', ['open', 'waiting']),
      withChips('B', ['open', 'stopped']),
      withChips('C', ['closed']),
    ];
    expect(filterFeaturesByChip(feats, 'open').map(f => f.featureId)).toEqual(['A', 'B']);
    expect(filterFeaturesByChip(feats, 'waiting').map(f => f.featureId)).toEqual(['A']);
    expect(filterFeaturesByChip(feats, 'stopped').map(f => f.featureId)).toEqual(['B']);
    expect(filterFeaturesByChip(feats, 'merge')).toEqual([]);
    expect(filterFeaturesByChip(feats, 'closed').map(f => f.featureId)).toEqual(['C']);
  });
});
