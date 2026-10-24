// «Сейчас работают» (ST-F6). Рендер-тесты на fakeDom: отсутствие блока у пустого
// run.now и у merged/closed, строка на каждый элемент run.now (аватар, stepRef,
// «идёт N мин», переход в чат с expected id), состояния «чата нет» и «не запущена»,
// тач-цель 40 на 360, токены карточки.
//
// Мутации (проверены прогоном): (1) убрать `isFinished` в guard FeatureStatsNow.tsx
// — краснеет тест «merged/closed — блока нет»; (2) убрать `now.length === 0`
// — краснеет тест «пустой run.now — блока нет»; (3) `C.borderLight` в rowStyle →
// `C.border` — краснеет тест токенов; (4) `isMobile` не передать в NavTargetButton —
// краснеет тест тач-цели; (5) `minutesSince` без round → «1 ч 05 мин» не сходится
// с фикстурой — краснеет тест часов.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { C, R, FS, SP } from '../../lib/design';
import { FeatureStatsNow } from './FeatureStatsNow';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureNow, FeatureRun } from '../../types/featureRun';

const mockPersonas = vi.hoisted(() => [
  { id: 'p-kira', name: 'Кира', role: 'Frontend' },
  { id: 'p-denis', name: 'Денис', role: 'Backend' },
]);
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return { ...orig, usePersonas: () => mockPersonas };
});
vi.mock('../../lib/openChat', () => ({
  openChatById: (id: string) => { mockOpenChat.calls.push(id); return Promise.resolve(true); },
}));

// Фиксированное «сейчас»: даты в фикстуре — относительно него, минуты детерминированы.
const NOW = Date.parse('2026-10-24T12:00:00Z');

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

function mkNow(over: Partial<FeatureNow> = {}): FeatureNow {
  return {
    taskId: 't-1',
    stage: 'prod',
    personaId: 'p-kira',
    stepRef: 'Ш1',
    title: 'Пилить блок',
    startedAt: new Date(NOW - 7 * 60_000).toISOString(),
    linkedSessionId: 's-1',
    ...over,
  };
}

function mountNow(run: FeatureRun, isMobile?: boolean) {
  return mount(createElement(FeatureStatsNow, { run, isMobile, nowMs: NOW }));
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

beforeEach(() => { installFakeDom(); mockOpenChat.calls.length = 0; });
afterEach(() => { uninstallFakeDom(); });

describe('когда блока нет (к2)', () => {
  it('пустой run.now — блока нет', () => {
    const { container } = mountNow(mkRun({ now: [] }));
    expect(findNodeByAttr(container, 'data-testid', 'stats-now')).toBeNull();
  });

  it('run.now не передан (старые данные) — блока нет', () => {
    const { container } = mountNow(mkRun());
    expect(findNodeByAttr(container, 'data-testid', 'stats-now')).toBeNull();
  });

  it('merged — блока нет, даже если run.now непустой', () => {
    const { container } = mountNow(mkRun({ state: 'merged', now: [mkNow()] }));
    expect(findNodeByAttr(container, 'data-testid', 'stats-now')).toBeNull();
  });

  it('closed — блока нет', () => {
    const { container } = mountNow(mkRun({ state: 'closed', now: [mkNow()] }));
    expect(findNodeByAttr(container, 'data-testid', 'stats-now')).toBeNull();
  });

  it('флаг closed при живом state — блока нет', () => {
    const { container } = mountNow(mkRun({ closed: true, now: [mkNow()] }));
    expect(findNodeByAttr(container, 'data-testid', 'stats-now')).toBeNull();
  });
});

describe('строка на элемент run.now (к1)', () => {
  it('одна строка на элемент: аватар, имя, шаг, «идёт 7 мин», кнопка «чат»', () => {
    const run = mkRun({ now: [mkNow()] });
    const { container } = mountNow(run);
    const block = findNodeByAttr(container, 'data-testid', 'stats-now');
    expect(block, 'блок не найден').toBeTruthy();

    const row = findNodeByAttr(block!, 'data-testid', 'now-row-t-1');
    expect(row, 'строка t-1 отсутствует').toBeTruthy();
    const text = textOf(row);
    expect(text).toContain('Кира');
    expect(text).toContain('шаг Ш1');
    expect(text).toContain('идёт 7 мин');

    // Аватар — круг 18 px (PersonaAvatar).
    let avatar: FNodeLike | null = null;
    const walk = (n: FNodeLike) => {
      if (n.style?.width === '18px') avatar = n;
      for (const ch of n.childNodes) walk(ch);
    };
    walk(row!);
    expect(avatar, 'аватар 18 px не найден').toBeTruthy();

    // Переход в чат — NavTargetButton с ожидаемым id.
    const btn = findNodeByAttr(row!, 'data-testid', 'nav-target-chat');
    expect(btn, 'кнопка «чат» отсутствует').toBeTruthy();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(mockOpenChat.calls).toEqual(['s-1']);
  });

  it('несколько элементов — несколько строк в порядке run.now', () => {
    const run = mkRun({
      now: [
        mkNow({ taskId: 't-1', personaId: 'p-kira', stepRef: 'Ш1' }),
        mkNow({ taskId: 't-2', personaId: 'p-denis', stepRef: 'Ш2', startedAt: new Date(NOW - 65 * 60_000).toISOString() }),
      ],
    });
    const { container } = mountNow(run);
    const block = findNodeByAttr(container, 'data-testid', 'stats-now')!;
    const rows = block.children[1].children;
    expect(rows.length).toBe(2);
    expect(textOf(rows[0])).toContain('Кира');
    // Часы: 65 мин — «1 ч 05 мин» (formatDuration), не «65 мин».
    expect(textOf(rows[1])).toContain('Денис');
    expect(textOf(rows[1])).toContain('идёт 1 ч 05 мин');
  });

  it('без stepRef — шага в строке нет', () => {
    const run = mkRun({ now: [mkNow({ stepRef: null })] });
    const { container } = mountNow(run);
    const row = findNodeByAttr(container, 'data-testid', 'now-row-t-1');
    expect(textOf(row!)).not.toContain('шаг');
  });

  it('startedAt нет — «не запущена», без выдуманных минут', () => {
    const run = mkRun({ now: [mkNow({ startedAt: null })] });
    const { container } = mountNow(run);
    const text = textOf(findNodeByAttr(container, 'data-testid', 'now-row-t-1'));
    expect(text).toContain('не запущена');
    expect(text).not.toContain('идёт');
  });

  it('персона неизвестна — нейтральный маркер и «персона недоступна»', () => {
    const run = mkRun({ now: [mkNow({ personaId: 'p-ghost' })] });
    const { container } = mountNow(run);
    const text = textOf(findNodeByAttr(container, 'data-testid', 'now-row-t-1'));
    expect(text).toContain('персона недоступна');
  });

  it('чата нет — текст «чата нет», кнопки нет', () => {
    const run = mkRun({ now: [mkNow({ linkedSessionId: null })] });
    const { container } = mountNow(run);
    const row = findNodeByAttr(container, 'data-testid', 'now-row-t-1');
    expect(findNodeByAttr(row!, 'data-testid', 'nav-target-chat')).toBeNull();
    expect(findNodeByAttr(row!, 'data-testid', 'nav-target-no-chat'), 'пометка «чата нет»').toBeTruthy();
    expect(mockOpenChat.calls.length).toBe(0);
  });
});

describe('мобильная раскладка (к3)', () => {
  it('isMobile — тач-цель «чат» 40 px, строка во всю ширину', () => {
    const run = mkRun({ now: [mkNow()] });
    const { container } = mountNow(run, true);
    const row = findNodeByAttr(container, 'data-testid', 'now-row-t-1');
    const btn = findNodeByAttr(row!, 'data-testid', 'nav-target-chat');
    const style = btn!.style as Record<string, string>;
    expect(style.minHeight).toBe('40px');
    expect((row!.style as Record<string, string>).width).toBe('100%');
  });

  it('десктоп — строка не тянется на всю ширину', () => {
    const run = mkRun({ now: [mkNow()] });
    const { container } = mountNow(run);
    const row = findNodeByAttr(container, 'data-testid', 'now-row-t-1');
    expect((row!.style as Record<string, string>).width).not.toBe('100%');
  });
});

describe('токены (к4)', () => {
  it('карточка строки — bgCard, borderLight, R.lg; подпись секции — FS.xxs UPPERCASE', () => {
    const run = mkRun({ now: [mkNow()] });
    const { container } = mountNow(run);
    const row = findNodeByAttr(container, 'data-testid', 'now-row-t-1').style as Record<string, string>;
    expect(row.background).toBe(C.bgCard);
    expect(row.border).toBe(`1px solid ${C.borderLight}`);
    expect(row.borderRadius).toBe(`${R.lg}px`);
    expect(row.gap).toBe(`${SP.sm}px`);

    const block = findNodeByAttr(container, 'data-testid', 'stats-now');
    const label = block!.children[0].style as Record<string, string>;
    expect(label.fontSize).toBe(`${FS.xxs}px`);
    expect(label.textTransform).toBe('uppercase');
    expect(label.color).toBe(C.textMuted);
  });
});

type FNodeLike = {
  attributes?: Record<string, string>;
  style?: Record<string, string>;
  childNodes: FNodeLike[];
};
