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
import { groupByFeatures, featureChipCounts, featureGroupTreeRows, otherFeatureCount, featureRoleFor, drawnClosedFeatures } from '../chatGroups';
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

// Опции для featureGroupTreeRows: всё видно, ничего не свёрнуто, активного нет.
const noTreeOpts = { isVisible: (() => true) as (c: import('../../types').Session) => boolean, collapsedIds: new Set<string>(), activeId: null as string | null };

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

describe('groupByFeatures: bucket merged', () => {
  it('порядок бэка attention → running → merged → idle → closed сохраняется', () => {
    const feats = [feature('Z', 'attention'), feature('M', 'running'), feature('G', 'merged'), feature('A', 'idle'), feature('C', 'closed')];
    const idx = index(feats, { 'z': [link('Z')], 'm': [link('M')], 'g': [link('G')], 'a': [link('A')], 'c': [link('C')] });
    const groups = groupByFeatures([chat('a'), chat('c'), chat('g'), chat('z'), chat('m')], idx);
    expect(groups.map(g => g.feature?.featureId)).toEqual(['Z', 'M', 'G', 'A', 'C']);
  });

  it('чип merged считается по chips[]', () => {
    const feats = [withChips('G', ['open', 'merged'])];
    expect(featureChipCounts(feats)).toMatchObject({ open: 1, merged: 1 });
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
    expect(featureChipCounts(feats)).toEqual({ open: 2, waiting: 1, stopped: 1, merge: 0, closed: 1, running: 0, merged: 0 });
  });

  it('пустой список — все нули', () => {
    expect(featureChipCounts([])).toEqual({ open: 0, waiting: 0, stopped: 0, merge: 0, closed: 0, running: 0, merged: 0 });
  });
});

// === C1: правило дерева на уровне рендера списка (не только чистая groupByFeatures) ===
// Сценарий из контракта С0: оркестратор — куратор двух фич (A и B), исполнители обеих
// висят под ним. groupByFeatures режет межфичевые рёбра: исполнители A остаются деревом
// в группе A (под оркестратором), исполнитель B всплывает корнем в группе B. featureGroupTreeRows
// строит «своё дерево» каждой группы — тест проверяет именно нарисованные строки (depth/вложенность),
// а не только группировку (groupByFeatures даёт полный список, и её юнит-тест был зелёным).
describe('featureGroupTreeRows: правило дерева (C1, уровень рендера)', () => {
  it('оркестратор двух фич + исполнители обеих → две группы, каждая со своим деревом', () => {
    // A — идёт (open), B — стоит (attention stopped → чип «Стоят»)
    const feats = [
      feature('A', 'running'),
      { ...feature('B', 'attention'), attention: { kind: 'stopped', since: '2026-10-18T10:00:00Z', reason: null, personaId: null, sessionId: null }, chips: ['open', 'stopped'] },
    ];
    const idx = index(feats, {
      // orch — мультифичевый куратор: A (первая) и B
      'orch': [link('A', 'curator'), link('B', 'curator')],
      'execA': [link('A', 'executor')],
      'execB': [link('B', 'executor')],
    });
    const chats = [
      chat('orch'),
      chat('execA', { parentSessionId: 'orch' }),
      chat('execB', { parentSessionId: 'orch' }),
    ];
    const groups = groupByFeatures(chats, idx);
    const gA = groups.find(g => g.feature?.featureId === 'A')!;
    const gB = groups.find(g => g.feature?.featureId === 'B')!;
    expect([gA, gB].every(Boolean)).toBe(true);

    // Группа A: оркестратор (корень, ключевой) + исполнитель A (ребёнок) — дерево живёт
    const rowsA = featureGroupTreeRows(gA.sessions, 'orch', null, noTreeOpts);
    const orchRow = rowsA.find(r => r.chat.id === 'orch')!;
    const execARow = rowsA.find(r => r.chat.id === 'execA')!;
    expect(orchRow.depth).toBe(0);
    expect(execARow.depth).toBe(1); // вложен под оркестратором (ребро внутри группы)
    // Ключевой (оркестратор) — первой строкой
    expect(rowsA[0].chat.id).toBe('orch');
    // Исполнитель B (группа B) НЕ в дереве группы A: межфичевое ребро orch→execB обрезано.
    // Мутация «рендер по segByRootId» (исходное дерево) кладёт execB под orch в группу A —
    // это утверждение краснеет (C1: группа первой фичи не тянет детей второй).
    expect(rowsA.find(r => r.chat.id === 'execB')).toBeUndefined();

    // Группа B: исполнитель B — корень (ребро к оркестратору через границу фич обрезано)
    const rowsB = featureGroupTreeRows(gB.sessions, null, null, noTreeOpts);
    expect(rowsB).toHaveLength(1);
    expect(rowsB[0].chat.id).toBe('execB');
    expect(rowsB[0].depth).toBe(0); // не вложен под оркестратором
  });

  it('«Стоят» ведёт в группу второй фичи: стоящая фича — в своём дереве, а не в первой', () => {
    const feats = [
      feature('A', 'running'),
      { ...feature('B', 'attention'), attention: { kind: 'stopped', since: '2026-10-18T10:00:00Z', reason: null, personaId: null, sessionId: null }, chips: ['open', 'stopped'] },
    ];
    const idx = index(feats, {
      'orch': [link('A', 'curator'), link('B', 'curator')],
      'execB': [link('B', 'executor')],
    });
    const chats = [chat('orch'), chat('execB', { parentSessionId: 'orch' })];
    const groups = groupByFeatures(chats, idx);
    // Чип «Стоят» — только фича B; её группа рисуется (исполнитель B — корень)
    expect(feats.filter(f => f.chips.includes('stopped')).map(f => f.featureId)).toEqual(['B']);
    const gB = groups.find(g => g.feature?.featureId === 'B')!;
    expect(gB).toBeDefined();
    const rowsB = featureGroupTreeRows(gB.sessions, null, null, noTreeOpts);
    expect(rowsB.map(r => r.chat.id)).toEqual(['execB']);
    // Фича A (идёт) не в «Стоят», но её группа (оркестратор) тоже рисуется отдельно
    const gA = groups.find(g => g.feature?.featureId === 'A')!;
    expect(featureGroupTreeRows(gA.sessions, 'orch', null, noTreeOpts).map(r => r.chat.id)).toEqual(['orch']);
  });

  it('ключевой чат не первым корнем → его поддерево переставляется в начало', () => {
    // Два корня в группе: X (свежий, buildChatTreeRows поставит первым) и KEY (ключевой).
    // Ключевой обязан быть первым — функция переставляет его поддерево в начало.
    const feats = [feature('A', 'running')];
    const idx = index(feats, { 'x': [link('A', 'executor')], 'key': [link('A', 'curator')] });
    const chats = [
      chat('x', { updatedAt: '2026-10-18T12:00:00Z' }),
      chat('key', { updatedAt: '2026-10-18T09:00:00Z' }),
    ];
    const groups = groupByFeatures(chats, idx);
    const gA = groups[0];
    const rows = featureGroupTreeRows(gA.sessions, 'key', null, noTreeOpts);
    expect(rows[0].chat.id).toBe('key'); // ключевой первым, хотя x свежее
  });
});

// === M1: «Нет фич» — чаты без фичи не пропадают ===
// Ранний return с EmptyState уходил раньше секции «Без фичи»: в режиме «Фичи» проект
// без фич выглядел пустым. Исправление — EmptyState + (ниже) обычные чаты. Ядро:
// groupByFeatures с ПУСТЫМ индексом кладёт все чаты в «Без фичи» (ничего не теряется).
describe('M1: «Нет фич» — чаты без фичи', () => {
  it('пустой индекс: все чаты — в «Без фичи», ни один не теряется', () => {
    // Индекс без фич (features: []) — все чаты без связи
    const idx = index([], {});
    const chats = [chat('a'), chat('b'), chat('c', { parentSessionId: 'a' })];
    const groups = groupByFeatures(chats, idx);
    // Одна группа — «Без фичи» (feature === null), в ней все чаты
    expect(groups).toHaveLength(1);
    expect(groups[0].feature).toBeNull();
    expect(groups[0].title).toBe('Без фичи');
    // Все 3 чата на месте (включая вложенного b под a)
    expect(groups[0].sessions.map(c => c.id).sort()).toEqual(['a', 'b', 'c']);
  });

  it('чат без фичи наследует дерево: вложенный остаётся под родителем', () => {
    const idx = index([], {});
    const chats = [chat('a'), chat('c', { parentSessionId: 'a' })];
    const groups = groupByFeatures(chats, idx);
    const rows = featureGroupTreeRows(groups[0].sessions, null, null, noTreeOpts);
    // a — корень, c — вложен (ребро внутри «Без фичи» живёт)
    expect(rows.find(r => r.chat.id === 'a')!.depth).toBe(0);
    expect(rows.find(r => r.chat.id === 'c')!.depth).toBe(1);
  });
});

// === M2: мультифичевый чат — чип «ещё N» ===
// otherFeatureCount: число других фич чата (кроме первой, в группе которой он нарисован).
// Мультифичевый чат (оркестратор/куратор нескольких фич) — «ещё N» на строке в группе
// первой фичи: след, где живут его ключевые чаты остальных фич (C0, п.5).
describe('M2: otherFeatureCount (чип «ещё N»)', () => {
  it('мультифичевый чат: N = число других фич', () => {
    const feats = [feature('A', 'running'), feature('B', 'running'), feature('C', 'running')];
    const idx = index(feats, {
      // orch — куратор трёх фич: A (первая), B, C → «ещё 2»
      'orch': [link('A', 'curator'), link('B', 'curator'), link('C', 'curator')],
      // single — одна фича → «ещё 0»
      'single': [link('A', 'executor')],
      // none — не в chats → 0
    });
    expect(otherFeatureCount('orch', idx)).toBe(2);
    expect(otherFeatureCount('single', idx)).toBe(0);
    expect(otherFeatureCount('none', idx)).toBe(0);
  });
});

// === M3: роль чата в группе — по id САМОЙ строки, не замыканием корня ===
// featureRoleFor: «ключевой» (ключевой чат фичи) и/или «ещё N» (чат в ещё N фичах).
// Считается по id строки: ChatTreeBranch рекурсивно передаёт renderCard потомкам, и
// замыкание роли корня лило её на всё поддерево (M3). Мутация (в отчёте): роль по
// фиксированному id (корню) вместо id строки — «plain» (не ключевой, не мультифичевый)
// получает «ключевой», тест краснеет.
describe('M3: featureRoleFor (роль по id строки, не корня)', () => {
  const feats = [feature('A', 'running'), feature('B', 'running'), feature('C', 'running')];
  const idx = index(feats, {
    // key — ключевой чат фичи A, ровно одна фича
    'key': [link('A', 'key')],
    // multi — мультифичевый: A (первая) + B + C → «ещё 2»
    'multi': [link('A', 'executor'), link('B', 'executor'), link('C', 'executor')],
    // plain — одна фича (A), не ключевой → метки нет
    'plain': [link('A', 'executor')],
  });

  it('ключевой чат: «ключевой» (без других фич) / «ключевой · ещё N» (с другими)', () => {
    // key — ровно A → «ключевой»
    expect(featureRoleFor('key', 'key', idx)).toBe('ключевой');
    // multi — A + B + C, ключевой = multi → «ключевой · ещё 2»
    expect(featureRoleFor('multi', 'multi', idx)).toBe('ключевой · ещё 2');
  });

  it('не-ключевой мультифичевый: «ещё N»; обычный (одна фича) и вне индекса — метки нет', () => {
    // multi не ключевой (keyId='key') → «ещё 2»
    expect(featureRoleFor('multi', 'key', idx)).toBe('ещё 2');
    // plain — одна фича, не ключевой → undefined
    expect(featureRoleFor('plain', 'key', idx)).toBeUndefined();
    // ghost — не в индексе → undefined
    expect(featureRoleFor('ghost', 'key', idx)).toBeUndefined();
  });
});

// === m2: drawnClosedFeatures — закрытые фичи, которые РИСУЮТСЯ (≥1 чат) ===
// Тумблер «Показывать закрытые фичи · N» и заголовок секции «Закрытые · M» должны
// показывать ОДНО число: фича без чатов не рисуется — её не считаем (chatCount > 0).
// Мутация (в отчёте): убрать условие chatCount > 0 → фича без чатов считается, тест краснеет.
describe('m2: drawnClosedFeatures (закрытые, которые рисуются)', () => {
  it('считает только закрытые с ≥1 чатом; без чатов и running — не считает', () => {
    const withChat = { ...feature('A', 'closed'), chatCount: 3 };
    const noChat = { ...feature('B', 'closed'), chatCount: 0 };
    const running = { ...feature('C', 'running'), chatCount: 5 };
    expect(drawnClosedFeatures([withChat, noChat, running])).toBe(1);
  });
});


