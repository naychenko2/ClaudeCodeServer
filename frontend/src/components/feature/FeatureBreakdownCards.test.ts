// Тесты мобильных карточек разреза (ST-F12). Рендер на fakeDom: карточка на группу,
// порядок по мере из sortGroups, три мини-полосы 6 px «время / токены / деньги» от
// МАКСИМУМА колонки (как ячейки таблицы), «нет учёта» в своей строке без полосы и
// процента, «бесплатно» через formatMoney, чипы связи разрезов из groupChips (персоны
// — модели и роли; модели и роли — персоны), живые чипы из liveChipText, «N чатов ›»
// со склонением и раскрытие FeatureBreakdownChats (одна группа за раз), раскрытие НЕ
// сбрасывается при смене меры (перерендер того же корня), тач-цель кнопки 44 px и
// сброс UA-стилей, hiddenKeys, fewData → null. Числа — те, что отдаёт модель ST-F8;
// пересчёт в компоненте не проверяем.
//
// Мутации (проверены прогоном): (1) убрать sortGroups (порядок из входа) — краснеет
// тест порядка по мере; (2) зафиксировать сортировку на 'time' (sortGroups(visible,
// 'time') вместо measure) — краснеет половина про «токены»; (3) не фильтровать
// hiddenKeys — краснеет тест «скрытая группа не рендерится»; (4) снять высоту 44 у
// кнопки чатов — краснеет тест тач-цели. Фикстура GROUPS намеренно НЕ отсортирована
// по времени: иначе проверка порядка по «времени» вакуумна.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { C } from '../../lib/design';
import { FeatureBreakdownCards } from './FeatureBreakdownCards';
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
// (в частности, не отсортирован по времени): иначе сортировка проверялась бы
// совпадением с входом, а не работой sortGroups.
const GROUPS = [
  mkGroup('vera', 30, null, null, ['c5', 'c6', 'c7', 'c8', 'c9']),
  mkGroup('timur', 60, 3000, 5.0, ['c4']),
  mkGroup('kira', 120, 1000, 0, ['c1', 'c2', 'c3']),
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

function propsOf(
  groups: FeatureBreakdownGroup[],
  extra: {
    axis?: 'personas' | 'models' | 'roles';
    measure?: 'time' | 'tokens' | 'cost';
    run?: FeatureRun;
    labelOf?: (k: string) => string;
    colorOf?: (k: string) => string;
    hiddenKeys?: string[];
  } = {},
) {
  return {
    groups,
    axis: extra.axis ?? 'personas',
    measure: extra.measure ?? 'time',
    run: extra.run ?? mkRun(),
    labelOf: extra.labelOf,
    colorOf: extra.colorOf,
    hiddenKeys: extra.hiddenKeys,
  };
}

function mountCards(
  groups: FeatureBreakdownGroup[],
  extra: Parameters<typeof propsOf>[1] = {},
) {
  return mount(createElement(FeatureBreakdownCards, propsOf(groups, extra)));
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

function styleOf(node: unknown): Record<string, string> {
  return (node as { style: Record<string, string> }).style;
}

function cardsRoot(container: ReturnType<typeof mount>['container']) {
  return findNodeByAttr(container, 'data-testid', 'feature-breakdown-cards');
}

function cardOrder(container: ReturnType<typeof mount>['container']): string[] {
  const root = cardsRoot(container);
  if (!root) return [];
  return root
    .children
    .filter(c => (c.getAttribute('data-testid') ?? '').startsWith('breakdown-card-'))
    .map(c => (c.getAttribute('data-testid') as string).slice('breakdown-card-'.length));
}

function cardNode(container: ReturnType<typeof mount>['container'], key: string) {
  return findNodeByAttr(container, 'data-testid', `breakdown-card-${key}`);
}

function click(node: ReturnType<typeof findNodeByAttr> | null) {
  if (!node) return;
  act(() => { (reactProps(node) as { onClick: () => void }).onClick(); });
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('мало данных — карточек нет (guard fewData)', () => {
  it('пустой разрез — null', () => {
    const { container } = mountCards([]);
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-cards')).toBeNull();
  });

  it('одна группа — null (сравнивать нечего)', () => {
    const { container } = mountCards([mkGroup('kira', 60, 100, 1)]);
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-cards')).toBeNull();
  });

  it('одна видимая группа после hiddenKeys — тоже null', () => {
    const { container } = mountCards(GROUPS, { hiddenKeys: ['timur', 'vera'] });
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-cards')).toBeNull();
  });
});

describe('карточка на группу (к1)', () => {
  it('имя — labelOf, а не сырой ключ; аватар и полоса — цвет colorOf', () => {
    const { container } = mountCards(GROUPS, {
      labelOf: key => (key === 'kira' ? 'Кира' : key),
      colorOf: key => (key === 'kira' ? C.success : C.accent),
    });
    const card = cardNode(container, 'kira')!;
    expect(textOf(card)).toContain('Кира');
    expect(textOf(card)).not.toContain('kira');
    const avatar = card.children[0].children[0];
    expect(styleOf(avatar).background).toBe(C.success);
    const fill = findNodeByAttr(card, 'data-testid', 'measure-bar-time')!.children[0];
    expect(styleOf(fill).background).toBe(C.success);
  });

  it('на 360 нет фиксированных ширинок больше 328 px: карточка — 100%', () => {
    const { container } = mountCards(GROUPS);
    const root = cardsRoot(container)!;
    expect(styleOf(cardNode(container, 'kira')!).width).toBe('100%');
    const fixed: string[] = [];
    const walk = (n: typeof root): void => {
      // Ни width, ни minWidth в px больше 328 (план ST-F12).
      for (const prop of ['width', 'minWidth']) {
        const w = styleOf(n)[prop];
        if (w && w !== '100%' && parseFloat(w) > 328) fixed.push(`${prop}: ${w}`);
      }
      for (const c of n.children) walk(c);
    };
    walk(root);
    expect(fixed).toEqual([]);
  });

  it('порядок по мере: время — kira, timur, vera; токены — timur, kira, vera (null в конце)', () => {
    const { container } = mountCards(GROUPS, { measure: 'time' });
    expect(cardOrder(container)).toEqual(['kira', 'timur', 'vera']);
    const { container: c2 } = mountCards(GROUPS, { measure: 'tokens' });
    expect(cardOrder(c2)).toEqual(['timur', 'kira', 'vera']);
  });

  it('подпись над карточками называет меру сортировки', () => {
    const cap = (m: 'time' | 'tokens' | 'cost') =>
      textOf(findNodeByAttr(mountCards(GROUPS, { measure: m }).container, 'data-testid', 'breakdown-cards-caption'));
    expect(cap('time')).toBe('По убыванию времени · сортировка — в шапке');
    expect(cap('tokens')).toContain('токенов');
    expect(cap('cost')).toContain('денег');
  });

  it('hiddenKeys: скрытая группа не рендерится и не входит в долю', () => {
    const { container } = mountCards(GROUPS, { hiddenKeys: ['vera'] });
    expect(cardNode(container, 'vera')).toBeNull();
    // Сумма без Веры — 180 мин: доля Кирой — 67 %, а не 57 %.
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-pct-kira'))).toBe('67 %');
  });
});

describe('три мини-полосы 6 px — из модели (к1)', () => {
  it('в каждой карточке три строки: время, токены, деньги; полоса от МАКСИМУМА колонки, высота 6', () => {
    const { container } = mountCards(GROUPS);
    const card = cardNode(container, 'timur')!;
    expect(textOf(findNodeByAttr(card, 'data-testid', 'measure-bar-time'))).toBe('');
    expect(textOf(findNodeByAttr(card, 'data-testid', 'measure-value-time'))).toContain('1 ч');
    expect(textOf(findNodeByAttr(card, 'data-testid', 'measure-value-tokens'))).toContain('3 тыс.');
    expect(textOf(findNodeByAttr(card, 'data-testid', 'measure-value-cost'))).toContain('$5,00');
    for (const m of ['time', 'tokens', 'cost']) {
      const bar = findNodeByAttr(card, 'data-testid', `measure-bar-${m}`);
      expect(bar, `нет полосы ${m}`).toBeTruthy();
      expect(parseFloat(styleOf(bar).height)).toBe(6);
    }
    // Максимумы: время 120, токены 3000, деньги 5 — доли полос от них.
    // Ширина — именно в процентах («50px» не проходит).
    const widthOf = (c: Parameters<typeof findNodeByAttr>[0], m: string): string =>
      styleOf(findNodeByAttr(c, 'data-testid', `measure-bar-${m}`)!.children[0]).width;
    expect(widthOf(card, 'time')).toMatch(/^50(\.0+)?%$/);
    expect(widthOf(card, 'tokens')).toBe('100%');
    const kira = cardNode(container, 'kira')!;
    expect(widthOf(kira, 'time')).toBe('100%');
    expect(widthOf(kira, 'tokens')).toMatch(/^33\.3+\d*%$/);
    expect(widthOf(kira, 'cost')).toBe('0%');
  });

  it('время: «2 ч» форматером, процент percentText от суммы; полоса — не от суммы, а от максимума', () => {
    const { container } = mountCards(GROUPS, { measure: 'time' });
    const card = cardNode(container, 'kira')!;
    expect(textOf(card)).toContain('2 ч');
    expect(textOf(findNodeByAttr(card, 'data-testid', 'share-pct-kira'))).toBe('57 %');
  });

  it('токены: «3 тыс.» и «1 тыс.»; без учёта — «нет учёта» в своей строке, без полосы', () => {
    const { container } = mountCards(GROUPS, { measure: 'tokens' });
    const timur = cardNode(container, 'timur')!;
    expect(textOf(timur)).toContain('3 тыс.');
    expect(textOf(findNodeByAttr(timur, 'data-testid', 'share-pct-timur'))).toBe('75 %');
    const vera = cardNode(container, 'vera')!;
    expect(textOf(findNodeByAttr(vera, 'data-testid', 'measure-none-tokens'))).toBe('нет учёта');
    expect(textOf(findNodeByAttr(vera, 'data-testid', 'measure-none-cost'))).toBe('нет учёта');
    expect(findNodeByAttr(vera, 'data-testid', 'measure-bar-tokens')).toBeNull();
    expect(findNodeByAttr(vera, 'data-testid', 'measure-bar-cost')).toBeNull();
    // Время у Веры есть — его строка с полосой (30/120 = 25 %).
    const veraBar = findNodeByAttr(vera, 'data-testid', 'measure-bar-time');
    expect(veraBar).toBeTruthy();
    expect(styleOf(veraBar!.children[0]).width).toBe('25%');
    expect(findNodeByAttr(vera, 'data-testid', 'share-pct-vera')).toBeNull();
  });

  it('деньги: 0 при токенах — «бесплатно», а не $0,00; null — «нет учёта»', () => {
    const { container } = mountCards(GROUPS, { measure: 'cost' });
    expect(cardOrder(container)).toEqual(['timur', 'kira', 'vera']);
    expect(textOf(cardNode(container, 'timur')!)).toContain('$5,00');
    const kira = cardNode(container, 'kira')!;
    expect(textOf(findNodeByAttr(kira, 'data-testid', 'measure-value-cost'))).toBe('бесплатно');
    expect(textOf(kira)).not.toContain('$0,00');
    expect(textOf(findNodeByAttr(cardNode(container, 'vera')!, 'data-testid', 'measure-none-cost'))).toBe('нет учёта');
  });
});

describe('чипы связи разрезов (к2)', () => {
  const NAMES: Record<string, string> = { kira: 'Кира', timur: 'Тимур', vera: 'Вера' };
  const labelOf = (key: string) => NAMES[key] ?? key;

  it('персона — её модели и роли; без исполнителей — чипов нет', () => {
    const { container } = mountCards(GROUPS, { run: RUN_LIVE, labelOf });
    const kira = cardNode(container, 'kira')!;
    expect(textOf(findNodeByAttr(kira, 'data-testid', 'breakdown-chip-model:Qwen 3.8'))).toBe('Qwen 3.8');
    expect(textOf(findNodeByAttr(kira, 'data-testid', 'breakdown-chip-role:step'))).toBe('шаг');
    const timur = cardNode(container, 'timur')!;
    expect(textOf(findNodeByAttr(timur, 'data-testid', 'breakdown-chip-model:Claude Opus 5.5'))).toBe('Claude Opus 5.5');
    expect(textOf(findNodeByAttr(timur, 'data-testid', 'breakdown-chip-role:review'))).toBe('ревью');
    // Вера — не исполнитель: чипов в её карточке нет.
    const vera = cardNode(container, 'vera')!;
    expect(vera.querySelectorAll('span').filter(s => (s.getAttribute('data-testid') ?? '').startsWith('breakdown-chip-')).length).toBe(0);
    // run без стадий — чипов нигде (пустой список, а не падение).
    const { container: bare } = mountCards(GROUPS, { labelOf });
    expect(findNodeByAttr(bare, 'data-testid', 'breakdown-chip-model:Qwen 3.8')).toBeNull();
  });

  it('модели и роли — персоны участников: имя и аватар, а не сырой ключ', () => {
    const modelGroups = [
      mkGroup('Claude Opus 5.5', 60, 3000, 5.0, ['c4']),
      mkGroup('Qwen 3.8', 120, 1000, 0, ['c1']),
    ];
    const { container } = mountCards(modelGroups, { axis: 'models', run: RUN_LIVE, labelOf });
    const opus = cardNode(container, 'Claude Opus 5.5')!;
    const chip = findNodeByAttr(opus, 'data-testid', 'breakdown-chip-persona:timur');
    expect(textOf(chip)).toContain('Тимур');
    expect(styleOf(chip).border).toBe('none'); // персона — без рамки, с аватаром
    expect(parseFloat(styleOf(chip.children[0]).width)).toBe(18); // аватар 18 px
    const qwen = cardNode(container, 'Qwen 3.8')!;
    expect(textOf(findNodeByAttr(qwen, 'data-testid', 'breakdown-chip-persona:kira'))).toContain('Кира');
    const roleGroups = [
      mkGroup('review', 60, 3000, 5.0, ['c4']),
      mkGroup('step', 120, 1000, 0, ['c1']),
    ];
    const { container: rc } = mountCards(roleGroups, { axis: 'roles', run: RUN_LIVE, labelOf });
    expect(textOf(findNodeByAttr(cardNode(rc, 'step')!, 'data-testid', 'breakdown-chip-persona:kira'))).toContain('Кира');
    expect(textOf(findNodeByAttr(cardNode(rc, 'review')!, 'data-testid', 'breakdown-chip-persona:timur'))).toContain('Тимур');
  });
});

describe('живые чипы (к3)', () => {
  it('running — «работает», waiting — «ждёт вас», у закрытой — нет', () => {
    const { container } = mountCards(GROUPS, { run: RUN_LIVE });
    expect(textOf(findNodeByAttr(container, 'data-testid', 'live-chip-kira'))).toContain('работает');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'live-chip-timur'))).toContain('ждёт вас');
    const dot = findNodeByAttr(findNodeByAttr(container, 'data-testid', 'live-chip-kira')!, 'role', 'status');
    expect(dot!.parentElement!.getAttribute('aria-hidden')).toBe('true');
    expect(findNodeByAttr(container, 'data-testid', 'live-chip-vera')).toBeNull();
    const { container: closed } = mountCards(GROUPS, { run: mkRun({ state: 'merged', stages: RUN_LIVE.stages }) });
    expect(findNodeByAttr(closed, 'data-testid', 'live-chip-kira')).toBeNull();
  });
});

describe('кнопка чатов: тач-цель и UA-сброс (к1, к3)', () => {
  it('высота 44, cursor/align/font сброшены (урок ST-F7)', () => {
    const { container } = mountCards(GROUPS);
    const btn = findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira');
    expect(btn, 'кнопки чатов нет').toBeTruthy();
    const st = styleOf(btn);
    expect(parseFloat(st.height)).toBe(44);
    expect(st.cursor).toBe('pointer');
    expect(st.textAlign).toBe('left');
    expect(st.font).toBe('inherit');
    expect(st.background).toBe('none');
    expect(st.border).toBe('none');
  });

  it('aria-expanded отражает раскрытие', () => {
    const { container } = mountCards(GROUPS, { run: RUN_LIVE });
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira')!.getAttribute('aria-expanded')).toBe('false');
    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira'));
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira')!.getAttribute('aria-expanded')).toBe('true');
  });

  it('склонение: 1 чат, 3 чата, 5 чатов; без чатов — кнопки нет', () => {
    const { container } = mountCards(GROUPS);
    expect(textOf(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-timur'))).toBe('1 чат ›');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira'))).toBe('3 чата ›');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-vera'))).toBe('5 чатов ›');
    const { container: bare } = mountCards([mkGroup('a', 60, 100, 1), mkGroup('b', 30, 50, 0.5)]);
    expect(findNodeByAttr(bare, 'data-testid', 'breakdown-chats-toggle-a')).toBeNull();
  });
});

describe('раскрытие чатов по key группы (к3)', () => {
  it('клик раскрывает чаты внутри карточки; другая группа — только одна за раз; повторный клик закрывает', () => {
    const { container } = mountCards(GROUPS, { run: RUN_LIVE });
    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira'));
    let blocks = cardsRoot(container)!.querySelectorAll('div').filter(d => d.getAttribute('data-testid') === 'breakdown-chats');
    expect(blocks.length).toBe(1);
    expect(textOf(cardNode(container, 'kira')!)).toContain('Чаты · 3 чата');
    // Карточка раскрыта — подсветка «выбранной» (макет .c-mr.sel).
    expect(styleOf(cardNode(container, 'kira')!).background).toBe(C.accentLight);

    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-timur'));
    blocks = cardsRoot(container)!.querySelectorAll('div').filter(d => d.getAttribute('data-testid') === 'breakdown-chats');
    expect(blocks.length, 'раскрыто больше одной группы').toBe(1);
    expect(textOf(cardNode(container, 'timur')!)).toContain('Чаты · 1 чат');
    expect(styleOf(cardNode(container, 'kira')!).background).not.toBe(C.accentLight);

    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-timur'));
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-chats')).toBeNull();
  });

  it('смена меры (перерендер того же корня): порядок меняется, раскрытие остаётся', () => {
    const { root, container } = mountCards(GROUPS, { measure: 'time', run: RUN_LIVE });
    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-kira'));
    act(() => {
      root.render(createElement(FeatureBreakdownCards, propsOf(GROUPS, { measure: 'tokens', run: RUN_LIVE })));
    });
    expect(cardOrder(container)).toEqual(['timur', 'kira', 'vera']);
    expect(textOf(cardNode(container, 'kira')!)).toContain('Чаты · 3 чата');
    expect(styleOf(cardNode(container, 'kira')!).background).toBe(C.accentLight);
  });
});
