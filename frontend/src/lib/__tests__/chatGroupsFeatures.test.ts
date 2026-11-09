// Режим «Фичи» (groupByFeatures): группировка чатов по индексу фич.
//
// Окружение node (без jsdom): тестируем ЧИСТУЮ функцию groupByFeatures,
// не React-хук. Контракт — «Контракт индекса фич — утверждён (Александр, 18.10)»,
// §3, изменён 24.10 (решение Андрея, вариант 1):
//   1. Группа чата — ВСЕ featureId из chats[id]: мультифичевый чат в каждой своей
//      группе один раз, в строке — пометка «общий с …».
//   2. У чата без фичи — группа родителя (рекурсивно); корень без фичи — «Без фичи».
//   3. Ребро живёт в группе, где есть и родитель; иначе ребёнок всплывает в корень.
//   4. ParentOverrideId не обрабатывается отдельно (уже в parentSessionId).
//   5. Висячий featureId (нет в features[]) группы не создаёт — чат идёт по п.2
//      (группа родителя, рекурсивно); в «Без фичи» — только если цепочка родителей тоже без фичи.
//
// Тесты независимы от пояса: время — только ISO-строки фикстуры.

import { describe, it, expect } from 'vitest';
import { groupByFeatures, featureGroupTreeRows, sharedFeatureTitles, featureRoleFor, FEATURE_KIND_LABEL, featureKindCounts, featureMatchesKinds, shownFiltersModel } from '../chatGroups';
import { ALL_FEATURE_KINDS, type FeatureKind } from '../chatFilters';
import type { FeatureBucket, FeatureChatLink, FeatureIndexFeature, FeaturesIndex } from '../../types/featureIndex';
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
  it('порядок групп = порядок features[] (фронт не сортирует по bucket)', () => {
    // Порядок features[] намеренно перемешан: фронт не сортирует по bucket, группы идут как у бэка
    const feats = [feature('G', 'merged'), feature('C', 'closed'), feature('Z', 'attention'), feature('A', 'idle'), feature('M', 'running')];
    const idx = index(feats, { 'z': [link('Z')], 'm': [link('M')], 'g': [link('G')], 'a': [link('A')], 'c': [link('C')] });
    const groups = groupByFeatures([chat('a'), chat('c'), chat('g'), chat('z'), chat('m')], idx);
    expect(groups.map(g => g.feature?.featureId)).toEqual(['G', 'C', 'Z', 'A', 'M']);
  });
});

describe('groupByFeatures: мультифичевый чат — в каждой своей группе', () => {
  it('чат с двумя featureId присутствует в обеих группах ровно по одному разу', () => {
    const feats = [feature('X', 'running'), feature('Y', 'running')];
    const idx = index(feats, {
      // cur — мультифичевый: X (key) и Y (curator)
      'cur': [link('X', 'key'), link('Y', 'curator')],
    });
    const groups = groupByFeatures([chat('cur')], idx);
    // Обе группы рисуются, и в каждой чат — ровно один раз (без дубля внутри группы)
    expect(groups.map(g => g.feature?.featureId)).toEqual(['X', 'Y']);
    for (const g of groups) expect(g.sessions.map(c => c.id)).toEqual(['cur']);
  });

  it('перестановка chats[id] в индексе не меняет членство: чат в обеих группах', () => {
    const feats = [feature('X', 'running'), feature('Y', 'running')];
    const idxXY = index(feats, { 'cur': [link('X', 'key'), link('Y', 'curator')] });
    const idxYX = index(feats, { 'cur': [link('Y', 'curator'), link('X', 'key')] });
    // Мутация «берётся первая фича» (старое правило) краснит этот тест:
    // при ней в группах был бы только X (или только Y).
    for (const idx of [idxXY, idxYX]) {
      const ids = groupByFeatures([chat('cur')], idx).map(g => g.feature?.featureId).sort();
      expect(ids).toEqual(['X', 'Y']);
    }
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

  it('висячий featureId (нет в features[]) без родителя — уходит в «Без фичи»', () => {
    const feats = [feature('A', 'running')];
    // чат ссылается на фичу 'GHOST', которой нет в features[]
    const idx = index(feats, { 'ghost': [link('GHOST')] });
    const groups = groupByFeatures([chat('ghost'), chat('a', { /* a не в chats — без фичи */ })], idx);
    // ghost (висячий) + a (без фичи) — оба в «Без фичи»
    const noFeat = groups.find(g => g.feature === null);
    expect(noFeat).toBeDefined();
    expect(noFeat!.sessions.map(c => c.id).sort()).toEqual(['a', 'ghost']);
  });

  it('висячий featureId + родитель в фиче → группа родителя, а не «Без фичи» (правило 5)', () => {
    // Мутация (в отчёте): если resolve() не рекурсирует к родителю, child попадает
    // в «Без фичи» — группа не одна, и тест краснеет.
    const feats = [feature('A', 'running')];
    const idx = index(feats, {
      'parent': [link('A', 'key')],   // родитель — в фиче A
      'ghostChild': [link('GHOST')],  // у child только висячая ссылка
    });
    const groups = groupByFeatures(
      [chat('parent'), chat('ghostChild', { parentSessionId: 'parent' })],
      idx,
    );
    // одна группа — A; «Без фичи» нет
    expect(groups).toHaveLength(1);
    expect(groups[0].feature?.featureId).toBe('A');
    expect(groups[0].sessions.map(c => c.id).sort()).toEqual(['ghostChild', 'parent']);
    // ребро parent→child внутри одной группы — сохранено
    expect(groups[0].sessions.find(c => c.id === 'ghostChild')?.parentSessionId).toBe('parent');
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

  it('мультифичевый корень: он в каждой своей группе', () => {
    const feats = [feature('X', 'running'), feature('Y', 'running')];
    const idx = index(feats, {
      'root': [link('X', 'key'), link('Y', 'curator')],
    });
    const groups = groupByFeatures([chat('root')], idx);
    expect(groups.map(g => g.feature?.featureId)).toEqual(['X', 'Y']);
    for (const g of groups) expect(g.sessions.map(c => c.id)).toEqual(['root']);
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

// === C1: правило дерева на уровне рендера списка (не только чистая groupByFeatures) ===
// Сценарий из контракта С0, изменённый 24.10: оркестратор — куратор двух фич (A и B),
// исполнители обеих висят под ним. Мультифичевый оркестратор рисуется в каждой своей
// группе, и ребро к исполнителю живёт в той группе, где есть и родитель: в группе A —
// дерево orch→execA, в группе B — orch→execB. featureGroupTreeRows строит «своё дерево»
// каждой группы — тест проверяет именно нарисованные строки (depth/вложенность).
describe('featureGroupTreeRows: правило дерева (C1, уровень рендера)', () => {
  it('оркестратор двух фич + исполнители обеих → две группы, каждая со своим деревом', () => {
    // A — идёт (open), B — стоит (attention stopped → чип «Стоят»)
    const feats = [
      feature('A', 'running'),
      { ...feature('B', 'attention'), attention: { kind: 'stopped', since: '2026-10-18T10:00:00Z', reason: null, personaId: null, sessionId: null }, chips: ['open', 'stopped'] },
    ];
    const idx = index(feats, {
      // orch — мультифичевый куратор: A и B (в обеих группах)
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
    // Исполнитель B НЕ в дереве группы A: он связан только с фичей B — группы A нет
    // в его groupsOf. Мутация «рендер по segByRootId» (исходное дерево) кладёт execB
    // под orch в группу A — это утверждение краснеет (C1: чужой исполнитель не тянется).
    expect(rowsA.find(r => r.chat.id === 'execB')).toBeUndefined();

    // Группа B: оркестратор тоже здесь (мультифичевый — в каждой своей группе), и ребро
    // orch→execB живёт: родитель в группе B. Исполнитель B — не корень, а ребёнок.
    const rowsB = featureGroupTreeRows(gB.sessions, null, null, noTreeOpts);
    expect(rowsB).toHaveLength(2);
    const orchRowB = rowsB.find(r => r.chat.id === 'orch')!;
    const execBRow = rowsB.find(r => r.chat.id === 'execB')!;
    expect(orchRowB.depth).toBe(0);
    expect(execBRow.depth).toBe(1);
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
    // Чип «Стоят» — только фича B; её группа рисуется: оркестратор (общий) + исполнитель B под ним
    expect(feats.filter(f => f.chips.includes('stopped')).map(f => f.featureId)).toEqual(['B']);
    const gB = groups.find(g => g.feature?.featureId === 'B')!;
    expect(gB).toBeDefined();
    const rowsB = featureGroupTreeRows(gB.sessions, null, null, noTreeOpts);
    expect(rowsB.map(r => r.chat.id).sort()).toEqual(['execB', 'orch']);
    expect(rowsB.find(r => r.chat.id === 'execB')!.depth).toBe(1); // ребро живёт: родитель в группе B
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

// === M2: общий чат — пометка «общий с …» (решение Андрея 24.10, вариант 1) ===
// sharedFeatureTitles: названия других известных фич чата (кроме текущей группы).
// Мультифичевый чат рисуется в группе каждой своей фичы; пометка называет остальные.
describe('M2: sharedFeatureTitles (пометка «общий с …»)', () => {
  it('названия других фич; текущая группа и висячие id не входят', () => {
    const feats = [feature('A', 'running'), feature('B', 'running'), feature('C', 'running')];
    const idx = index(feats, {
      // orch — куратор трёх фич: в группе A пометка называет B и C
      'orch': [link('A', 'curator'), link('B', 'curator'), link('C', 'curator')],
      // single — одна фича → пометки нет
      'single': [link('A', 'executor')],
      // ghost — висячий featureId (нет в features[]) → названия неизвестны, не упоминается
      'ghost': [link('GHOST')],
      // none — не в chats → пусто
    });
    expect(sharedFeatureTitles('orch', 'A', idx)).toEqual(['Фича B', 'Фича C']);
    expect(sharedFeatureTitles('single', 'A', idx)).toEqual([]);
    expect(sharedFeatureTitles('ghost', 'A', idx)).toEqual([]);
    expect(sharedFeatureTitles('none', 'A', idx)).toEqual([]);
  });
});

// === M3: роль чата в группе — по id САМОЙ строки, не замыканием корня ===
// featureRoleFor: «ключевой» (ключевой чат фичи) и/или «общий с …» (чат
// показывается и в группах других своих фич). Считается по id строки: ChatTreeBranch
// рекурсивно передаёт renderCard потомкам, и замыкание роли корня лило её на всё
// поддерево (M3). Мутация (в отчёте): роль по фиксированному id (корню) вместо id
// строки — «plain» (не ключевой, не мультифичевый) получает «ключевой», тест краснеет.
// Формат метки ограничен (минор Глеба, 24.10): первая другая фича + «· ещё N».
describe('M3: featureRoleFor (роль по id строки, пометка «общий с …»)', () => {
  const feats = [feature('A', 'running'), feature('B', 'running'), feature('C', 'running')];
  const idx = index(feats, {
    // key — ключевой чат фичи A, ровно одна фича
    'key': [link('A', 'key')],
    // multi — мультифичевый: A + B + C → в группе A пометка «общий с Фича B · ещё 1»
    'multi': [link('A', 'executor'), link('B', 'executor'), link('C', 'executor')],
    // plain — одна фича (A), не ключевой → метки нет
    'plain': [link('A', 'executor')],
  });

  it('ключевой чат: «ключевой» (без других фич) / «ключевой · общий с …» (с другими)', () => {
    // key — ровно A → «ключевой»
    expect(featureRoleFor('key', 'key', idx, 'A')).toBe('ключевой');
    // multi — A + B + C, ключевой = multi → первая другая + счётчик
    expect(featureRoleFor('multi', 'multi', idx, 'A')).toBe('ключевой · общий с Фича B · ещё 1');
  });

  it('не-ключевой мультифичевый: «общий с …» в каждой группе — первая другая + «· ещё N»', () => {
    // multi не ключевой (keyId='key') → в группе A названа B, остальные — счётчиком
    expect(featureRoleFor('multi', 'key', idx, 'A')).toBe('общий с Фича B · ещё 1');
    expect(featureRoleFor('multi', 'key', idx, 'B')).toBe('общий с Фича A · ещё 1');
    // plain — одна фича, не ключевой → undefined
    expect(featureRoleFor('plain', 'key', idx, 'A')).toBeUndefined();
    // ghost — не в индексе → undefined
    expect(featureRoleFor('ghost', 'key', idx, 'A')).toBeUndefined();
  });

  it('ровно одна другая фича — счётчика «· ещё» нет', () => {
    const two = index([feature('A'), feature('B')], { 'pair': [link('A'), link('B')] });
    expect(featureRoleFor('pair', null, two, 'A')).toBe('общий с Фича B');
  });

  it('длина ограничена: четыре фичы → названа первая, остальные — «· ещё 2»', () => {
    const four = index(
      [feature('A'), feature('B'), feature('C'), feature('D')],
      { 'multi': [link('A'), link('B'), link('C'), link('D')] },
    );
    // Полный список («Фича B, Фича C, Фича D») на 360 px выталкивал имя чата из строки
    expect(featureRoleFor('multi', null, four, 'A')).toBe('общий с Фича B · ещё 2');
  });
});

// === Ф-1.3: ось «Фичи · что показывать» — подписи, счётчики, отбор, «Показаны» ===
// Записка feature-state-filter-1.md п. 2 (таблица подписей) и п. 4 (строка «Показаны»).
// Фронт принадлежность фичи к состоянию НЕ пересчитывает: читается только kind из
// индекса (сторож в конце).

// Фича с явным kind и chatCount (базовый feature() kind не задаёт).
const withKind = (id: string, kind: FeatureKind | null, chatCount = 1): FeatureIndexFeature =>
  ({ ...feature(id), kind, chatCount });

describe('Ф-1.3: FEATURE_KIND_LABEL (подписи оси, записка п. 2)', () => {
  it('ключи равны ALL_FEATURE_KINDS как множества, ровно 9', () => {
    expect(ALL_FEATURE_KINDS.length).toBe(9);
    expect(Object.keys(FEATURE_KIND_LABEL).length).toBe(9);
    expect(new Set(Object.keys(FEATURE_KIND_LABEL))).toEqual(new Set(ALL_FEATURE_KINDS));
  });

  it('подписи — из таблицы п. 2 (мн. ч.; «Смержены» — девятая)', () => {
    expect(FEATURE_KIND_LABEL).toEqual({
      waiting_you: 'Ждут вас', error: 'Остановлены', running: 'Идут', queued: 'В очереди',
      paused: 'На паузе', draft: 'Черновики', idle: 'Не начаты', merged: 'Смержены', closed: 'Закрытые',
    });
  });
});

describe('Ф-1.3: featureKindCounts (счётчики по kind, только рисуемые)', () => {
  it('сумма = числу фич с chatCount>0; без чатов не считается', () => {
    const feats = [
      withKind('A', 'running', 1),
      withKind('B', 'closed', 2),
      withKind('C', 'draft', 0),
      withKind('E', 'merged', 1),
    ];
    expect(featureKindCounts(feats)).toEqual({
      waiting_you: 0, error: 0, running: 1, queued: 0, paused: 0, draft: 0, idle: 0, merged: 1, closed: 1,
    });
    const sum = ALL_FEATURE_KINDS.reduce((s, k) => s + featureKindCounts(feats)[k], 0);
    expect(sum).toBe(feats.filter(f => f.chatCount > 0).length);
  });

  it('фича с kind=null (старый индекс без поля) в счётчики не попадает', () => {
    const base = [withKind('A', 'running', 1), withKind('B', 'closed', 2)];
    const withNull = [...base, withKind('D', null, 5)];
    expect(featureKindCounts(withNull)).toEqual(featureKindCounts(base));
  });

  // Ф-1.3b (R-Ф1.3, замечание 2): неизвестный kind (бэковый словарь обновился
  // раньше фронтального) не создаёт десятого ключа и NaN в счётчиках.
  // Мутация (в отчёте): убрать `k in counts` из featureKindCounts → этот тест
  // красный (10 ключей, NaN); откат → зелёный.
  it('неизвестный kind (бэк обновился раньше фронта): не считается — ровно 9 числовых ключей', () => {
    const unknown = withKind('U', 'новое' as unknown as FeatureKind, 3);
    const counts = featureKindCounts([unknown, withKind('A', 'running', 1), withKind('C', 'closed', 1)]);
    const keys = Object.keys(counts);
    expect(keys.length).toBe(9);
    expect(new Set(keys)).toEqual(new Set(ALL_FEATURE_KINDS));
    expect(keys.every(k => Number.isFinite(counts[k as FeatureKind]))).toBe(true);
    expect(counts.running).toBe(1);
    expect(counts.closed).toBe(1);
  });

  it('пустой список — все нули', () => {
    const counts = featureKindCounts([]);
    expect(ALL_FEATURE_KINDS.map(k => counts[k])).toEqual(ALL_FEATURE_KINDS.map(() => 0));
  });
});

describe('Ф-1.3: featureMatchesKinds (отбор по выбранному kind, чистая)', () => {
  it('умолчание [] — все открытые видны, closed скрыт', () => {
    for (const k of ALL_FEATURE_KINDS.filter(x => x !== 'closed')) {
      expect(featureMatchesKinds(withKind('F', k), [])).toBe(true);
    }
    expect(featureMatchesKinds(withKind('C', 'closed'), [])).toBe(false);
  });

  it('явный выбор — только свой kind; «только закрытые» — только closed', () => {
    expect(featureMatchesKinds(withKind('A', 'waiting_you'), ['waiting_you'])).toBe(true);
    expect(featureMatchesKinds(withKind('B', 'error'), ['waiting_you'])).toBe(false);
    expect(featureMatchesKinds(withKind('C', 'closed'), ['closed'])).toBe(true);
    expect(featureMatchesKinds(withKind('A', 'waiting_you'), ['closed'])).toBe(false);
    expect(featureMatchesKinds(withKind('M', 'merged'), ['merged'])).toBe(true);
    expect(featureMatchesKinds(withKind('M', 'merged'), ['running'])).toBe(false);
  });

  it('kind нет/null — видна при любом выборе, кроме «только закрытые»', () => {
    expect(featureMatchesKinds(withKind('N', null), [])).toBe(true);
    expect(featureMatchesKinds(withKind('N', null), ['running'])).toBe(true);
    expect(featureMatchesKinds(withKind('N', null), ['running', 'closed'])).toBe(true);
    expect(featureMatchesKinds(withKind('N', null), ['closed'])).toBe(false);
    // Старый ответ без поля вовсе (undefined) — то же правило.
    const noField = { ...withKind('N', null) };
    delete (noField as { kind?: FeatureKind | null }).kind;
    expect(featureMatchesKinds(noField, [])).toBe(true);
    expect(featureMatchesKinds(noField, ['closed'])).toBe(false);
  });
});

describe('Ф-1.3: shownFiltersModel (строка «Показаны», 4 строки таблицы п. 4)', () => {
  const open = ALL_FEATURE_KINDS.filter(k => k !== 'closed');
  const chip = (kind: FeatureKind, action: 'remove' | 'restore', andBefore = false) => ({ kind, action, andBefore });

  it('умолчание ([] и все открытые без closed) — строки нет (null)', () => {
    expect(shownFiltersModel([])).toBeNull();
    expect(shownFiltersModel([...open])).toBeNull();
  });

  it('строка 1: on пусто, выбраны только закрытые → «Показаны:» [Закрытые ×], снимает → умолчание', () => {
    expect(shownFiltersModel(['closed'])).toEqual({
      prefix: 'Показаны:', chips: [chip('closed', 'remove')],
    });
  });

  it('строка 2: off пусто, закрытые включены → «Показаны все открытые и» [Закрытые ×]', () => {
    expect(shownFiltersModel([...ALL_FEATURE_KINDS])).toEqual({
      prefix: 'Показаны все открытые и', chips: [chip('closed', 'remove')],
    });
  });

  it('строка 3: on ≤ off → «Показаны:» + снятые значения (remove); + [Закрытые ×] если выбраны', () => {
    expect(shownFiltersModel(['waiting_you', 'error', 'closed'])).toEqual({
      prefix: 'Показаны:',
      chips: [chip('waiting_you', 'remove'), chip('error', 'remove'), chip('closed', 'remove')],
    });
    // Граница сравнения: on = off = 4 — эта строка (условие ≤).
    expect(shownFiltersModel(['waiting_you', 'error', 'running', 'queued'])).toEqual({
      prefix: 'Показаны:',
      chips: [chip('waiting_you', 'remove'), chip('error', 'remove'), chip('running', 'remove'), chip('queued', 'remove')],
    });
  });

  it('строка 4: иначе → «Все открытые, кроме:» + снятые (restore); «и» [Закрытые ×] если выбраны', () => {
    expect(shownFiltersModel([...open.filter(k => k !== 'draft')])).toEqual({
      prefix: 'Все открытые, кроме:', chips: [chip('draft', 'restore')],
    });
    expect(shownFiltersModel([...open.filter(k => k !== 'draft'), 'closed'])).toEqual({
      prefix: 'Все открытые, кроме:',
      chips: [chip('draft', 'restore'), chip('closed', 'remove', true)],
    });
  });
});

// Сторож (разрез: «фронт не пересчитывает»): отбор и счётчики читают только kind из
// индекса. Ф-1.3b (R-Ф1.3, замечание 1): текстовый запрет — по идентификатору, а не
// по доступу через точку: `const { bucket } = f` и `f['bucket']` старый regex
// пропускал молча. Поведенческая проверка — на фиче с противоречивыми данными
// (kind='running', bucket='closed'): принадлежность категории определяет ТОЛЬКО kind.
// Мутация (в отчёте): в featureMatchesKinds `const k = f.kind` →
// `const { kind: k, bucket } = f` и вернуть bucket в сравнение → оба теста красные;
// откат → зелёный.
describe('Ф-1.3: сторож — featureMatchesKinds/featureKindCounts не читают state/chips/bucket', () => {
  it('поведенчески: kind="running", bucket="closed" — видна и считается по kind', () => {
    const f = { ...feature('X', 'closed'), kind: 'running', chatCount: 1 };
    // Умолчание: running — открытая → видна; bucket на отбор не влияет.
    expect(featureMatchesKinds(f, [])).toBe(true);
    // «Только закрытые»: kind ≠ closed → скрыта; bucket её не тянет в выбор.
    expect(featureMatchesKinds(f, ['closed'])).toBe(false);
    // Счётчики: по kind, не по bucket.
    const counts = featureKindCounts([f]);
    expect(counts.running).toBe(1);
    expect(counts.closed).toBe(0);
  });

  it('исходник функций не содержит идентификаторов state/chips/bucket', () => {
    const src = `${featureMatchesKinds.toString()}\n${featureKindCounts.toString()}`;
    expect(src).not.toMatch(/\b(state|chips|bucket)\b/);
  });
});


