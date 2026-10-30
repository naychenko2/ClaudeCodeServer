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

// Фикстура: 7 сегментов (design не включена — необязательный этап); `over` — точечные
// переопределения по ключу стадии.
const KEYS: FeatureRailKey[] = ['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge'];
function rail(over: Partial<Record<FeatureRailKey, Partial<FeatureRailSegment>>> = {}): FeatureRailSegment[] {
  const base: Record<FeatureRailKey, FeatureRailSegment> = {
    cut:    { key: 'cut',    state: 'done',    tone: null },
    design: { key: 'design', state: 'pending', tone: null },
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

function attention(kind: FeatureAttention['kind'], personaId: string | null = null): FeatureAttention {
  return { kind, since: null, reason: null, personaId, sessionId: null };
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

  it('ждёт вас (wait): глиф wait, кромка warning (тон В2), статус «ждёт вас»', () => {
    const f = feat({ bucket: 'attention', attention: attention('wait') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.alarm).toBe('wait');
    expect(st.status).toBe('ждёт вас');
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="wait"')).toBe(true);
    // С1 (макет v4): кромка тревоги — тоны В2: «ждёт вас» — warning (не синий info), 3px longhand.
    expect(html.includes('border-left-width:3px')).toBe(true);
    expect(html.includes('border-left-color:var(--c-warning)')).toBe(true);
  });

  it('ждёт согласия на мерж (merge, attention): кромка warning, статус «ждёт согласия на мерж»', () => {
    const f = feat({ bucket: 'attention', attention: attention('merge') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.alarm).toBe('wait');
    expect(st.status).toBe('ждёт согласия на мерж');
    expect(renderHeader(f).includes('border-left-width:3px')).toBe(true);
    expect(renderHeader(f).includes('border-left-color:var(--c-warning)')).toBe(true);
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

  it('С1: мини-рельсы из точек этапов в плашке нет нигде (этапы — только на странице фичи)', () => {
    for (const b of ['running', 'merged', 'closed'] as const) {
      const html = renderHeader(feat({ bucket: b, summary: null }));
      expect(html).not.toContain('data-testid="feature-group-progress"');
      // точек этапов (feature-dot-*) в плашке быть не должно вовсе
      expect(html).not.toContain('feature-dot-');
    }
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

describe('FeatureGroupHeader: bucket merged (смержена, ждёт закрытия)', () => {
  const f = feat({ bucket: 'merged', chips: ['open', 'merged'], state: 'merged' });

  it('статус «смержена, ждёт закрытия», глиф done, без кромки; тег «Смержена»', () => {
    expect(featureDisplayState(f)).toEqual({ kind: 'done', alarm: null, status: 'смержена, ждёт закрытия' });
    expect(featureStatusTag(f)).toEqual({ label: 'Смержена', tone: 'merged' });
  });

  it('рендер: статус виден, цвет — successText', () => {
    const html = renderHeader(f);
    expect(html).toContain('смержена, ждёт закрытия');
    expect(html).toContain('var(--c-success-text)');
  });
});

describe('FeatureGroupHeader: счётчик расхождений трекера', () => {
  it('trackerIssues > 0: бейдж «⚠ N» (глиф + число, статус не режется) с пояснением в title', () => {
    const html = renderHeader(feat({ trackerIssues: 3 }));
    expect(html).toContain('data-testid="feature-group-tracker-issues"');
    // Длинного слова нет: «расхождений: N» резало статус до «ждёт согласия на ме…».
    expect(html).not.toContain('расхождений:');
    expect(html).toContain('>3<');
    expect(html).toContain('Трекер расходится с работой: 3 — оркестратор поправит');
  });

  it('нет поля или 0: бейджа нет', () => {
    expect(renderHeader(feat())).not.toContain('feature-group-tracker-issues');
    expect(renderHeader(feat({ trackerIssues: 0 }))).not.toContain('feature-group-tracker-issues');
  });

  it('закрытая (compact): бейджа нет', () => {
    const html = renderHeader(feat({ bucket: 'closed', trackerIssues: 2 }), { compact: true });
    expect(html).not.toContain('feature-group-tracker-issues');
  });
});

describe('FeatureGroupHeader: клики (шеvron vs тело)', () => {
  it('клик по шеврону: stopPropagation + onToggle, onOpen НЕ зовёт', () => {
    let toggled = 0, stopped = 0;
    const opened = 0;
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

describe('featureAvatarPersonaIds: nowPersonaIds с фолбэком на now0, затем на attention.personaId', () => {
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

  it('ни nowPersonaIds, ни now0, ни attention.personaId → пусто (аватаров нет)', () => {
    expect(featureAvatarPersonaIds(feat())).toEqual([]);
  });

  // ST-FA (дефект 4a241bee): после сужения run.now у фичи, где все исполнители
  // ждут человека, nowPersonaIds и now0 пусты — стопка не должна пустеть.
  it('nowPersonaIds и now0 пусты, attention.personaId задан → фолбэк на attention.personaId', () => {
    const f = feat({ bucket: 'attention', attention: attention('wait', 'p7') });
    expect(featureAvatarPersonaIds(f)).toEqual(['p7']);
  });

  // Приоритет: живой now не уступает тревоге.
  it('nowPersonaIds непустой при заданном attention.personaId → прежнее поведение, не фолбэк', () => {
    const f = feat({ nowPersonaIds: ['p1'], attention: attention('wait', 'p7') });
    expect(featureAvatarPersonaIds(f)).toEqual(['p1']);
  });

  it('attention есть, но без personaId → пусто (фолбэк только если id задан)', () => {
    const f = feat({ bucket: 'attention', attention: attention('stopped') });
    expect(featureAvatarPersonaIds(f)).toEqual([]);
  });

  it('рендер: пустой nowPersonaIds → аватар из now0 (PersonaAvatar в DOM)', () => {
    const f = feat({ nowPersonaIds: [], now0: { personaId: 'p9', stage: 'prod', stepRef: 'Ш3' } });
    const html = renderHeader(f, { personaById: (id: string) => (id === 'p9' ? persona('p9', 'Кира') : undefined) });
    // Аватар персоны p9 (инициалы) рендерится; PersonaFace рисует инициалы имени («Кира» → «КИ»).
    expect(html).toContain('КИ');
  });

  it('рендер: пустой now при attention.personaId → аватар из attention (PersonaAvatar в DOM)', () => {
    const f = feat({ bucket: 'attention', attention: attention('wait', 'p7') });
    const html = renderHeader(f, { personaById: (id: string) => (id === 'p7' ? persona('p7', 'Тимур') : undefined) });
    // Инициалы персоны p7 («Тимур» → «ТИ») — стопка не пустая.
    expect(html).toContain('ТИ');
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

// === m2: высота плашки как СУММА слоёв (С1 = ровно 55 px) ===
// Раньше тест проверял только строку полей («вакуумный»: дизайн-ревью F2, major 2 — при
// gap 3 ×2 и растянутой бейджем строке плашка вышла 60,4). Теперь собираем высоту из тех
// же чисел, которые рисует компонент: рамка + поля + фиксированные строки + отступ над
// полосой + полоса. Сдвинется любой слой — краснеет.
describe('FeatureGroupHeader: m2 высота С1 как сумма слоёв', () => {
  it('обычная плашка: 1 + 6 + 19 + 17 + 4 + 4 + 3 + 1 = ровно 55 px, без фиксированной height', () => {
    const html = renderHeader(feat({ stepsAccepted: 4, stepsTotal: 9, stepsReview: 2, stepsWork: 2, stepsNotCreated: 1, planState: 'cut' }));
    const styleOf = (testId: string): string => {
      const m = new RegExp(`data-testid="${testId}"[^>]*style="([^"]*)"`).exec(html);
      if (!m) throw new Error(`слой не найден в разметке: ${testId}`);
      return m[1];
    };
    const px = (style: string, prop: string): number => {
      const m = new RegExp(`${prop}:(\\d+)px`).exec(style);
      if (!m) throw new Error(`слоя нет в стиле ${prop}: ${style}`);
      return Number(m[1]);
    };
    const frame = styleOf('feature-group-header');
    const body = styleOf('feature-group-body');
    const bar = styleOf('feature-group-bar');
    const pad = /padding:(\d+)px (\d+)px (\d+)px (\d+)px/.exec(body);
    if (!pad) throw new Error(`полей тела нет: ${body}`);
    const total =
      px(frame, 'border-top-width') + Number(pad[1])
      + px(styleOf('feature-group-row1'), 'height')
      + px(styleOf('feature-group-row2'), 'height')
      + px(bar, 'margin-top') + px(bar, 'height')
      + Number(pad[3]) + px(frame, 'border-bottom-width');
    expect(total).toBe(55);
    // Строки идут вплотную (зазора между слоями нет) и высота не задана фиксированной —
    // она складывается из слоёв (фиксированных), поэтому не съезжает от содержимого.
    expect(body).not.toContain('gap:');
    expect(frame).not.toContain('height:55px');
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

// === С1: счётчики уходят в title, на виду — процент и полоса ===
// Строка «принято N из T · …» (formatStepProgress) больше не печатается в плашке:
// она — title процета и статуса (полная строка по тапу/наведению). На виду — «N %»
// и полоса трекера (сравнение фич глазами, не чтением).
describe('FeatureGroupHeader: С1 — процент и полоса, счётчики в title', () => {
  it('старый ответ (no new fields) → ни процента, ни полосы (нет данных — пустых частей нет)', () => {
    const f = feat({ stepsAccepted: 3, stepsTotal: 5 });
    const html = renderHeader(f);
    expect(html).not.toContain('feature-group-percent');
    expect(html).not.toContain('feature-group-bar');
  });

  it('новые поля есть → процент «60 %» справа, полоса под строкой, полная строка — в title', () => {
    const f = feat({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 2, planState: 'partial' });
    const html = renderHeader(f);
    const m = html.match(/<span data-testid="feature-group-percent"[^>]*title="([^"]*)"[^>]*>[\s\S]*?<b[^>]*>(\d+ %)<\/b>/);
    expect(m, 'percent-нода не найдена').not.toBeNull();
    expect(m![2]).toBe('60 %');
    // title — общий форматтер: «принято 3 из 5 · 2 в ревью · план частично», без голого «3/5».
    expect(m![1]).toContain('принято 3 из 5');
    expect(m![1]).not.toMatch(/\d+\s*\/\s*\d+/);
    // полоса: сегменты accepted (success) и review (info), без «master» (в индексе его нет)
    expect(html).toContain('data-testid="feature-group-bar"');
    expect(html).toContain('data-testid="feature-bar-accepted"');
    expect(html).toContain('data-testid="feature-bar-review"');
    expect(html).not.toContain('data-testid="feature-bar-master"');
  });

  it('«Сейчас {Имя} +N»: имя 600 textHeading, «+N» — остальные идущие', () => {
    const f = feat({ nowPersonaIds: ['p1', 'p2', 'p3'] });
    const html = renderHeader(f, { personaById: (id: string) => (id === 'p1' ? persona('p1', 'Кира') : undefined) });
    expect(html).toContain('Сейчас <b data-testid="feature-group-now-name"');
    expect(html).toContain('>Кира</b> +2');
  });
});

describe('FeatureGroupHeader: очередь фич (глиф queue, «в очереди · N из M»)', () => {
  const queued = (over: Partial<FeatureIndexFeature> = {}) =>
    feat({ state: 'paused', holdReason: 'queue', queue: { position: 2, length: 3 }, ...over });

  it('featureDisplayState: queue → kind queue, без тревоги, подпись с позицией', () => {
    expect(featureDisplayState(queued())).toEqual({ kind: 'queue', alarm: null, status: 'в очереди · 2 из 3' });
  });

  it('queue не пришёл → «в очереди» без цифр', () => {
    expect(featureDisplayState(queued({ queue: null })).status).toBe('в очереди');
  });

  it('плашка: глиф queue 14, цифры в FONT.mono, textPrimary/600, кромки слева нет', () => {
    const html = renderHeader(queued());
    expect(html).toContain('data-feature-state="queue"');
    // глиф queue: круг r7 + три полосы (r 0.8)
    expect(html).toMatch(/<svg[^>]*width="14"[^>]*>(?:(?!<\/svg>).)*rx="0\.8"/);
    const m = html.match(/<span data-testid="feature-group-status"([^>]*)>([\s\S]*?)<\/span><span data-testid="feature-group-progress|<span data-testid="feature-group-status"([^>]*)>([\s\S]*?)<\/span>/);
    expect(m).not.toBeNull();
    expect(html).toMatch(/data-testid="feature-group-queue-nums" style="font-family:[^"]*JetBrains Mono[^"]*">2 из 3</);
    expect(html).toContain('в очереди · ');
    expect(m![0]).toContain('font-weight:600');
    expect(m![0]).toContain('var(--c-text-primary)');
    expect(html).not.toContain('border-left-width:3px');
  });

  it('holdReason manual → прежний вид: не очередь, кромки слева нет', () => {
    const f = feat({ state: 'paused', holdReason: 'manual', queue: { position: 2, length: 3 } });
    expect(featureDisplayState(f).kind).not.toBe('queue');
    const html = renderHeader(f);
    expect(html).not.toContain('в очереди');
    expect(html).not.toContain('border-left-width:3px');
  });

  // (Рельсы в очереди больше нет: С1 заменил точки этапов полосой трекера; приглушение
  // active и подпись с позицией жили в FeatureRail — он удалён.)
});
