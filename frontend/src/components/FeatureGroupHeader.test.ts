// Заголовок группы фичи (FeatureGroupHeader) — волна 2 (макет features-overview-2.html,
// вариант А: обёртка div → две СОСЕДНИЕ кнопки: шеврон (сворачивает) + тело (страница)
// + быстрое действие) + раскладка её чатов (featureGroupChats) + пункты группировки.
//
// Окружение node (без jsdom): renderToStaticMarkup (SSR) для компонента, прямые вызовы
// чистых функций для логики. Контракт — «Контракт индекса фич — утверждён (Александр,
// 18.10)» (bucket/attention/rail/summary/nowPersonaIds) и таблица токенов
// features-overview-2-tokens.md §1 (плашка А: 55 px, шеврон 26/40, стопка аватаров,
// быстрое действие, ловушки <button>).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { FeatureGroupHeader, featureGroupChevronClick } from './FeatureGroupHeader';
import { featureDisplayState, featureGroupChats, featureGroupByOptions, featureQuickAction, featureAvatarPersonaIds, featureStatusTag } from '../lib/chatGroups';
import type { FeatureAttention, FeatureIndexFeature, FeatureRailSegment, FeatureRailKey } from '../types/featureIndex';
import type { Session, Persona } from '../types';

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

// Минимальная персона: для стопки аватаров (PersonaAvatar требует полный объект).
function persona(id: string, name: string): Persona {
  return {
    id, ownerId: 'o1', name, handle: id,
    scope: 'global',
    avatar: { kind: 'initials' },
    memoryEnabled: false,
  } as Persona;
}

// Рендер заголовка (SSR). `over` — точечные пропсы (onOpen, personaById, isMobile, compact…).
function renderHeader(f: FeatureIndexFeature, over: Record<string, unknown> = {}): string {
  return renderToStaticMarkup(
    createElement(FeatureGroupHeader, {
      feature: f,
      expanded: false,
      onToggle: () => {},
      onOpen: () => {},
      ...over,
    }),
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
    expect(html.includes('border-left-width:1px')).toBe(true);
    expect(html.includes('border-left-color:var(--c-border-light)')).toBe(true);
  });

  it('M2: заголовок группы — шрифт Hanken (не Arial): inline font-family на кнопке', () => {
    const html = renderHeader(feat());
    // M2: нативная <button> не наследует font-family — задаём FONT.sans явно.
    // Первый <button> — шеврон; SSR экранирует кавычки, поэтому ищем по имени шрифта
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

  it('ждёт согласия на мерж (merge, attention): кромка info, статус «ждёт согласия на мерж»', () => {
    const f = feat({ bucket: 'attention', attention: attention('merge') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.alarm).toBe('wait');
    expect(st.status).toBe('ждёт согласия на мерж');
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
});

// === state (бэк Б1a/Б1b): источник правды — f.state, не attention ===
// Мутация «тон по attention.kind вместо state» красит: f без attention, но с state=merge_pending.

describe('FeatureGroupHeader: state (merge_pending / merging)', () => {
  it('merge_pending → wait, кромка info, статус «ждёт согласия на мерж»; тег «К мержу»', () => {
    const f = feat({ bucket: 'attention', state: 'merge_pending', attention: null });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.alarm).toBe('wait');
    expect(st.status).toBe('ждёт согласия на мерж');
    expect(featureStatusTag(f)).toEqual({ label: 'К мержу', tone: 'merge' });
    expect(featureQuickAction(f)).toBe('К мержу');
  });

  it('merging → run (нейтрально, НЕ «ждёт вас»), статус «Мерж идёт»; тег «Идёт»', () => {
    const f = feat({ bucket: 'running', state: 'merging', attention: null });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('run');
    expect(st.alarm).toBeNull();
    expect(st.status).toBe('Мерж идёт');
    expect(featureStatusTag(f)).toEqual({ label: 'Идёт', tone: 'run' });
  });

  it('waiting (не мерж) → wait, «ждёт вас»; тег «Ждёт вас», быстрое «Ответить»', () => {
    const f = feat({ bucket: 'attention', state: 'waiting', attention: null });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.status).toBe('ждёт вас');
    expect(featureStatusTag(f)).toEqual({ label: 'Ждёт вас', tone: 'wait' });
    expect(featureQuickAction(f)).toBe('Ответить');
  });

  it('запасной путь: state нет, attention.kind=merge → «ждёт согласия на мерж» (старый признак)', () => {
    const f = feat({ bucket: 'attention', attention: attention('merge') });
    const st = featureDisplayState(f);
    expect(st.status).toBe('ждёт согласия на мерж');
  });
});

// === Клики: шеврон (сворачивает) vs тело (страница) ===

describe('FeatureGroupHeader: клики (шеvron vs тело)', () => {
  it('клик по шеврону: stopPropagation + onToggle, onOpen НЕ зовёт', () => {
    let toggled = 0, opened = 0, stopped = 0;
    // featureGroupChevronClick — чистая функция: вызываю её с фейковым событием.
    featureGroupChevronClick(
      () => { toggled++; },
      { stopPropagation: () => { stopped++; } },
    );
    expect(toggled).toBe(1);   // onToggle зовёт
    expect(stopped).toBe(1);    // stopPropagation (всплытие не доходит до обёртки/родителя)
    expect(opened).toBe(0);    // onOpen не зовёт (шеvron — не тело)
  });

  it('шеvron и тело — СОСЕДНИЕ кнопки (не вложенные): клик по одному не зовёт обработчик другого', () => {
    const html = renderHeader(feat());
    // Оба test-id присутствуют и на равном уровне (внутри обёртки div, не друг в друге).
    expect(html).toContain('data-testid="feature-group-chevron"');
    expect(html).toContain('data-testid="feature-group-body"');
    // Шеврон — ПЕРЕД телом (порядок в обёртке): его <button> открывается раньше.
    const chev = html.indexOf('data-testid="feature-group-chevron"');
    const body = html.indexOf('data-testid="feature-group-body"');
    expect(chev).toBeGreaterThan(-1);
    expect(body).toBeGreaterThan(chev);
  });

  it('aria: шеврон несёт aria-expanded и aria-label «Развернуть/Свернуть чаты фичи»', () => {
    const collapsed = renderHeader(feat());
    expect(collapsed).toContain('aria-expanded="false"');
    expect(collapsed).toContain('Развернуть чаты фичи');
    const expanded = renderHeader(feat(), { expanded: true });
    expect(expanded).toContain('aria-expanded="true"');
    expect(expanded).toContain('Свернуть чаты фичи');
  });
});

// === Быстрое действие (featureQuickAction) ===

describe('featureQuickAction: «Ответить» / «К мержу» / «Ключевой чат»', () => {
  it('ждёт вас (wait) → «Ответить»', () => {
    expect(featureQuickAction(feat({ bucket: 'attention', attention: attention('wait') }))).toBe('Ответить');
  });

  it('к мержу (merge) → «К мержу»', () => {
    expect(featureQuickAction(feat({ bucket: 'attention', attention: attention('merge') }))).toBe('К мержу');
  });

  it('идёт (running, есть ключевой чат) → «Ключевой чат»', () => {
    expect(featureQuickAction(feat({ bucket: 'running', keySessionId: 'key' }))).toBe('Ключевой чат');
  });

  it('идёт, но ключевого чата нет → null (действия нет)', () => {
    expect(featureQuickAction(feat({ bucket: 'running', keySessionId: null }))).toBeNull();
  });

  it('закрытая (chip closed) → null (действие живёт на странице)', () => {
    expect(featureQuickAction(feat({ bucket: 'closed', chips: ['closed'], keySessionId: 'key' }))).toBeNull();
  });

  it('рендер: быстрое действие в DOM с верной подписью (десктоп)', () => {
    const html = renderHeader(feat({ bucket: 'attention', attention: attention('wait') }));
    expect(html).toContain('data-testid="feature-group-quick"');
    expect(html).toContain('Ответить');
  });

  it('M1: быстрое действие несёт aria-label «Ответить: <название фичи>» (контекст для скринридера)', () => {
    const html = renderHeader(feat({ bucket: 'attention', attention: attention('wait'), title: 'Моя фича' }));
    // aria-label на самой кнопке (ui/Button): «Ответить: Моя фича» — не только «Ответить»
    expect(html).toContain('aria-label="Ответить: Моя фича"');
  });

  it('рендер: на 360 (isMobile) быстрое действие не рендерится', () => {
    const html = renderHeader(feat({ bucket: 'attention', attention: attention('wait') }), { isMobile: true });
    expect(html).not.toContain('data-testid="feature-group-quick"');
  });
});

// === Стопка аватаров (featureAvatarPersonaIds) ===

describe('featureAvatarPersonaIds: nowPersonaIds с фолбэком на now0', () => {
  it('nowPersonaIds непустой → из него (в порядке убывания старта)', () => {
    const f = feat({ nowPersonaIds: ['p1', 'p2'] });
    expect(featureAvatarPersonaIds(f)).toEqual(['p1', 'p2']);
  });

  it('nowPersonaIds пуст → фолбэк на now0.personaId', () => {
    const f = feat({ nowPersonaIds: [], now0: { personaId: 'p9', stage: 'prod', stepRef: 'Ш3' } });
    expect(featureAvatarPersonaIds(f)).toEqual(['p9']);
  });

  it('nowPersonaIds отсутствует (старый ответ) → фолбэк на now0.personaId', () => {
    const f = feat({ now0: { personaId: 'p9', stage: 'prod', stepRef: 'Ш3' } });
    expect(featureAvatarPersonaIds(f)).toEqual(['p9']);
  });

  it('ни nowPersonaIds, ни now0 → пусто (аватаров нет)', () => {
    expect(featureAvatarPersonaIds(feat())).toEqual([]);
  });

  it('рендер: пустой nowPersonaIds → аватар из now0 (PersonaAvatar в DOM)', () => {
    const f = feat({ nowPersonaIds: [], now0: { personaId: 'p9', stage: 'prod', stepRef: 'Ш3' } });
    const html = renderHeader(f, { personaById: (id: string) => (id === 'p9' ? persona('p9', 'Кира') : undefined) });
    // Аватар персоны p9 (инициалы) рендерится; PersonaFace рисует инициалы имени («Кира» → «КИ»).
    expect(html).toContain('КИ');
  });

  it('рендер: nowPersonaIds → стопка (несколько аватаров, нахлёст −5 со второго)', () => {
    const f = feat({ nowPersonaIds: ['p1', 'p2'] });
    const html = renderHeader(f, {
      personaById: (id: string) => (id === 'p1' ? persona('p1', 'Денис') : id === 'p2' ? persona('p2', 'Кира') : undefined),
    });
    // Оба аватара в DOM (инициалы «Денис» → «ДЕ», «Кира» → «КИ»).
    expect(html).toContain('ДЕ');
    expect(html).toContain('КИ');
    // Нахлёст −5px со второго аватара (макет: стопка).
    expect(html).toContain('margin-left:-5px');
  });
});

// === Раскладка чатов группы (featureGroupChats) ===

describe('featureGroupChats: ключевой первым и «ещё N»', () => {
  it('ключевой чат — первым в ordered и в exposed', () => {
    const f = feat({ keySessionId: 'key' });
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

// === m2: поля тела плашки по состоянию (таблица §2) ===
// Обычная — 7 10 7 2 (55 px); закрытая (compact) — 5 10 5 2 (≈50,5), на 360 minHeight 52.
// До фикса тело закрытой плашки шло с полями 7 10 7 2 и высотой 55, как открытая.
describe('FeatureGroupHeader: m2 поля тела по состоянию', () => {
  it('обычная плашка: поля 7 10 7 2 (55 px)', () => {
    const html = renderHeader(feat());
    expect(html).toContain('padding:7px 10px 7px 2px');
  });

  it('закрытая (compact): поля 5 10 5 2 (≈50,5, таблица §2)', () => {
    const html = renderHeader(feat({ bucket: 'closed', summary: null }), { compact: true });
    expect(html).toContain('padding:5px 10px 5px 2px');
  });

  it('закрытая на 360 (compact + isMobile): minHeight 52', () => {
    const html = renderHeader(feat({ bucket: 'closed', summary: null }), { compact: true, isMobile: true });
    expect(html).toContain('min-height:52px');
  });

  it('обычная на 360: minHeight 52 нет (только у закрытой)', () => {
    const html = renderHeader(feat(), { isMobile: true });
    expect(html).not.toContain('min-height:52px');
  });
});

// === Ф-pr: мёртвый фолбэк «N/M» убран (один формат «принято N из T») ===
// Бэк всегда даёт данные (stepProgress non-nullable, новые поля индекса безусловные),
// поэтому ветка «${stepsAccepted}/${stepsTotal} ↺loops» недостижима; а голое «N/M»
// (без слов) запрещено ТЗ. Убрана → progress-нода либо «принято N из T», либо пуста.
describe('FeatureGroupHeader: Ф-pr — мёртвый фолбэк «N/M» убран', () => {
  it('старый ответ (no new fields) → progress-нода БЕЗ «N/M» (голое «3/5» не возвращается)', () => {
    // stepsAccepted/stepsTotal есть, новых полей (stepsReview/stepsWork/stepsNotCreated/planState)
    // нет → stepProgressFromIndex = null. Мёртвый фолбэк «3/5» (убран) не должен возвращаться.
    const f = feat({ stepsAccepted: 3, stepsTotal: 5 });
    const html = renderHeader(f);
    const m = html.match(/<span data-testid="feature-group-progress"[^>]*>([\s\S]*?)<\/span>/);
    expect(m, 'progress-нода не найдена').not.toBeNull();
    expect(m![1], 'текст прогресса не должен содержать «N/M»').not.toMatch(/\d+\s*\/\s*\d+/);
  });

  it('новые поля есть → progress-нода = «принято N из T» (форматтер, не «N/M»)', () => {
    const f = feat({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 2, planState: 'partial' });
    const html = renderHeader(f);
    const m = html.match(/<span data-testid="feature-group-progress"[^>]*>([\s\S]*?)<\/span>/);
    expect(m, 'progress-нода не найдена').not.toBeNull();
    // Форматтер: «принято 3 из 5 · 2 в ревью · план частично» — без голого «3/5».
    expect(m![1]).toContain('принято 3 из 5');
    expect(m![1]).not.toMatch(/\d+\s*\/\s*\d+/);
  });
});
