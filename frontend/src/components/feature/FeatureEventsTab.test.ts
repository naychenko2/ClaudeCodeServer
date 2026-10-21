// Вкладка «События» страницы фичи (волна 3, Ф4; макет features-overview-3-final §4/§9а).
//
// Проводка данных — рендер вкладки с НАСТОЯЩИМИ events[] (FeatureEvent), не прямой проп:
//   • строка: время · аватар · имя персоны · понятная русская формулировка типа ·
//     переходы «чат · задача» (NavTargetPair: чат = sessionId, задача = taskId);
//   • группировка по дням (заголовок «Сегодня, dd.mm» / «Вчера, dd.mm» / «dd.mm»);
//   • «Показать все» (порог 8): по умолчанию 8 последних, раскрытие по eventsTotal;
//   • тон: ожидания (info) / ошибки (danger) / мерж (accent);
//   • пустое: «Событий пока нет».
//
// Мутации (в отчёте):
//   (1) «Показать все» не раскрывает остаток (всегда 8) → «показать все» краснеет;
//   (2) потерять taskId (null) → «переход в задачу» краснеет;
//   (3) тип события без формулировки (kind в raw) → «формулировка» краснеет;
//   (4) ожидание без тона (eventTone→null) → «тон ожидания» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  FeatureEventsTab, eventTone, eventPhrase, groupEventsByDay, EVENTS_COLLAPSED_LIMIT,
} from './FeatureEventsTab';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { __test as backNavTest } from '../../lib/featureBackNav';
import type { FeatureEvent } from '../../types/featureIndex';

// === Мок персон (lib/personas): id → имя; аватар — инициалы (без image) ===
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) =>
    personaMap.has(id) ? { id, name: personaMap.get(id), avatar: { kind: 'initials' as const } } : undefined,
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
vi.mock('../../lib/api', () => ({ api: { personas: { avatarUrl: () => '' } } }));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));
// Навигация: журнал вызовов (chat / task).
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { mockOpenChat.calls.push(id); return true; } }));
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));

// ── Хелперы данных ──────────────────────────────────────────────────────────────
function ev(over: Partial<FeatureEvent> = {}): FeatureEvent {
  return {
    at: '2026-10-20T10:00:00Z', kind: 'started', taskId: 't1',
    role: null, stepRef: null, personaId: null, sessionId: null, round: null, reason: null,
    ...over,
  };
}

// Корни всех mount'ов: unmount в afterEach (иначе два живых корня дёргают общий стор).
const roots: import('react-dom/client').Root[] = [];
function render(props: { events: FeatureEvent[]; eventsTotal?: number; isMobile?: boolean }) {
  installFakeDom();
  const { root, container } = mount(createElement(FeatureEventsTab, {
    events: props.events,
    eventsTotal: props.eventsTotal ?? props.events.length,
    featureId: 'f1',
    isMobile: props.isMobile ?? false,
  }));
  roots.push(root);
  return container;
}
function unmountAll() {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
}

beforeEach(() => {
  personaMap.clear();
  personaMap.set('p-kira', 'Кира');
  personaMap.set('p-gleb', 'Глеб');
  personaMap.set('p-mark', 'Марк');
  mockOpenChat.calls = [];
  mockOpenTask.calls = [];
  backNavTest.reset();
});
afterEach(() => {
  unmountAll();
  uninstallFakeDom();
  vi.restoreAllMocks();
});

// ═══ 1. Чистые функции: тон, формулировка, группировка по дням ═════════════════
describe('Чистые функции (тон/формулировка/дни)', () => {
  it('eventTone: waiting→wait, stopped→error, merged/closed→merge, прочее→null', () => {
    expect(eventTone(ev({ kind: 'waiting' }))).toBe('wait');
    expect(eventTone(ev({ kind: 'stopped' }))).toBe('error');
    expect(eventTone(ev({ kind: 'merged' }))).toBe('merge');
    expect(eventTone(ev({ kind: 'closed' }))).toBe('merge');
    expect(eventTone(ev({ kind: 'started' }))).toBeNull();
    expect(eventTone(ev({ kind: 'accepted' }))).toBeNull();
  });

  it('eventPhrase: понятные русские формулировки (мутация: kind в raw → red)', () => {
    expect(eventPhrase(ev({ kind: 'created', role: 'feature' }))).toBe('создание фичи');
    expect(eventPhrase(ev({ kind: 'started', stepRef: 'Ш1' }))).toBe('старт Ш1');
    expect(eventPhrase(ev({ kind: 'completed', stepRef: 'Ш2' }))).toBe('сдача Ш2');
    expect(eventPhrase(ev({ kind: 'accepted', stepRef: 'Ш1' }))).toBe('принято Ш1');
    expect(eventPhrase(ev({ kind: 'accepted_with_remarks', stepRef: 'Ш2' }))).toBe('принято Ш2 с замечаниями');
    expect(eventPhrase(ev({ kind: 'returned', stepRef: 'Ш3' }))).toBe('возврат Ш3');
    expect(eventPhrase(ev({ kind: 'stopped' }))).toBe('исполнитель остановился');
    expect(eventPhrase(ev({ kind: 'waiting' }))).toBe('ждёт вашего ответа');
    expect(eventPhrase(ev({ kind: 'merged' }))).toBe('мерж в master');
    expect(eventPhrase(ev({ kind: 'closed' }))).toBe('фича закрыта');
    // Мутация (3): неизвестный kind → raw (не выдумываем).
    expect(eventPhrase(ev({ kind: 'weird_kind' }))).toBe('weird_kind');
  });

  it('groupEventsByDay: группировка по дням (порядок at desc, заголовки dd.mm)', () => {
    const now = new Date('2026-10-20T12:00:00Z').getTime();
    const events = [
      ev({ at: '2026-10-20T10:00:00Z', kind: 'started', taskId: 'a' }),
      ev({ at: '2026-10-20T09:00:00Z', kind: 'started', taskId: 'b' }),
      ev({ at: '2026-10-19T18:00:00Z', kind: 'created', taskId: 'c' }),
    ];
    const days = groupEventsByDay(events, now);
    // Два дня: 20.10 (2 события) и 19.10 (1 событие).
    expect(days.length).toBe(2);
    expect(days[0].events.length).toBe(2);
    expect(days[1].events.length).toBe(1);
    // Заголовки: «Сегодня, 20.10» и «Вчера, 19.10» (now = 20.10).
    expect(days[0].label).toBe('Сегодня, 20.10');
    expect(days[1].label).toBe('Вчера, 19.10');
  });

  it('порог = 8 (EVENTS_COLLAPSED_LIMIT)', () => {
    expect(EVENTS_COLLAPSED_LIMIT).toBe(8);
  });
});

// ═══ 2. Проводка: строка события (время, имя, формулировка, «чат · задача») ═════
describe('Строка события (макет §9а)', () => {
  it('время, имя персоны, формулировка, пара «чат · задача»', () => {
    const e = ev({
      at: '2026-10-20T14:58:00Z', kind: 'accepted', stepRef: 'Ш2',
      personaId: 'p-gleb', sessionId: 'chat-g', taskId: 't-r2',
    });
    const container = render({ events: [e] });

    // Время (ЧЧ:ММ, локальный пояс).
    const time = findNodeByAttr(container, 'data-testid', 'event-time');
    expect(time).not.toBeNull();
    expect(textNodes(time!).join('')).toMatch(/^\d{2}:\d{2}$/);

    // Имя персоны.
    const who = findNodeByAttr(container, 'data-testid', 'event-who');
    expect(who).not.toBeNull();
    expect(textNodes(who!).join('')).toBe('Глеб');

    // Формулировка.
    const phrase = findNodeByAttr(container, 'data-testid', 'event-phrase');
    expect(phrase).not.toBeNull();
    expect(textNodes(phrase!).join('')).toBe('принято Ш2');

    // Пара «чат · задача»: чат = sessionId, задача = taskId.
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task')).not.toBeNull();
  });

  it('событие без чата (sessionId=null) → слота «чат» нет (showNoChat=false)', () => {
    const e = ev({ kind: 'created', role: 'feature', taskId: 't-epic', sessionId: null });
    const container = render({ events: [e] });
    // «чата нет» НЕ рисуется (showNoChat=false) — слот пуст.
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-no-chat')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat')).toBeNull();
    // Задача есть.
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task')).not.toBeNull();
  });
});

// ═══ 3. Тон: ожидания / ошибки / мерж выделены ═════════════════════════════════
describe('Тон события (макет: ожидания/ошибки/мерж выделены)', () => {
  it('ожидание (waiting) → имя персоны в tone info (мутация: eventTone→null → red)', () => {
    const e = ev({ kind: 'waiting', stepRef: 'Ш3', personaId: 'p-kira', sessionId: 'c1', taskId: 't-s3' });
    const container = render({ events: [e] });
    // eventTone('waiting') = 'wait' → имя персоны красится C.info.
    const who = findNodeByAttr(container, 'data-testid', 'event-who')!;
    expect((who as unknown as { style: Record<string, string> }).style.color).toBe('var(--c-info)');
  });

  it('ошибка (stopped) → tone danger', () => {
    const e = ev({ kind: 'stopped', personaId: 'p-kira', taskId: 't-s3' });
    const container = render({ events: [e] });
    const who = findNodeByAttr(container, 'data-testid', 'event-who')!;
    expect((who as unknown as { style: Record<string, string> }).style.color).toBe('var(--c-danger-text)');
  });

  it('мерж (merged) → tone accent', () => {
    const e = ev({ kind: 'merged', personaId: 'p-mark', sessionId: 'mk', taskId: 't-merge' });
    const container = render({ events: [e] });
    const who = findNodeByAttr(container, 'data-testid', 'event-who')!;
    expect((who as unknown as { style: Record<string, string> }).style.color).toBe('var(--c-accent)');
  });
});

// ═══ 4. «Показать все» (порог 8) ══════════════════════════════════════════════
describe('«Показать все» (макет: порог 8)', () => {
  it('по умолчанию 8; «Показать все · N» раскрывает остаток (мутация: не раскрывает → red)', async () => {
    // 10 событий, newest-first (как бэк: at desc): t9 (19:00) первым … t0 (10:00) последним.
    const events: FeatureEvent[] = [];
    for (let i = 9; i >= 0; i--) {
      events.push(ev({
        at: `2026-10-20T${String(10 + i).padStart(2, '0')}:00:00Z`,
        kind: 'started', taskId: `t${i}`,
      }));
    }
    const container = render({ events, eventsTotal: 10 });

    // По умолчанию: 8 (t9..t2), t1/t0 скрыты; кнопка «Показать все · 10».
    expect(findNodeByAttr(container, 'data-testid', 'event-row-started-t9')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'event-row-started-t0')).toBeNull();
    const showAll = findNodeByAttr(container, 'data-testid', 'events-show-all');
    expect(showAll).not.toBeNull();
    expect(textNodes(showAll!).join('')).toContain('10');

    // Клик → все 10.
    const onClick = reactProps(showAll!)!.onClick as () => void;
    await act(async () => { onClick(); });
    expect(findNodeByAttr(container, 'data-testid', 'event-row-started-t0')).not.toBeNull();
    expect(textNodes(showAll!).join('')).toContain('Свернуть');
  });

  it('≤ 8 событий → кнопки «Показать все» нет', () => {
    const events: FeatureEvent[] = [];
    for (let i = 0; i < 5; i++) events.push(ev({ kind: 'started', taskId: `t${i}` }));
    const container = render({ events });
    expect(findNodeByAttr(container, 'data-testid', 'events-show-all')).toBeNull();
  });
});

// ═══ 5. Навигация: «чат · задача» ══════════════════════════════════════════════
describe('Навигация «чат · задача» (макет §9а)', () => {
  it('клик «задача» → openTaskInSection(taskId); клик «чат» → openChatById(sessionId)', async () => {
    const e = ev({ kind: 'accepted', stepRef: 'Ш1', personaId: 'p-gleb', sessionId: 'chat-g', taskId: 't-r1' });
    const container = render({ events: [e] });

    const taskBtn = findNodeByAttr(container, 'data-testid', 'nav-target-task')!;
    const taskClick = reactProps(taskBtn)!.onClick as () => void;
    await act(async () => { taskClick(); });
    expect(mockOpenTask.calls).toEqual([{ id: 't-r1' }]);

    const chatBtn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    const chatClick = reactProps(chatBtn)!.onClick as () => void;
    await act(async () => { chatClick(); });
    expect(mockOpenChat.calls).toEqual(['chat-g']);
  });

  it('мутация: потерять taskId (null) → кнопки задачи нет (red)', () => {
    const e = ev({ kind: 'created', role: 'feature', taskId: '', sessionId: 'c1' });
    const container = render({ events: [e] });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task')).toBeNull();
  });
});

// ═══ 6. Пустое состояние ═══════════════════════════════════════════════════════
describe('Пустое состояние', () => {
  it('нет событий → «Событий пока нет»', () => {
    const container = render({ events: [] });
    const empty = findNodeByAttr(container, 'data-testid', 'events-empty');
    expect(empty).not.toBeNull();
    expect(textNodes(empty!).join('')).toBe('Событий пока нет');
  });
});
