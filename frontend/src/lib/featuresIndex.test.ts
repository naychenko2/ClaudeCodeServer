// Индекс фич (фасет «Фичи» и плашка в чате): стор.
//
// Окружение node (без jsdom): блоки 1–3 тестируют ЛОГИКУ стора через тестовые
// швы `__test`, а не React-хук (effects не идут); блок 4 — сам хук с эффектами
// (fakeDom + mount, паттерн FeatureBannerSheet.test.ts):
//   1 — стор разделён по проекту: срез по проекту, стабильная ссылка, загрузка;
//   2 — лукап featureOf по фикстуре: мультифичевый куратор, исполнитель,
//      дедупликация по (чат × фича) между срезами;
//   3 — живой режим: task_changed → перезапрос затронутого проекта (дебаунс
//        1,5 с), событие без фичи игнорируется, флаг выключен → запросов нет;
//   4 — несколько потребителей (R3, минор 5): выключенный потребитель не
//        стирает срез и не отписывает чужую подписку; последний ушедший — чистит.
//
// Тесты независимы от пояса: время — только fake timers и ISO-строки фикстуры.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { __test, featureOf, useFeaturesIndex } from './featuresIndex';
import { api } from './api';
import { upsertTaskLocal } from './tasks';
import { setAllFlags } from './featureFlags';
import { installFakeDom, mount, uninstallFakeDom } from '../components/chat/__tests__/fakeDom';
import type { FeaturesIndex, FeatureIndexFeature } from '../types/featureIndex';
import type { Task } from '../types';
import fixture from './__fixtures__/featuresIndex.json';

// signalr: onMessage — no-op (иначе getConnection() строит реальное соединение
// '/hubs/session', которое в node не резолвится; паттерн FeatureBannerSheet.test.ts).
vi.mock('./signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('./signalr')>();
  return { ...orig, onMessage: () => () => {} };
});

const FLAG = 'features-overview';
// Фикстура — по утверждённому контракту: все bucket, закрытая с summary.mergedAt,
// мультифичевый куратор, ключевой архивный и в другом проекте.
const FIXTURE = fixture as unknown as FeaturesIndex;

// Минимальная строка фичи (для тестов изоляции срезов).
function feature(id: string): FeatureIndexFeature {
  return {
    featureId: id, title: `Фича ${id}`, projectId: 'p1',
    bucket: 'running', attention: null,
    rail: [
      { key: 'cut', state: 'done', tone: null },
      { key: 'plan', state: 'done', tone: null },
      { key: 'prod', state: 'active', tone: 'run' },
      { key: 'review', state: 'pending', tone: null },
      { key: 'qa', state: 'pending', tone: null },
      { key: 'final', state: 'pending', tone: null },
      { key: 'merge', state: 'pending', tone: null },
    ],
    chips: ['open'], stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
  };
}

function task(over: Partial<Task>): Task {
  return {
    id: 't', title: 'Задача', description: '', status: 'todo', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 1, createdAt: '', updatedAt: '',
    ...over,
  } as Task;
}

// ── 1: стор разделён по проекту ───────────────────────────────────────────────

describe('стор индекса разделён по проекту', () => {
  beforeEach(() => __test.reset());
  afterEach(() => __test.reset());

  it('срез по проекту: p1 видит только p1, p2 — только p2', () => {
    const a: FeaturesIndex = { features: [feature('A')], chats: {} };
    const b: FeaturesIndex = { features: [feature('B')], chats: {} };
    __test.setIndex('p1', a);
    __test.setIndex('p2', b);
    expect(__test.snapshotFor('p1')).toBe(a);
    expect(__test.snapshotFor('p2')).toBe(b);
    // срезы разных проектов — разные ссылки (не общий объект)
    expect(__test.snapshotFor('p1')).not.toBe(__test.snapshotFor('p2'));
  });

  it('setIndex("p2") не меняет срез p1 (стабильная ссылка)', () => {
    const a: FeaturesIndex = { features: [feature('A')], chats: {} };
    __test.setIndex('p1', a);
    const aBefore = __test.snapshotFor('p1');
    expect(aBefore).toBe(a);
    // новый fetch для p2 — срез p1 остаётся тем же объектом (иначе useSyncExternalStore зациклит)
    __test.setIndex('p2', { features: [feature('B')], chats: {} });
    expect(__test.snapshotFor('p1')).toBe(aBefore);
    expect(__test.snapshotFor('p1')).toBe(a);
  });

  it('срез несуществующего проекта — EMPTY (стабильная ссылка)', () => {
    expect(__test.snapshotFor('nope')).toBe(__test.snapshotFor('nope'));
    expect(__test.snapshotFor('').features).toHaveLength(0);
  });

  it('clearIndex() — все срезы пусты', () => {
    __test.setIndex('p1', { features: [feature('A')], chats: {} });
    __test.setIndex('p2', { features: [feature('B')], chats: {} });
    __test.clearIndex();
    expect(__test.snapshotFor('p1').features).toHaveLength(0);
    expect(__test.snapshotFor('p2').features).toHaveLength(0);
  });

  it('загрузка: setLoading помечает проект, isIndexLoading читает', () => {
    expect(__test.isIndexLoading('p1')).toBe(false);
    __test.setLoading('p1', true);
    expect(__test.isIndexLoading('p1')).toBe(true);
    // чужий проект не затронут
    expect(__test.isIndexLoading('p2')).toBe(false);
    __test.setLoading('p1', false);
    expect(__test.isIndexLoading('p1')).toBe(false);
  });
});

// ── 2: лукап featureOf по фикстуре ─────────────────────────────────────────────

describe('лукап featureOf по фикстуре', () => {
  beforeEach(() => {
    __test.reset();
    // фикстура — ответ глобальной области (без projectId) → срез ''
    __test.setIndex('', FIXTURE);
  });
  afterEach(() => __test.reset());

  it('мультифичевый чат: все фичи, порядок — как в chats[] бэка; виды curator/curator/orchestrator', () => {
    const entries = featureOf('chat-cur');
    // featureOf не пересортировывает: порядок = порядок links в chats[chat-cur] (бэк
    // отдаёт по силе). feat-closed — curator (chat-cur — ключевой/создавший эпик),
    // feat-attention — curator, feat-running — orchestrator.
    expect(entries.map(e => e.feature.featureId)).toEqual(['feat-closed', 'feat-attention', 'feat-running']);
    expect(entries.map(e => e.link.kind)).toEqual(['curator', 'curator', 'orchestrator']);
  });

  it('исполнитель: роль executor с taskRole/stepRef/lane', () => {
    const entries = featureOf('chat-exec-1');
    expect(entries).toHaveLength(1);
    expect(entries[0].feature.featureId).toBe('feat-attention');
    expect(entries[0].link.kind).toBe('executor');
    expect(entries[0].link.taskRole).toBe('step');
    expect(entries[0].link.stepRef).toBe('Ш1');
    expect(entries[0].link.lane).toBe('бэк');
  });

  it('ключевой чат: архивный (feat-running) и в другом проекте (feat-idle)', () => {
    const arch = featureOf('chat-key-arch');
    expect(arch).toHaveLength(1);
    expect(arch[0].feature.featureId).toBe('feat-running');
    // ключевой чат в chats приходит не как 'key' (зарезервирован), а как curator
    expect(arch[0].link.kind).toBe('curator');
    // ключевой чат фичи feat-idle живёт в другом проекте (keyProjectId = p2)
    const other = featureOf('chat-key-other');
    expect(other[0].feature.featureId).toBe('feat-idle');
    expect(other[0].feature.keyProjectId).toBe('p2');
  });

  it('чат без фичи (и null) — пустой список', () => {
    expect(featureOf('nope')).toEqual([]);
    expect(featureOf(null)).toEqual([]);
  });

  it('дедупликация: та же пара (чат × фича) в двух срезах — одна запись', () => {
    // тот же ответ как срез проекта p1: чат cur виден и в '', и в 'p1'
    __test.setIndex('p1', FIXTURE);
    const entries = featureOf('chat-cur');
    expect(entries).toHaveLength(3);
    expect(entries.map(e => e.feature.featureId)).toEqual(['feat-closed', 'feat-attention', 'feat-running']);
  });
});

// ── 3: живой режим — перезапрос по task_changed ───────────────────────────────

describe('live-режим: перезапрос затронутого проекта', () => {
  let featuresIndexSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
    setAllFlags({ [FLAG]: true });
    // Шпион на REST-запрос: доказываем, что именно этот проект был перезапрошен.
    featuresIndexSpy = vi.spyOn(api.tasks, 'featuresIndex').mockResolvedValue(FIXTURE);
  });
  afterEach(() => {
    featuresIndexSpy.mockRestore();
    __test.reset();
    vi.useRealTimers();
  });

  it('всплеск из 5 событий → 1 перезапрос (дебаунс 1,5 с)', () => {
    upsertTaskLocal(task({ id: 'epic-1', role: 'feature', projectId: 'p1' }));
    __test.setActive('p1');
    const msg = { type: 'task_changed' as const, action: 'updated' as const, task: task({ id: 'epic-1', role: 'feature', projectId: 'p1' }) };
    for (let i = 0; i < 5; i++) __test.handleTaskChanged(msg);
    // один таймер на проект (дебаунс), не пять
    expect([...__test.pending()].filter(k => k === 'p1')).toHaveLength(1);
    // до окна дебаунса запрос не уходит, после — ровно один
    vi.advanceTimersByTime(1499);
    expect(featuresIndexSpy).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(featuresIndexSpy).toHaveBeenCalledTimes(1);
    expect(featuresIndexSpy).toHaveBeenCalledWith('p1');
  });

  it('task_changed по эпику (role=feature) → перезапрос его проекта', () => {
    upsertTaskLocal(task({ id: 'epic-2', role: 'feature', projectId: 'p2' }));
    __test.setActive('p2');

    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'epic-2', role: 'feature', projectId: 'p2' }) });

    expect(__test.pending().has('p2')).toBe(true);
    vi.advanceTimersByTime(1500);
    expect(featuresIndexSpy).toHaveBeenCalledTimes(1);
    expect(featuresIndexSpy).toHaveBeenCalledWith('p2');
  });

  it('task_changed по дочерней (featureId) → перезапрос проекта ЭПИКА', () => {
    // эпик живёт в p1; дочерняя задача ссылается на него
    upsertTaskLocal(task({ id: 'epic-3', role: 'feature', projectId: 'p1' }));
    upsertTaskLocal(task({ id: 'step-3', featureId: 'epic-3' }));
    __test.setActive('p1');

    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'step-3', featureId: 'epic-3' }) });

    expect(__test.pending().has('p1')).toBe(true);
    vi.advanceTimersByTime(1500);
    expect(featuresIndexSpy).toHaveBeenCalledTimes(1);
    expect(featuresIndexSpy).toHaveBeenCalledWith('p1');
  });

  it('обычная задача (без featureId/role) — нет перезапроса', () => {
    __test.setActive('p1');
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'plain' }) });
    expect(__test.pending().has('p1')).toBe(false);
    vi.advanceTimersByTime(1500);
    expect(featuresIndexSpy).not.toHaveBeenCalled();
  });

  it('флаг выключен — событие проигнорировано (нет перезапроса)', () => {
    setAllFlags({ [FLAG]: false });
    upsertTaskLocal(task({ id: 'epic-4', role: 'feature', projectId: 'p1' }));
    __test.setActive('p1');

    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'epic-4', role: 'feature', projectId: 'p1' }) });

    expect(__test.pending().has('p1')).toBe(false);
    vi.advanceTimersByTime(1500);
    expect(featuresIndexSpy).not.toHaveBeenCalled();
  });

  it('deleted — игнорируется (как в featureRuns: агрегат пересчитается на следующем событии)', () => {
    upsertTaskLocal(task({ id: 'epic-5', role: 'feature', projectId: 'p1' }));
    __test.setActive('p1');

    __test.handleTaskChanged({ type: 'task_changed', action: 'deleted', task: task({ id: 'epic-5', role: 'feature', projectId: 'p1' }) });

    expect(__test.pending().has('p1')).toBe(false);
  });

  it('неактивный проект (никто его не смотрит) — нет перезапроса', () => {
    upsertTaskLocal(task({ id: 'epic-6', role: 'feature', projectId: 'p3' }));
    // p3 не в _active
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'epic-6', role: 'feature', projectId: 'p3' }) });
    expect(__test.pending().has('p3')).toBe(false);
    vi.advanceTimersByTime(1500);
    expect(featuresIndexSpy).not.toHaveBeenCalled();
  });

  it('M1: размонтирование наблюдателя отменяет pending-таймер перезапроса', () => {
    upsertTaskLocal(task({ id: 'epic-7', role: 'feature', projectId: 'p1' }));
    __test.setActive('p1');
    // task_changed поставил дебаунс-таймер перезапроса p1
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'epic-7', role: 'feature', projectId: 'p1' }) });
    expect(__test.pending().has('p1')).toBe(true);
    // Размонтирование: таймер отменён, проект снят из активных
    __test.onUnmount('p1');
    expect(__test.pending().has('p1')).toBe(false);
    expect(__test.active().has('p1')).toBe(false);
    // Таймер мёртв — даже по истечении окна дебаунса запрос не уйдёт
    vi.advanceTimersByTime(1500);
    expect(featuresIndexSpy).not.toHaveBeenCalled();
  });
});

// ── 4: несколько потребителей (R3, минор 5) — хук с эффектами (fakeDom + mount) ──

// Тестовый потребитель: та же подписка, что FeatureBanner/SessionList/ChatCard.
function Consumer(props: { projectId: string | null; enabled: boolean }) {
  useFeaturesIndex(props.projectId, props.enabled);
  return null;
}

// Дождаться, пока асинхронная цепочка fetch → setIndex/emit отработает (fetch —
// уже решённый промис; макрозадача даёт очередь микрозадач дойти до конца).
async function flush() {
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
}

describe('несколько потребителей: выключенный не стирает чужой срез (R3, минор 5)', () => {
  let featuresIndexSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    installFakeDom();
    __test.reset();
    setAllFlags({ [FLAG]: true });
    featuresIndexSpy = vi.spyOn(api.tasks, 'featuresIndex').mockResolvedValue(FIXTURE);
  });
  afterEach(() => {
    featuresIndexSpy.mockRestore();
    __test.reset();
    setAllFlags({});
    uninstallFakeDom();
  });

  it('один enabled=true, другой enabled=false: срез не стирается, данные доходят', async () => {
    const a = mount(createElement(Consumer, { projectId: 'p1', enabled: true }));
    await flush();
    // Включённый потребитель загрузил срез (фикстура — глобальная область, но срез
    // кладётся по projectKey запроса).
    expect(__test.snapshotFor('p1')).toBe(FIXTURE);
    // Выключенный потребитель того же проекта: его монтирование срез не стирает.
    const b = mount(createElement(Consumer, { projectId: 'p1', enabled: false }));
    await flush();
    expect(__test.snapshotFor('p1')).toBe(FIXTURE);
    // Данные доходят: лукап видит чаты фикстуры (сценарий — фасет «Фичи» SessionList
    // при выключенной плашке: dark launch, turn-plan-feature-mode off).
    expect(featureOf('chat-cur').map(e => e.feature.featureId)).toEqual(['feat-closed', 'feat-attention', 'feat-running']);
    // Проект остаётся «активным» (включённый потребитель на месте).
    expect(__test.active().has('p1')).toBe(true);
    // Размонтирование ВЫКЛЮЧЕННОГО потребителя срез тоже не трогает.
    b.root.unmount();
    await flush();
    expect(__test.snapshotFor('p1')).toBe(FIXTURE);
    // Последний включённый ушёл — срез очищен, проект неактивен.
    a.root.unmount();
    await flush();
    expect(__test.snapshotFor('p1').features).toHaveLength(0);
    expect(__test.active().has('p1')).toBe(false);
  });

  it('оба выключены: ничего не грузится и не подписывается (как в Ф1)', async () => {
    const a = mount(createElement(Consumer, { projectId: 'p1', enabled: false }));
    const b = mount(createElement(Consumer, { projectId: 'p1', enabled: false }));
    await flush();
    expect(featuresIndexSpy).not.toHaveBeenCalled();
    expect(__test.snapshotFor('p1').features).toHaveLength(0);
    expect(__test.active().has('p1')).toBe(false);
    a.root.unmount();
    b.root.unmount();
  });
});
