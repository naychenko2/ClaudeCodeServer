// «Чаты группы» (ST-F10). Рендер-тесты на fakeDom: пустой список — блока нет;
// подпись строки — «шаг · роль» из chatLabel, обрезанного id в тексте нет
// (regex /[0-9a-f]{8}/); чисел в строке чата нет (числа группы — в таблице, ST-F11);
// архивный чат — C.textMuted и «в архиве», переход ровно один с ожидаемым chatId;
// высота строки 34, на мобильной — 44; склонение счётчика; список резется шестью
// чатами, остаток — «ещё N» со склонением; блок отделён сверху рамкой, чип шага — 700.
//
// Мутации (проверены прогоном): (1) подпись брать из chatId, а не chatLabel —
// краснеет тест «подпись — «шаг · роль», id в тексте нет»; (2) убрать `archived`
// в rowStyle — краснеет тест приглушения архивного; (3) `isMobile` не передать в
// rowStyle — краснеет тест высоты 44; (4) убрать filter(Boolean) — краснеет тест
// пустого списка; (5) вернуть числа в строку (span с numbersText) — краснеет тест
// «в строке чата нет чисел»; (6) убрать slice(MAX_ROWS) — краснеет тест среза
// «7 чатов — 6 строк».

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { C, R, SP } from '../../lib/design';
import { FeatureBreakdownChats } from './FeatureBreakdownChats';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureBreakdownGroup } from '../../types/featureStats';
import type { FeatureRun, FeatureStageExecutor } from '../../types/featureRun';

const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/openChat', () => ({
  openChatById: (id: string) => { mockOpenChat.calls.push(id); return Promise.resolve(true); },
}));

// Id «как на проде»: полные uuid — если подпись строится из id, regex их поймает.
const CHAT_A = '3f9a1c2e-1111-4aaa-8bbb-cccccccccccc'; // живой: Ш2 · доработка
const CHAT_B = '7d4b8e12-2222-4bbb-8ccc-dddddddddddd'; // архивный: ревью

function mkExecutor(over: Partial<FeatureStageExecutor>): FeatureStageExecutor {
  return {
    personaId: 'p-kira', taskId: 't-1', taskRole: 'rework', stepRef: 'Ш2', lane: 'front',
    status: 'done', chatId: CHAT_A, chatArchived: false, modelLabel: null, modelFull: null,
    ...over,
  };
}

function mkRun(executors: FeatureStageExecutor[]): FeatureRun {
  return {
    featureId: 'f-1', state: 'running', steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    stages: [{
      key: 'prod', state: 'done', passes: 1, taskIds: ['t-1'],
      firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false,
      rounds: [], executors,
    }],
  };
}

function mkGroup(over: Partial<FeatureBreakdownGroup> = {}): FeatureBreakdownGroup {
  return { key: 'p-kira', minutes: 12, tokens: 4500, costUsd: 1.2, chatIds: [CHAT_A, CHAT_B], ...over };
}

function mountChats(group: FeatureBreakdownGroup, run: FeatureRun, isMobile?: boolean) {
  return mount(createElement(FeatureBreakdownChats, { group, run, isMobile }));
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

beforeEach(() => { installFakeDom(); mockOpenChat.calls.length = 0; });
afterEach(() => { uninstallFakeDom(); });

describe('когда блока нет (к3)', () => {
  it('пустой chatIds — блока нет', () => {
    const { container } = mountChats(mkGroup({ chatIds: [] }), mkRun([]));
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-chats')).toBeNull();
  });

  it('chatIds не передан (старые данные) — блока нет', () => {
    const { container } = mountChats(mkGroup({ chatIds: undefined as unknown as string[] }), mkRun([]));
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-chats')).toBeNull();
  });

  it('пустые id отфильтрованы: один настоящий чат — одна строка', () => {
    const { container } = mountChats(mkGroup({ chatIds: ['', CHAT_A] }), mkRun([mkExecutor({})]));
    const block = findNodeByAttr(container, 'data-testid', 'breakdown-chats');
    expect(block, 'блок не найден').toBeTruthy();
    const rows = block!.children[1].children;
    expect(rows.length).toBe(1);
  });
});

describe('подпись строки (к1)', () => {
  it('«шаг · роль» из executors: «Ш2» чипом, «доработка» текстом; id в тексте нет', () => {
    const { container } = mountChats(mkGroup(), mkRun([mkExecutor({})]));
    const block = findNodeByAttr(container, 'data-testid', 'breakdown-chats');
    const row = findNodeByAttr(block!, 'data-testid', `breakdown-chat-${CHAT_A}`);
    expect(row, 'строка чата отсутствует').toBeTruthy();
    const text = textOf(row);
    expect(text).toContain('Ш2');
    expect(text).toContain('доработка');
    // Обрезанного id в DOM нет: ни полного uuid, ни его первых 8 символов.
    expect(/[0-9a-f]{8}/.test(text), `в тексте найден id: ${text}`).toBe(false);
  });

  it('несколько чатов — строки в порядке chatIds, каждый без id в тексте', () => {
    const run = mkRun([
      mkExecutor({}),
      mkExecutor({ taskId: 't-2', taskRole: 'review', stepRef: null, chatId: CHAT_B, chatArchived: true }),
    ]);
    const { container } = mountChats(mkGroup(), run);
    const block = findNodeByAttr(container, 'data-testid', 'breakdown-chats')!;
    const rows = block.children[1].children;
    expect(rows.length).toBe(2);
    expect(textOf(rows[1])).toContain('ревью');
    expect(/[0-9a-f]{8}/.test(textOf(block))).toBe(false);
  });

  it('чат не среди исполнителей — подпись «чат», без id', () => {
    const { container } = mountChats(mkGroup({ chatIds: ['ghost-chat-id'] }), mkRun([mkExecutor({})]));
    const row = findNodeByAttr(container, 'data-testid', 'breakdown-chat-ghost-chat-id');
    expect(row, 'строка «чат» отсутствует').toBeTruthy();
    const text = textOf(row);
    expect(text).toContain('чат');
    expect(/[0-9a-f]{8}/.test(text), `в тексте найден id: ${text}`).toBe(false);
  });

  it('в строке чата нет чисел группы: ни «мин», ни «тыс.», ни «$», ни «нет учёта»', () => {
    const { container } = mountChats(mkGroup(), mkRun([mkExecutor({})]));
    const text = textOf(findNodeByAttr(container, 'data-testid', `breakdown-chat-${CHAT_A}`));
    // Подпись на месте — иначе проверка пустого текста была бы тривиальной.
    expect(text).toContain('Ш2');
    expect(text).toContain('доработка');
    for (const bad of ['мин', 'тыс.', '$', 'нет учёта']) {
      expect(text, `в строке найдено число «${bad}»: ${text}`).not.toContain(bad);
    }
  });
});

describe('архивный чат (к2)', () => {
  it('архивный — приглушён C.textMuted и помечен «в архиве»', () => {
    const run = mkRun([
      mkExecutor({}),
      mkExecutor({ taskId: 't-2', taskRole: 'review', stepRef: null, chatId: CHAT_B, chatArchived: true }),
    ]);
    const { container } = mountChats(mkGroup(), run);
    const row = findNodeByAttr(container, 'data-testid', `breakdown-chat-${CHAT_B}`);
    expect(row, 'строка архивного чата отсутствует').toBeTruthy();
    expect((row!.style as Record<string, string>).color, 'архивный чат не приглушён').toBe(C.textMuted);
    expect(textOf(row)).toContain('в архиве');
    // Живой чат — без приглушения и пометки (fakeDom отдаёт '' для незаданного
    // свойства стиля, поэтому сравниваем с токеном, а не с undefined).
    const live = findNodeByAttr(container, 'data-testid', `breakdown-chat-${CHAT_A}`);
    expect((live!.style as Record<string, string>).color).not.toBe(C.textMuted);
    expect(textOf(live)).not.toContain('в архиве');
  });

  it('переход — ровно один вызов openChatById с ожидаемым chatId', () => {
    const { container } = mountChats(mkGroup({ chatIds: [CHAT_A] }), mkRun([mkExecutor({})]));
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(btn, 'кнопка «чат» отсутствует').toBeTruthy();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(mockOpenChat.calls).toEqual([CHAT_A]);
  });
});

describe('высота строки и тач-цель (к3)', () => {
  it('десктоп — 34 px', () => {
    const { container } = mountChats(mkGroup({ chatIds: [CHAT_A] }), mkRun([mkExecutor({})]));
    const row = findNodeByAttr(container, 'data-testid', `breakdown-chat-${CHAT_A}`);
    const style = row!.style as Record<string, string>;
    expect(style.height).toBe('34px');
    expect(style.background).toBe(C.bgWhite);
    expect(style.border).toBe(`1px solid ${C.borderLight}`);
    expect(style.borderRadius).toBe(`${R.md}px`);
  });

  it('isMobile — 44 px (минимальная тач-цель), строка во всю ширину', () => {
    const { container } = mountChats(mkGroup({ chatIds: [CHAT_A] }), mkRun([mkExecutor({})]), true);
    const row = findNodeByAttr(container, 'data-testid', `breakdown-chat-${CHAT_A}`);
    const style = row!.style as Record<string, string>;
    expect(style.height).toBe('44px');
    expect(style.width).toBe('100%');
    // Кнопка перехода тоже тач-цель ≥ 40 (NavTargetButton).
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect((btn!.style as Record<string, string>).minHeight).toBe('40px');
  });
});

describe('срез списка и рамка блока (хвосты макета)', () => {
  it('7 чатов — 6 строк и строка «ещё 1 чат»', () => {
    const ids = Array.from({ length: 7 }, (_, i) => `c-${i}`);
    const { container } = mountChats(mkGroup({ chatIds: ids }), mkRun([]));
    const block = findNodeByAttr(container, 'data-testid', 'breakdown-chats')!;
    expect(block.children[1].children.length, 'срез должен быть 6 строк').toBe(6);
    expect(textOf(block)).toContain('ещё 1 чат');
  });

  it('8 чатов — «ещё 2 чата»; ровно 6 — строки «ещё» нет', () => {
    const { container } = mountChats(
      mkGroup({ chatIds: Array.from({ length: 8 }, (_, i) => `c-${i}`) }), mkRun([]),
    );
    expect(textOf(findNodeByAttr(container, 'data-testid', 'breakdown-chats')!)).toContain('ещё 2 чата');

    const six = mountChats(mkGroup({ chatIds: Array.from({ length: 6 }, (_, i) => `c-${i}`) }), mkRun([]));
    expect(textOf(findNodeByAttr(six.container, 'data-testid', 'breakdown-chats')!)).not.toContain('ещё');
  });

  it('блок отделён сверху: border-top borderLight, padding-top 12', () => {
    const { container } = mountChats(mkGroup({ chatIds: [CHAT_A] }), mkRun([mkExecutor({})]));
    const style = findNodeByAttr(container, 'data-testid', 'breakdown-chats')!.style as Record<string, string>;
    expect(style.borderTop).toBe(`1px solid ${C.borderLight}`);
    expect(style.paddingTop).toBe(`${SP.md}px`);
  });

  it('чип шага — жирный 700 (макет .ref)', () => {
    const { container } = mountChats(mkGroup({ chatIds: [CHAT_A] }), mkRun([mkExecutor({})]));
    const row = findNodeByAttr(container, 'data-testid', `breakdown-chat-${CHAT_A}`)!;
    expect((row.children[0].style as Record<string, string>).fontWeight).toBe('700');
  });
});

describe('счётчик секции (склонение)', () => {
  it('склонение «N чат/чата/чатов»', () => {
    const run = mkRun([mkExecutor({})]);
    const cases: Array<[number, string]> = [
      [1, '1 чат'], [2, '2 чата'], [5, '5 чатов'], [11, '11 чатов'], [21, '21 чат'],
    ];
    for (const [n, want] of cases) {
      const { container } = mountChats(mkGroup({ chatIds: Array.from({ length: n }, (_, i) => `c-${i}`) }), run);
      const label = findNodeByAttr(container, 'data-testid', 'breakdown-chats')!.children[0];
      expect(textOf(label)).toContain(want);
    }
  });
});
