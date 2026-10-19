// Заголовок группы фичи (FeatureGroupHeader) + раскладка её чатов (featureGroupChats)
// + пункты группировки (featureGroupByOptions).
//
// Окружение node (без jsdom): renderToStaticMarkup (SSR) для компонента, прямые
// вызовы чистых функций для раскладки. Контракт — «Контракт индекса фич —
// утверждён (Александр, 18.10)» (bucket/attention/rail/summary) и макет
// features-overview.html (.fgh: шеврон, глиф, название, счётчик, мини-рельса,
// статус, кромка тревоги 3px у «ждёт вас»/«упала»).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { FeatureGroupHeader } from './FeatureGroupHeader';
import { featureDisplayState, featureGroupChats, featureGroupByOptions } from '../lib/chatGroups';
import type { FeatureAttention, FeatureIndexFeature, FeatureRailSegment, FeatureRailKey } from '../types/featureIndex';
import type { Session } from '../types';

// === Фикстуры ===

// 7 стадий в порядке жизненного цикла (cut → … → merge); `over` — точечные
// переопределения по ключу стадии.
const KEYS: FeatureRailKey[] = ['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge'];
function rail(over: Partial<Record<FeatureRailKey, Partial<FeatureRailSegment>>> = {}): FeatureRailSegment[] {
  const base: Record<FeatureRailKey, FeatureRailSegment> = {
    cut:    { key: 'cut',    state: 'done',    tone: null },
    plan:   { key: 'plan',   state: 'done',    tone: null },
    prod:   { key: 'prod',   state: 'active',  tone: 'run' },
    review: { key: 'review', state: 'pending', tone: null },
    qa:     { key: 'qa',     state: 'pending', tone: null },
    final:  { key: 'final',  state: 'pending', tone: null },
    merge:  { key: 'merge',  state: 'pending', tone: null },
  };
  return KEYS.map(k => ({ ...base[k], ...over[k] }));
}

// Минимальная строка фичи: по умолчанию «идёт» (prod active run), без тревоги.
function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Фича', projectId: 'p1',
    bucket: 'running', attention: null,
    rail: rail(),
    chips: ['open'],
    stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  };
}

function attention(kind: FeatureAttention['kind']): FeatureAttention {
  return { kind, since: null, reason: null, personaId: null, sessionId: null };
}

// Минимальный чат: для раскладки важны id, status, parentSessionId.
function chat(id: string, over: Partial<Session> = {}): Session {
  return {
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual', ...over,
  } as Session;
}

function renderHeader(f: FeatureIndexFeature, count = 3, expanded = false): string {
  return renderToStaticMarkup(
    createElement(FeatureGroupHeader, { feature: f, count, expanded, onToggle: () => {} }),
  );
}

// === Тона (featureDisplayState + рендер заголовка) ===

describe('FeatureGroupHeader: тона', () => {
  it('идёт (run): глиф run, статус «{этап} · идёт», кромки нет', () => {
    const f = feat({ bucket: 'running' }); // prod active run
    const st = featureDisplayState(f);
    expect(st.kind).toBe('run');
    expect(st.alarm).toBeNull();
    expect(st.status).toBe('Производство · идёт');
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="run"')).toBe(true);
    expect(html.includes('Производство · идёт')).toBe(true);
    // M1: без тревоги левая сторона — 1px (longhand), НЕ 3px и НЕ обнулённая (UA-outset).
    // Мутация: вернуть `borderLeft: undefined` поверх шорткода → border-left-width пропадает.
    expect(html.includes('border-left-width:1px')).toBe(true);
    expect(html.includes('border-left-color:var(--c-border-light)')).toBe(true);
  });

  it('M2: заголовок группы — шрифт Hanken (не Arial): inline font-family на кнопке', () => {
    const html = renderHeader(feat());
    // M2: нативная <button> не наследует font-family — задаём FONT.sans явно.
    // SSR экранирует кавычки (font-family:&#x27;Hanken Grotesk…), поэтому ищем по имени шрифта
    // в открывающем теге кнопки (у внутренних span собственного font-family нет).
    const i = html.indexOf('<button');
    const tag = html.slice(i, html.indexOf('>', i) + 1);
    expect(tag).toContain('font-family:');
    expect(tag).toContain('Hanken Grotesk');
  });

  it('доработка (loop): глиф loop, статус «{этап} · доработка», кромки нет', () => {
    const f = feat({ bucket: 'running', rail: rail({ prod: { state: 'active', tone: 'loop' } }) });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('loop');
    expect(st.alarm).toBeNull();
    expect(st.status).toBe('Производство · доработка');
    expect(renderHeader(f).includes('data-feature-state="loop"')).toBe(true);
  });

  it('ждёт вас (wait): глиф wait, кромка info, статус «ждёт вас»', () => {
    const f = feat({ bucket: 'attention', attention: attention('wait') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.alarm).toBe('wait');
    expect(st.status).toBe('ждёт вас');
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="wait"')).toBe(true);
    // M1: кромка тревоги — longhand: левая 3px + цвет info
    expect(html.includes('border-left-width:3px')).toBe(true);
    expect(html.includes('border-left-color:var(--c-info)')).toBe(true);
  });

  it('ждёт вас · мерж (merge): кромка info, статус «ждёт вас · мерж»', () => {
    const f = feat({ bucket: 'attention', attention: attention('merge') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.alarm).toBe('wait');
    expect(st.status).toBe('ждёт вас · мерж');
    // M1: кромка тревоги — longhand: левая 3px + цвет info
    expect(renderHeader(f).includes('border-left-width:3px')).toBe(true);
    expect(renderHeader(f).includes('border-left-color:var(--c-info)')).toBe(true);
  });

  it('упала (stopped): глиф stopped, кромка danger, статус «стоит»', () => {
    const f = feat({ bucket: 'attention', attention: attention('stopped') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('stopped');
    expect(st.alarm).toBe('stop');
    expect(st.status).toBe('стоит');
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="stopped"')).toBe(true);
    // M1: кромка тревоги — longhand: левая 3px + цвет danger
    expect(html.includes('border-left-width:3px')).toBe(true);
    expect(html.includes('border-left-color:var(--c-danger)')).toBe(true);
  });

  it('закрыта (closed, без мержа): глиф closed, статус «закрыта», кромки нет', () => {
    const f = feat({ bucket: 'closed', summary: null });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('closed');
    expect(st.alarm).toBeNull();
    expect(st.status).toBe('закрыта');
    expect(renderHeader(f).includes('data-feature-state="closed"')).toBe(true);
  });

  it('смержена (closed + mergedAt): глиф done (галочка), статус «смержена»', () => {
    const f = feat({ bucket: 'closed', summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 } });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('done');
    expect(st.alarm).toBeNull();
    expect(st.status).toBe('смержена');
    expect(renderHeader(f).includes('data-feature-state="done"')).toBe(true);
  });

  it('счётчик и название группы в заголовке', () => {
    const f = feat({ title: 'Моя фича' });
    const html = renderHeader(f, 7);
    expect(html.includes('Моя фича')).toBe(true);
    // Счётчик — CountBadge со значением 7
    expect(html).toContain('7');
  });
});

// === Раскладка чатов группы (featureGroupChats) ===

describe('featureGroupChats: ключевой первым и «ещё N»', () => {
  it('ключевой чат — первым в ordered и в exposed', () => {
    const f = feat({ keySessionId: 'key' });
    // key — finished (не живой), остальные — finished; порядок ввода намерено не
    // начинается с key, чтобы проверить, что именно ключевой вытаскивается вперёд.
    const sessions = [chat('b'), chat('key'), chat('c')];
    const { ordered, exposed } = featureGroupChats(f, sessions);
    expect(ordered[0].id).toBe('key');
    expect(exposed[0].id).toBe('key');
  });

  it('наружу — ключевой + живые + вставшие; «ещё N» = остаток', () => {
    const f = feat({ keySessionId: 'key' });
    const sessions = [
      chat('key'),
      chat('live', { status: 'working' }),
      chat('stuck', { status: 'error' }),
      chat('hidden1'),
      chat('hidden2'),
    ];
    const { exposed, hiddenCount } = featureGroupChats(f, sessions);
    // Ключевой первым, затем живые/вставшие; finished-чаты (hidden1/2) — не наружу.
    expect(exposed.map(c => c.id)).toEqual(['key', 'live', 'stuck']);
    expect(hiddenCount).toBe(2);
  });

  it('без ключевого: наружу только живые/вставшие, «ещё N» — остальные', () => {
    const f = feat({ keySessionId: null });
    const sessions = [
      chat('live', { status: 'working' }),
      chat('hidden'),
    ];
    const { exposed, hiddenCount } = featureGroupChats(f, sessions);
    expect(exposed.map(c => c.id)).toEqual(['live']);
    expect(hiddenCount).toBe(1);
  });

  it('все наружу: «ещё N» = 0', () => {
    const f = feat({ keySessionId: 'key' });
    const sessions = [chat('key'), chat('live', { status: 'working' })];
    const { hiddenCount } = featureGroupChats(f, sessions);
    expect(hiddenCount).toBe(0);
  });
});

// === Пункты группировки (featureGroupByOptions) ===

describe('featureGroupByOptions: пункт «Фичи»', () => {
  it('в списке проекта с включённым флагом — «features» на месте', () => {
    expect(featureGroupByOptions(true, true)).toEqual(['days', 'tags', 'subjects', 'features', 'none']);
  });

  it('флаг выключен — пункта «Фичи» нет (даже в проекте)', () => {
    expect(featureGroupByOptions(true, false)).toEqual(['days', 'tags', 'subjects', 'none']);
  });

  it('вне проекта — «Фичи» нет (реестра фич нет), даже с флагом', () => {
    expect(featureGroupByOptions(false, true)).toEqual(['days', 'none']);
    expect(featureGroupByOptions(false, false)).toEqual(['days', 'none']);
  });
});
