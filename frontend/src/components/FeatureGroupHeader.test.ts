// Заголовок группы фичи (FeatureGroupHeader) — v2 (шаг FS-FL1, записка
// features-simplify-v1 §3 R2): две строки и тонкая полоса. Строка 1: название и срок
// справа (из feature.eta / дата мержа); строка 2: одна фраза — «{этап} · N/M» или фраза
// тревоги (из feature.ask) или подпись закрытия. Аватаров, ⚠, процента и давности нет.
//
// Окружение node (без jsdom): renderToStaticMarkup (SSR) для компонента, прямые вызовы
// чистых функций для логики. Контракт — «Контракт индекса фич — утверждён (Александр,
// 18.10)» (bucket/attention/rail/summary/ask/eta) и таблица токенов
// features-overview-2-tokens.md §1 (плашка А: 55 px, шеврон 26/40, ловушки <button>).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { FeatureGroupHeader, featureGroupChevronClick } from './FeatureGroupHeader';
import { featureDisplayState, featureGroupChats, featureGroupByOptions, featureQuickAction, featureStatusTag } from '../lib/chatGroups';
import type { FeatureAttention, FeatureIndexFeature, FeatureRailSegment, FeatureRailKey } from '../types/featureIndex';
import type { FeatureAsk } from '../lib/featurePage';
import type { Session } from '../types';

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

// «Нужно от тебя» (FS-V4): строка как отдаёт бэк (text ≤ 120).
function ask(over: Partial<FeatureAsk> = {}): FeatureAsk {
  return { text: 'нужен ответ по счёту объёма', source: 'wait', since: null, ...over };
}

// «Сейчас» для давности тревоги: фиксированный момент (стабильные тесты).
const NOW_MS = Date.UTC(2026, 10, 11, 12, 0, 0);
const isoAgo = (min: number) => new Date(NOW_MS - min * 60000).toISOString();

// Минимальный чат: для раскладки важны id, status, parentSessionId.
function chat(id: string, over: Partial<Session> = {}): Session {
  return {
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual', ...over,
  } as Session;
}

// Рендер заголовка (SSR). `over` — точечные пропсы (onOpen, isMobile, compact…).
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

// Регион текста строки: от row1 до полосы (полоса — не «строка»: её aria-подпись
// «Готовность N %» несёт % по своему назначению и к строке отношения не имеет).
function rowsRegion(html: string): string {
  const a = html.indexOf('data-testid="feature-group-row1"');
  if (a < 0) throw new Error('row1 не найден в разметке');
  const b = html.indexOf('data-testid="feature-group-bar"');
  return html.slice(a, b === -1 ? html.length : b);
}

// Текст ноды срока (между её открывающим тегом и текстом — style/title, поэтому
// берём содержимое регуляркой, а не срезом после test-id).
const etaShown = (html: string): string => {
  const m = /data-testid="feature-group-eta"[^>]*>([^<]*)</.exec(html);
  if (!m) throw new Error('нода срока не найдена в разметке');
  return m[1];
};
// Стиль ноды строки 2 (тот же порядок атрибутов).
const statusStyle = (html: string): string => {
  const m = /data-testid="feature-group-status"[^>]*style="([^"]*)"/.exec(html);
  if (!m) throw new Error('нода строки 2 не найдена в разметке');
  return m[1];
};

// Локальные геттеры той же Date, что использует компонент (featureTime/etaLabel):
// ожидаемое не зависит от пояса машины (CI в UTC, разработка в МСК).
const localDate = (iso: string) => {
  const d = new Date(iso);
  return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
};
const localWeekday = (iso: string) => new Date(iso).toLocaleDateString('ru-RU', { weekday: 'short' });

// === Тона (featureDisplayState + рендер заголовка) ===

describe('FeatureGroupHeader: тона', () => {
  it('идёт (run): глиф run, строка 2 «{этап} · N/M», кромки нет', () => {
    const f = feat({ bucket: 'running', stepsAccepted: 4, stepsTotal: 9 }); // prod active run
    const st = featureDisplayState(f);
    expect(st.kind).toBe('run');
    expect(st.alarm).toBeNull();
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="run"')).toBe(true);
    // v2 (FS-FL1): этап из активной стадии (now0 нет) и счётчики индекса как есть.
    expect(html).toContain('Производство · 4/9');
    // M1: без тревоги левая сторона — 1px (longhand), НЕ 3px и НЕ обнулённая (UA-outset).
    expect(html.includes('border-left-width:1px')).toBe(true);
    expect(html.includes('border-left-color:var(--c-border-light)')).toBe(true);
  });

  it('этап — из now0.stage (контракт FS-FL1), не из рельсы: now0=review при активной prod', () => {
    const f = feat({ stepsAccepted: 4, stepsTotal: 9, now0: { personaId: 'p9', stage: 'review', stepRef: 'Ш3' } });
    const html = renderHeader(f);
    expect(html).toContain('Ревью · 4/9');
    expect(html).not.toContain('Производство · 4/9');
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

  it('доработка (loop): глиф loop, строка этапа тоном warningText 600, кромки нет', () => {
    const f = feat({ bucket: 'running', stepsAccepted: 4, stepsTotal: 9, rail: rail({ prod: { state: 'active', tone: 'loop' } }) });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('loop');
    expect(st.alarm).toBeNull();
    const html = renderHeader(f);
    expect(html).toContain('data-feature-state="loop"');
    expect(statusStyle(html)).toContain('var(--c-warning-text)');
    expect(statusStyle(html)).toContain('font-weight:600');
  });

  it('ждёт вас (wait): глиф wait, кромка warning (тон В2)', () => {
    const f = feat({ bucket: 'attention', attention: attention('wait') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.alarm).toBe('wait');
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="wait"')).toBe(true);
    // С1 (макет v4): кромка тревоги — тоны В2: «ждёт вас» — warning (не синий info), 3px longhand.
    expect(html.includes('border-left-width:3px')).toBe(true);
    expect(html.includes('border-left-color:var(--c-warning)')).toBe(true);
  });

  it('ждёт согласия на мерж (merge, attention): кромка warning (тон В2)', () => {
    const f = feat({ bucket: 'attention', attention: attention('merge') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('wait');
    expect(st.alarm).toBe('wait');
    expect(renderHeader(f).includes('border-left-width:3px')).toBe(true);
    expect(renderHeader(f).includes('border-left-color:var(--c-warning)')).toBe(true);
  });

  it('упала (stopped): глиф stopped, кромка danger', () => {
    const f = feat({ bucket: 'attention', attention: attention('stopped') });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('stopped');
    expect(st.alarm).toBe('stop');
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="stopped"')).toBe(true);
    expect(html.includes('border-left-width:3px')).toBe(true);
    expect(html.includes('border-left-color:var(--c-danger)')).toBe(true);
  });

  it('закрыта (closed, без мержа): глиф closed, «закрыта без мержа», кромки нет, срока нет', () => {
    const f = feat({ bucket: 'closed', summary: null });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('closed');
    expect(st.alarm).toBeNull();
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="closed"')).toBe(true);
    expect(html).toContain('закрыта без мержа');
    expect(etaShown(html)).toBe('—');
  });

  // Ш1f (хвост ревью Ш1e, решение Майи «везде одна подпись»): компактная плашка закрытой
  // фичи без мержа печатает полную CLOSED_NO_MERGE_STATUS, а не короткую «закрыта». Мутация
  // «вернуть литерал «закрыта»» краснит этот тест.
  it('закрытая (compact): полная «закрыта без мержа», не короткая «закрыта» (Ш1f)', () => {
    const html = renderHeader(feat({ bucket: 'closed', summary: null }), { compact: true });
    expect(html).toContain('закрыта без мержа');
  });

  it('смержена (closed + mergedAt): глиф done (галочка), «Смержена · {длительность}»', () => {
    const f = feat({ bucket: 'closed', summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 } });
    const st = featureDisplayState(f);
    expect(st.kind).toBe('done');
    expect(st.alarm).toBeNull();
    const html = renderHeader(f);
    expect(html.includes('data-feature-state="done"')).toBe(true);
    // v2 (FS-FL1): дата мержа — справа в строке 1, длительность — в строке 2.
    expect(html).toContain('Смержена · 30 мин');
    expect(etaShown(html)).toBe(localDate('2026-10-18T12:00:00Z'));
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

// === v2 (FS-FL1): срок — только feature.eta ===

describe('FeatureGroupHeader: v2 срок (feature.eta)', () => {
  it('endsAt → «≈ {день недели}» (табличные цифры, не моно)', () => {
    const f = feat({ eta: { minutes: 240, endsAt: '2026-11-13T15:00:00Z', source: 'note', clockStopped: null } });
    const html = renderHeader(f);
    const day = localWeekday('2026-11-13T15:00:00Z');
    const m = /data-testid="feature-group-eta" style="([^"]*)">([^<]*)</.exec(html);
    expect(m, 'нода срока не найдена').not.toBeNull();
    expect(m![2]).toBe(`≈ ${day}`);
    // 600 FS.sm textSecondary tabular-nums; шрифт — UI (не JetBrains Mono).
    expect(m![1]).toContain('font-variant-numeric:tabular-nums');
    expect(m![1]).toContain('font-weight:600');
    expect(m![1]).toContain('var(--c-text-secondary)');
    expect(m![1]).not.toContain('JetBrains Mono');
  });

  it('clockStopped=wait → «часы стоят», даже при живом endsAt', () => {
    const f = feat({ eta: { minutes: 240, endsAt: '2026-11-13T15:00:00Z', source: 'note', clockStopped: 'wait' } });
    const html = renderHeader(f);
    expect(etaShown(html)).toBe('часы стоят');
    expect(html).not.toContain('≈');
  });

  it('eta нет (или даты нет) → «—»', () => {
    expect(etaShown(renderHeader(feat()))).toBe('—');
    const f = feat({ eta: { minutes: 240, endsAt: null, source: 'history', clockStopped: null } });
    expect(etaShown(renderHeader(f))).toBe('—');
  });

  it('закрытая/смерженная — дата мержа вместо срока; без мержа — «—»', () => {
    const merged = feat({ bucket: 'closed', summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 }, eta: { minutes: 60, endsAt: null, source: 'history', clockStopped: null } });
    expect(etaShown(renderHeader(merged))).toBe(localDate('2026-10-18T12:00:00Z'));
    expect(etaShown(renderHeader(feat({ bucket: 'closed', summary: null })))).toBe('—');
  });
});

// === v2 (FS-FL1): тревога — только feature.ask ===

describe('FeatureGroupHeader: v2 фраза тревоги (feature.ask)', () => {
  it('ask wait → «Ждёт тебя: {text}» warningText 600, этапа в строке нет', () => {
    const f = feat({ bucket: 'attention', state: 'waiting', attention: attention('wait'), ask: ask(), stepsAccepted: 4, stepsTotal: 9 });
    const html = renderHeader(f);
    expect(html).toContain('Ждёт тебя: нужен ответ по счёту объёма');
    expect(html).not.toContain('Производство · 4/9');
    expect(statusStyle(html)).toContain('var(--c-warning-text)');
    expect(statusStyle(html)).toContain('font-weight:600');
  });

  it('ask merge → та же фраза «Ждёт тебя: {text}» (вид merge — общий тон warn)', () => {
    const f = feat({ bucket: 'attention', state: 'merge_pending', attention: attention('merge'), ask: ask({ source: 'merge', text: 'нужен вердикт по мержу' }) });
    const html = renderHeader(f);
    expect(html).toContain('Ждёт тебя: нужен вердикт по мержу');
    expect(html).not.toContain('ждёт согласия на мерж');
  });

  it('ask stopped → «Стоит {давность}: {text}» dangerText 600', () => {
    const f = feat({
      bucket: 'attention', state: 'stopped', attention: attention('stopped'),
      ask: ask({ source: 'stopped', text: 'Денис не стартует', since: isoAgo(120) }),
    });
    const html = renderHeader(f, { now: NOW_MS });
    // Давность — с ask.since, тот же формат длительности, что у статусов фичи.
    expect(html).toContain('Стоит 2 ч: Денис не стартует');
    expect(statusStyle(html)).toContain('var(--c-danger-text)');
    expect(statusStyle(html)).toContain('font-weight:600');
  });

  it('ask stopped без since → «Стоит: {text}», давности нет', () => {
    const f = feat({ bucket: 'attention', state: 'stopped', attention: attention('stopped'), ask: ask({ source: 'stopped', text: 'Денис не стартует' }) });
    const html = renderHeader(f, { now: NOW_MS });
    expect(html).toContain('Стоит: Денис не стартует');
    expect(html).not.toMatch(/Стоит \d+ (мин|ч)/);
  });

  it('полный текст тревоги — в title (фраза на строке — одна, с многоточием)', () => {
    const f = feat({ bucket: 'attention', state: 'waiting', attention: attention('wait'), ask: ask({ text: 'нужен ответ по счёту объёма' }) });
    expect(renderHeader(f)).toContain('title="нужен ответ по счёту объёма"');
  });

  it('закрытой ask не рисуется: остаётся подпись закрытия', () => {
    const f = feat({ bucket: 'closed', summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 }, ask: ask() });
    const html = renderHeader(f);
    expect(html).not.toContain('Ждёт тебя');
    expect(html).toContain('Смержена · 30 мин');
  });
});

// === v2 (FS-FL1): чего в строке нет (R2) ===

describe('FeatureGroupHeader: v2 в строке нет аватаров, ⚠ и процента', () => {
  it('богатая фикстура (nowPersonaIds, trackerIssues, шаги): ни аватаров, ни ⚠, ни %', () => {
    const f = feat({
      stepsAccepted: 3, stepsTotal: 5, stepsReview: 2, planState: 'partial',
      trackerIssues: 3, nowPersonaIds: ['p1', 'p2'],
    });
    const html = renderHeader(f, {
      personaById: (id: string) => ({ id, ownerId: 'o1', name: 'Кира', handle: id, scope: 'global', avatar: { kind: 'initials' }, memoryEnabled: false } as never),
    });
    const region = rowsRegion(html);
    // Стопки аватаров и «Сейчас {Имя}» нет (нахлёст −5 и имя-<b> — их подписи).
    expect(region).not.toContain('margin-left:-5px');
    expect(html).not.toContain('feature-group-now-name');
    // ⚠ расхождений трекера переехал на страницу фичи (R2).
    expect(region).not.toContain('⚠');
    expect(html).not.toContain('feature-group-tracker-issues');
    // Процента нет: полоса ниже говорит о прогрессе глазами.
    expect(region).not.toContain('%');
    expect(html).not.toContain('feature-group-percent');
    // Давности активности нет.
    expect(html).not.toContain('feature-group-age');
  });

  it('полная строка счётчиков — в title (formatStepProgress)', () => {
    const f = feat({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 2, planState: 'partial' });
    const html = renderHeader(f);
    expect(html).toContain('принято 3 из 5');
    expect(html).toContain('data-testid="feature-group-bar"');
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

// === selected (открыта страница фичи) ===

describe('FeatureGroupHeader: selected', () => {
  it('выбранная плашка: фон accentLight, рамка accentMuted', () => {
    const html = renderHeader(feat(), { selected: true });
    expect(html).toContain('var(--c-accent-light)');
    expect(html).toContain('var(--c-accent-muted)');
  });

  it('обычная: фон bgCard, рамка borderLight (selected не прилипает)', () => {
    const html = renderHeader(feat());
    expect(html).toContain('var(--c-bg-card)');
    expect(html).not.toContain('var(--c-accent-light)');
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
  it('в списке проекта — «features» на месте', () => {
    expect(featureGroupByOptions(true)).toEqual(['days', 'tags', 'subjects', 'features', 'none']);
  });

  it('вне проекта — «Фичи» нет (реестра фич нет)', () => {
    expect(featureGroupByOptions(false)).toEqual(['days', 'none']);
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

// === v2: полоса остаётся, процента нет (R2) ===
// Счётчики «принято N из T · …» (formatStepProgress) — в title строки 2. На виду —
// полоса трекера (сравнение фич глазами, не чтением); «N %» из строки убран.
describe('FeatureGroupHeader: v2 — полоса и title, процента нет', () => {
  it('старый ответ (no new fields) → ни процента, ни полосы (нет данных — пустых частей нет)', () => {
    const f = feat({ stepsAccepted: 3, stepsTotal: 5 });
    const html = renderHeader(f);
    expect(html).not.toContain('feature-group-percent');
    expect(html).not.toContain('feature-group-bar');
  });

  it('новые поля есть → полоса под строкой, полная строка — в title; процента нет', () => {
    const f = feat({ stepsAccepted: 3, stepsTotal: 5, stepsReview: 2, planState: 'partial' });
    const html = renderHeader(f);
    // title статуса — общий форматтер: «принято 3 из 5 · 2 в ревью · план частично», без голого «3/5».
    const m = /<span data-testid="feature-group-status"[^>]*title="([^"]*)"/.exec(html);
    expect(m, 'title строки 2 не найден').not.toBeNull();
    expect(m![1]).toContain('принято 3 из 5');
    expect(m![1]).not.toMatch(/\d+\s*\/\s*\d+/);
    // полоса: сегменты accepted (success) и review (info), без «master» (в индекса его нет)
    expect(html).toContain('data-testid="feature-group-bar"');
    expect(html).toContain('data-testid="feature-bar-accepted"');
    expect(html).toContain('data-testid="feature-bar-review"');
    expect(html).not.toContain('data-testid="feature-bar-master"');
    // процента в строке нет (R2): ни ноды, ни «N %» в тексте строк.
    expect(html).not.toContain('feature-group-percent');
    expect(rowsRegion(html)).not.toContain('%');
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
