// Тесты таблицы разреза (ST-F11). Рендер на fakeDom: колонки и строка «Всего»,
// сортировка по заголовку с aria-sort (по умолчанию — время), полоса 8 px от максимума
// колонки, «нет учёта» один раз без полосы, «бесплатно» через formatMoney, склонение
// «N чатов ›» и раскрытие FeatureBreakdownChats (одна строка за раз), живые чипы из
// liveChipText, labelOf/colorOf, hiddenKeys, fewData → null. Числа — те, что отдаёт
// модель ST-F8; пересчёт в компоненте не проверяем.
//
// Мутации (проверены прогоном): (1) убрать sortGroups (порядок из входа) — краснеет
// тест сортировки по токенам; (2) убрать guard fewData — краснеет тест «<2 групп → null»;
// (3) не фильтровать hiddenKeys — краснеет тест «скрытый ключ не рендерится»;
// (4) снять сброс TH_BTN (font/cursor) — краснеет тест сброса UA-стилей заголовков;
// (5) sortGroups(visible, measure) → [...visible] — краснеют и «по умолчанию — время по
// убыванию» (фикстура не отсортирована по времени), и сортировка по токенам;
// (6) normalizeModelKey «как есть» — краснеет not.toContain('Авто') в тесте чипов.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { C } from '../../lib/design';
import { FeatureBreakdownTable } from './FeatureBreakdownTable';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureBreakdownGroup } from '../../types/featureStats';
import type { FeatureRun, FeatureStage, FeatureStageExecutor } from '../../types/featureRun';

function mkGroup(
  key: string,
  minutes: number,
  tokens: number | null,
  costUsd: number | null,
  chatIds: string[] = [],
): FeatureBreakdownGroup {
  return { key, minutes, tokens, costUsd, chatIds };
}

// Кира — локальная модель (0 при токенах → «бесплатно»), Тимур — самый дорогой по
// токенам, Вера — без учёта. Порядок входа не совпадает ни с одним порядком по мере
// (ни по времени, ни по токенам, ни по деньгам): сортировка проверяется, а не
// совпадает с входом.
const GROUPS = [
  mkGroup('vera', 30, null, null, ['c5', 'c6', 'c7', 'c8', 'c9']),
  mkGroup('kira', 120, 1000, 0, ['c1', 'c2', 'c3']),
  mkGroup('timur', 60, 3000, 5.0, ['c4']),
];

function mkExecutor(over: Partial<FeatureStageExecutor> = {}): FeatureStageExecutor {
  return {
    personaId: null,
    taskId: 't-1',
    taskRole: null,
    stepRef: null,
    lane: null,
    status: 'done',
    chatId: null,
    chatArchived: false,
    modelLabel: null,
    modelFull: null,
    ...over,
  };
}

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

function mkStage(executors: FeatureStageExecutor[]): FeatureStage {
  return {
    key: 'prod',
    state: 'active',
    passes: 1,
    taskIds: ['t-1'],
    rounds: [],
    firstStartedAt: null,
    lastCompletedAt: null,
    barEnd: null,
    approxMinutes: 180,
    overlapsActive: false,
    executors,
  };
}

// Кира — «работает» на Qwen 3.8 (шаг Ш7), Тимур — «ждёт вас» на Opus (ревью).
const RUN_LIVE = mkRun({
  stages: [
    mkStage([
      mkExecutor({ personaId: 'kira', taskRole: 'step', stepRef: 'Ш7', status: 'running', chatId: 'c1', modelFull: 'Авто → Qwen 3.8' }),
      mkExecutor({ personaId: 'timur', taskRole: 'review', status: 'waiting', chatId: 'c4', modelFull: 'Claude Opus 5.5' }),
    ]),
  ],
});

function mountTable(
  groups: FeatureBreakdownGroup[],
  extra: {
    axis?: 'personas' | 'models' | 'roles';
    run?: FeatureRun;
    labelOf?: (k: string) => string;
    colorOf?: (k: string) => string;
    hiddenKeys?: string[];
  } = {},
) {
  return mount(createElement(FeatureBreakdownTable, {
    groups,
    axis: extra.axis ?? 'personas',
    run: extra.run ?? mkRun(),
    labelOf: extra.labelOf,
    colorOf: extra.colorOf,
    hiddenKeys: extra.hiddenKeys,
  }));
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

function styleOf(node: unknown): Record<string, string> {
  return (node as { style: Record<string, string> }).style;
}

// Корень таблицы и строки данных (без «Всего»).
function tableNode(container: ReturnType<typeof mount>['container']) {
  return findNodeByAttr(container, 'data-testid', 'feature-breakdown-table');
}

function rowOrder(container: ReturnType<typeof mount>['container']): string[] {
  const table = tableNode(container);
  if (!table) return [];
  return table
    .querySelectorAll('tr')
    .filter(t => (t.getAttribute('data-testid') ?? '').startsWith('breakdown-row-'))
    .map(t => (t.getAttribute('data-testid') as string).slice('breakdown-row-'.length));
}

function rowCells(container: ReturnType<typeof mount>['container'], key: string) {
  const row = findNodeByAttr(container, 'data-testid', `breakdown-row-${key}`);
  if (!row) return null;
  return row.children.filter(c => c.nodeName === 'TD');
}

function click(node: ReturnType<typeof findNodeByAttr> | null) {
  if (!node) return;
  act(() => { (reactProps(node) as { onClick: () => void }).onClick(); });
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('мало данных — таблицы нет (к2)', () => {
  it('пустой разрез — null', () => {
    const { container } = mountTable([]);
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-table')).toBeNull();
  });

  it('одна группа — null (сравнивать нечего)', () => {
    const { container } = mountTable([mkGroup('kira', 60, 100, 1)]);
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-table')).toBeNull();
  });

  it('одна видимая группа после hiddenKeys — тоже null', () => {
    const { container } = mountTable(GROUPS, { hiddenKeys: ['timur', 'vera'] });
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-table')).toBeNull();
  });
});

describe('колонки, строки и «Всего» (к1)', () => {
  it('шапка: разрез, «Модели и роли», время/токены/деньги, чаты', () => {
    const { container } = mountTable(GROUPS);
    const table = tableNode(container)!;
    const heads = table.querySelectorAll('th').map(t => textOf(t));
    expect(heads[0]).toBe('персоны');
    expect(heads[1]).toBe('Модели и роли');
    expect(heads[2]).toContain('время');
    expect(heads[3]).toContain('токены');
    expect(heads[4]).toContain('деньги');
    expect(heads[5]).toBe('чаты');
  });

  it('строка на группу + строка «Всего» с суммами', () => {
    const { container } = mountTable(GROUPS);
    expect(rowOrder(container).length).toBe(3);
    const total = findNodeByAttr(container, 'data-testid', 'breakdown-total');
    expect(total, 'строки «Всего» нет').toBeTruthy();
    const text = textOf(total);
    expect(text).toContain('Всего');
    expect(text).toContain('3 ч 30 мин'); // 120+60+30
    expect(text).toContain('4 тыс.');     // 1000+3000
    expect(text).toContain('$5,00');     // 0+5,00
  });

  it('имя группы — labelOf, а не сырой ключ; цвет — colorOf', () => {
    const { container } = mountTable(GROUPS, {
      labelOf: key => (key === 'kira' ? 'Кира' : key),
      colorOf: key => (key === 'kira' ? C.success : C.accent),
    });
    const cells = rowCells(container, 'kira')!;
    expect(textOf(cells[0])).toContain('Кира');
    expect(textOf(cells[0])).not.toContain('kira');
    const avatar = cells[0].children[0].children[0];
    expect(styleOf(avatar).background).toBe(C.success);
  });

  it('кнопка сортировки: доступное имя «Сортировать по: …», подпись не красится на th, отступа нет', () => {
    const { container } = mountTable(GROUPS);
    const btn = findNodeByAttr(container, 'data-testid', 'breakdown-sort-tokens')!;
    expect(btn.getAttribute('aria-label')).toBe('Сортировать по: токены');
    expect(styleOf(btn).color).toBe(C.textMuted);
    expect(parseFloat(styleOf(btn).padding)).toBe(0);
    const active = findNodeByAttr(container, 'data-testid', 'breakdown-sort-time')!;
    expect(styleOf(active).color).toBe(C.textHeading);
    expect(styleOf(active.parentElement).color).toBe(C.textMuted);
  });

  it('UA-сброс заголовков сортировки: cursor, align, font (урок ST-F7)', () => {
    const { container } = mountTable(GROUPS);
    const btn = findNodeByAttr(container, 'data-testid', 'breakdown-sort-time');
    expect(btn, 'кнопки сортировки нет').toBeTruthy();
    const st = styleOf(btn);
    expect(st.cursor).toBe('pointer');
    expect(st.textAlign).toBe('left');
    expect(st.font).toBe('inherit');
    expect(st.background).toBe('none');
  });
});

describe('сортировка и aria-sort (к1)', () => {
  it('по умолчанию — время по убыванию; у активной колонки aria-sort=descending', () => {
    const { container } = mountTable(GROUPS);
    expect(rowOrder(container)).toEqual(['kira', 'timur', 'vera']);
    const table = tableNode(container)!;
    const ths = table.querySelectorAll('th');
    expect(ths[2].getAttribute('aria-sort')).toBe('descending');
    // aria-sort ставится только активной колонке.
    expect(ths[3].getAttribute('aria-sort')).toBeNull();
    expect(ths[4].getAttribute('aria-sort')).toBeNull();
  });

  it('клик по «токены»: порядок по токенам, null — в конце; aria-sort переезжает', () => {
    const { container } = mountTable(GROUPS);
    click(findNodeByAttr(container, 'data-testid', 'breakdown-sort-tokens'));
    expect(rowOrder(container)).toEqual(['timur', 'kira', 'vera']);
    const ths = tableNode(container)!.querySelectorAll('th');
    expect(ths[3].getAttribute('aria-sort')).toBe('descending');
    expect(ths[2].getAttribute('aria-sort')).toBeNull();
  });

  it('клик по «деньги»: порядок по деньгам; «бесплатно» (0) — выше без учёта', () => {
    const { container } = mountTable(GROUPS);
    click(findNodeByAttr(container, 'data-testid', 'breakdown-sort-cost'));
    expect(rowOrder(container)).toEqual(['timur', 'kira', 'vera']);
  });
});

describe('полоса меры и состояния ячеек (к2)', () => {
  it('полоса 8 px: ширина — доля от максимума колонки, цвет — цвет группы', () => {
    const { container } = mountTable(GROUPS, { colorOf: () => C.info });
    const cells = rowCells(container, 'timur')!;
    const bar = findNodeByAttr(cells[3], 'data-testid', 'measure-bar');
    expect(bar, 'полосы токенов нет').toBeTruthy();
    expect(parseFloat(styleOf(bar).height)).toBe(8);
    const fill = bar!.children[0];
    expect(parseFloat(styleOf(fill).width)).toBeCloseTo(100, 3);
    expect(styleOf(fill).background).toBe(C.info);

    const kiraCells = rowCells(container, 'kira')!;
    const kiraFill = findNodeByAttr(kiraCells[3], 'data-testid', 'measure-bar')!.children[0];
    expect(parseFloat(styleOf(kiraFill).width)).toBeCloseTo(1000 / 3000 * 100, 3);
  });

  it('без учёта — «нет учёта» один раз, без полосы и процента', () => {
    const { container } = mountTable(GROUPS);
    const cells = rowCells(container, 'vera')!;
    expect(textOf(cells[3])).toBe('нет учёта');
    expect(textOf(cells[4])).toBe('нет учёта');
    expect(findNodeByAttr(cells[3], 'data-testid', 'measure-bar')).toBeNull();
    expect(findNodeByAttr(cells[4], 'data-testid', 'measure-bar')).toBeNull();
  });

  it('0 при токенах — «бесплатно», а не $0,00', () => {
    const { container } = mountTable(GROUPS);
    const cells = rowCells(container, 'kira')!;
    expect(textOf(cells[4])).toContain('бесплатно');
    expect(textOf(cells[4])).not.toContain('$0,00');
  });

  it('«Всего» без учёта — «—», как в макете', () => {
    const { container } = mountTable([mkGroup('a', 60, null, null), mkGroup('b', 30, null, null)]);
    const cells = findNodeByAttr(container, 'data-testid', 'breakdown-total')!.children;
    expect(textOf(cells[3])).toBe('—');
    expect(textOf(cells[4])).toBe('—');
  });

  it('hiddenKeys: скрытая строка не рендерится и не входит в «Всего»', () => {
    const { container } = mountTable(GROUPS, { hiddenKeys: ['vera'] });
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-row-vera')).toBeNull();
    const total = findNodeByAttr(container, 'data-testid', 'breakdown-total');
    expect(textOf(total)).toContain('3 ч'); // 180 мин без Веры
    expect(textOf(total)).not.toContain('30 мин');
  });
});

describe('чипы «Модели и роли» (к3)', () => {
  it('персона: модели (нормализованные) и роли', () => {
    const { container } = mountTable(GROUPS, { run: RUN_LIVE });
    const cells = rowCells(container, 'kira')!;
    const text = textOf(cells[1]);
    expect(text).toContain('Qwen 3.8'); // «Авто → » снят normalizeModelKey
    expect(text).not.toContain('Авто');
    expect(text).toContain('шаг');
  });

  it('без colorOf чипы-персоны разных исполнителей получают заливку, а не пустой фон', () => {
    const run = mkRun({
      stages: [mkStage([
        mkExecutor({ personaId: 'kira', taskRole: 'step', modelFull: 'Qwen' }),
        mkExecutor({ personaId: 'vera', taskRole: 'step', modelFull: 'Qwen' }),
      ])],
    });
    const { container } = mountTable(
      [mkGroup('Qwen', 60, 1, 0), mkGroup('Opus', 30, 1, 0)],
      { axis: 'models', run },
    );
    const avatars = rowCells(container, 'Qwen')![1].querySelectorAll('span').filter(s => styleOf(s).width === '18px');
    expect(avatars.length).toBe(2);
    for (const a of avatars) expect(styleOf(a).background).toMatch(/^var\(--c-/);
    // Мутация: один цвет на все чипы (chipColor → ARC_COLORS[0]) — тест краснеет.
    expect(styleOf(avatars[0]).background, 'фоны разных исполнителей различаются').not.toBe(styleOf(avatars[1]).background);
  });

  it('без executors — чипов нет, строка живая (не падение)', () => {
    const { container } = mountTable(GROUPS); // run без stages
    const cells = rowCells(container, 'kira')!;
    expect(textOf(cells[1])).toBe('');
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-row-kira')).toBeTruthy();
  });
});

describe('живые чипы (к3)', () => {
  it('running — «работает», waiting — «ждёт вас»', () => {
    const { container } = mountTable(GROUPS, { run: RUN_LIVE });
    expect(textOf(findNodeByAttr(container, 'data-testid', 'live-chip-kira'))).toContain('работает');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'live-chip-timur'))).toContain('ждёт вас');
    // Точка не читается скринридером отдельно: слово «работает» уже есть рядом.
    const dot = findNodeByAttr(findNodeByAttr(container, 'data-testid', 'live-chip-kira')!, 'role', 'status');
    expect(dot!.parentElement!.getAttribute('aria-hidden')).toBe('true');
    expect(findNodeByAttr(container, 'data-testid', 'live-chip-vera')).toBeNull();
  });

  it('смерженная фича — живых чипов нет', () => {
    const { container } = mountTable(GROUPS, { run: mkRun({ state: 'merged', stages: RUN_LIVE.stages }) });
    expect(findNodeByAttr(container, 'data-testid', 'live-chip-kira')).toBeNull();
  });
});

describe('«N чатов ›» и раскрытие (к3)', () => {
  it('склонение: 1 чат, 3 чата, 5 чатов', () => {
    const { container } = mountTable(GROUPS);
    expect(textOf(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-timur'))).toBe('1 чат ›');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira'))).toBe('3 чата ›');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-vera'))).toBe('5 чатов ›');
  });

  it('без чатов — ссылки нет', () => {
    const { container } = mountTable([
      mkGroup('a', 60, 100, 1),
      mkGroup('b', 30, 50, 0.5),
    ]);
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-a')).toBeNull();
  });

  it('клик раскрывает чаты строки; другой строки — только одна за раз; повторный клик закрывает', () => {
    const { container } = mountTable(GROUPS, { run: RUN_LIVE });
    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira'));
    let blocks = tableNode(container)!.querySelectorAll('div').filter(d => d.getAttribute('data-testid') === 'breakdown-chats');
    expect(blocks.length).toBe(1);
    expect(textOf(blocks[0])).toContain('Чаты · 3 чата');

    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-timur'));
    blocks = tableNode(container)!.querySelectorAll('div').filter(d => d.getAttribute('data-testid') === 'breakdown-chats');
    expect(blocks.length, 'раскрыто больше одной строки').toBe(1);
    expect(textOf(blocks[0])).toContain('Чаты · 1 чат');

    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-timur'));
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-chats')).toBeNull();
  });

  it('раскрытая строка подсвечена (accentLight + полоса accent слева)', () => {
    const { container } = mountTable(GROUPS);
    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira'));
    const row = findNodeByAttr(container, 'data-testid', 'breakdown-row-kira');
    expect(styleOf(row).background).toBe(C.accentLight);
    const firstCell = row!.children[0];
    expect(styleOf(firstCell).boxShadow).toContain(C.accent);
  });
});
