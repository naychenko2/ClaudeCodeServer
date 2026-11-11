// FP-C1: рендер строки-подзаголовка FeatureChatSubtitle (макет chat-feature-fit-v1, вариант А).
// Модель — настоящая featureChatCardModel (тот же путь данных, что в продукте), рендер —
// fakeDom, как в FeatureChatCard.test: строка проверяется как она будет стоять в шапке (FP-C3).
//
// Что защищается (постановка): строка — одна, без фона и рамки (root — PressableArea),
// aria-haspopup="dialog" и aria-expanded отражают проп; 1280 — глиф · название · mono-счёт
// с жирным числом · «часы стоят» тоном · слово состояния тоном слова; 360 — точка тона +
// «{count} · {eta}», а при тревоге — слово вместо счёта, весь блок minHeight 40; клик — onOpen.
//
// Мутации (в отчёте): (1) tone «ждёт» в chatCardStateWord warn→muted → краснеет ровно
// «слово — тон слова модели» (1280-тест слова); (2) убрать minHeight на мобиле → краснеет
// «цель 40»; (3) убрать ветку «слово вместо счёта» на мобиле → краснеет «при тревоге — слово».

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { C } from '../../../lib/design';
import { FeatureChatSubtitle } from './FeatureChatSubtitle';
import { featureChatCardModel } from './featureChatCardModel';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../__tests__/fakeDom';
import type { FeatureIndexFeature } from '../../../types/featureIndex';
import type { FeatureRun } from '../../../types/featureRun';

const { personaMap } = vi.hoisted(() => ({
  personaMap: new Map<string, string>([['p-kira', 'Кира'], ['p-gleb', 'Глеб'], ['p-alex', 'Александр']]),
}));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Фичи: упрощение', projectId: 'p1',
    bucket: 'running', state: 'running', attention: null,
    rail: [], chips: ['open'],
    stepsAccepted: 6, stepsTotal: 44, stepsReview: 2, stepsWork: 3, stepsNotCreated: 33, planState: 'cut',
    loops: 0, now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  } as FeatureIndexFeature;
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1', state: 'running',
    epic: { id: 'f1', title: 'Фича', status: 'todo' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

const NOW = Date.parse('2026-11-10T12:00:00Z');
const styleOf = (n: NonNullable<ReturnType<typeof findNodeByAttr>>): Record<string, string> =>
  (n as { style: Record<string, string> }).style;
const textOf = (n: NonNullable<ReturnType<typeof findNodeByAttr>>): string => textNodes(n).join('');

const roots: import('react-dom/client').Root[] = [];
let openCount = 0;

function renderSubtitle(model: ReturnType<typeof featureChatCardModel>, isMobile = false, expanded = false) {
  const { root, container } = mount(createElement(FeatureChatSubtitle, {
    model, isMobile, expanded, onOpen: () => { openCount++; },
  }));
  roots.push(root);
  return container;
}

function click(node: ReturnType<typeof findNodeByAttr>): void {
  act(() => { (reactProps(node!)!.onClick as () => void)(); });
}

// Обход дерева в поиске svg-точки (Dot): узел с именем 'svg' под корнем строки.
function findSvgUnder(c: NonNullable<ReturnType<typeof findNodeByAttr>>): ReturnType<typeof findNodeByAttr> {
  let hit: ReturnType<typeof findNodeByAttr> = null;
  const walk = (n: NonNullable<ReturnType<typeof findNodeByAttr>>) => {
    if (hit) return;
    // fakeDom кладёт nodeName в верхнем регистре (как настоящий DOM): 'SVG'.
    if (n.nodeType === 1 && n.nodeName === 'SVG') { hit = n; return; }
    for (const ch of (n as { childNodes: NonNullable<ReturnType<typeof findNodeByAttr>>[] }).childNodes) walk(ch);
  };
  walk(c);
  return hit;
}

// Модели — через тот же featureChatCardModel, что и продукт.
const waitModel = () => featureChatCardModel(feat({
  state: 'waiting',
  attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-alex', sessionId: 's-alex', taskId: 't-1' },
  ask: { text: 'согласен, что «ещё N» можно убрать?', source: 'wait', personaId: 'p-alex' },
}), null, 's-me', false, NOW);
const waitModelMobile = () => featureChatCardModel(feat({
  state: 'waiting',
  attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: 'p-alex', sessionId: 's-alex', taskId: 't-1' },
}), null, 's-me', true, NOW);
const runningModel = () => featureChatCardModel(feat({ nowPersonaIds: ['p-kira'] }), null, 's-me', true, NOW);

beforeEach(() => { openCount = 0; installFakeDom(); });
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

describe('FeatureChatSubtitle: строка 1280 (макет §5 «Строка фичи»)', () => {
  it('одна строка-кнопка без фона и рамки: глиф · название в title · mono-счёт · слово «ждёт тебя: Александр» тоном слова модели', () => {
    const c = renderSubtitle(waitModel());
    const root = findNodeByAttr(c, 'data-testid', 'feature-chat-subtitle');
    expect(root, 'строки нет').not.toBeNull();
    // Строка — текст, а не панель: у корня нет ни фона, ни рамки (отклик — класс .cc-pressable).
    expect(styleOf(root!).background ?? styleOf(root!).backgroundColor, 'фон строки').toBeFalsy();
    expect(styleOf(root!).border ?? styleOf(root!).borderWidth, 'рамка строки').toBeFalsy();
    expect(findNodeByAttr(c, 'data-testid', 'feature-subtitle-glyph'), 'глифа нет').not.toBeNull();
    const name = findNodeByAttr(c, 'data-testid', 'feature-subtitle-name');
    expect(textOf(name!), 'названия нет').toBe('Фичи: упрощение');
    expect(name!.getAttribute('title'), 'полного названия в title нет').toBe('Фичи: упрощение');
    const count = findNodeByAttr(c, 'data-testid', 'feature-subtitle-count');
    expect(textOf(count!), 'счёта нет').toContain('6 из 44');
    expect(styleOf(count!).fontFamily, 'счёт не mono').toContain('JetBrains Mono');
    const word = findNodeByAttr(c, 'data-testid', 'feature-subtitle-word');
    expect(textOf(word!), 'слова состояния нет').toBe('ждёт тебя: Александр');
    expect(styleOf(word!).color, 'тон слова не тон слова модели').toBe(C.warningText);
    expect(styleOf(word!).fontWeight, 'тревога не 600').toBe('600');
    const dispatched: string[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent = (e: Event) => { dispatched.push(e.type); return true; };
    click(root);
    expect(openCount, 'клик по строке не позвал onOpen').toBe(1);
    expect(dispatched.length, 'лишних событий нет').toBe(0);
  });

  it('закрытая: «12 шагов» с жирным числом textHeading, срок без своего цвета, слово «смержена» successText', () => {
    const m = featureChatCardModel(feat({
      title: 'Смержена', bucket: 'closed', state: 'merged',
      stepsAccepted: 12, stepsTotal: 12, planState: 'done',
    }), run({ state: 'merged', summary: { mergedAt: '2026-10-19T12:00:00Z', durationMinutes: 913, tasks: 14, steps: 12, loops: 1 } }), 's-me', false, NOW);
    const c = renderSubtitle(m);
    const word = findNodeByAttr(c, 'data-testid', 'feature-subtitle-word');
    expect(textOf(word!), 'слова закрытой нет').toBe('смержена');
    expect(styleOf(word!).color, 'закрытая не success-тон').toBe(C.successText);
    const count = findNodeByAttr(c, 'data-testid', 'feature-subtitle-count')!;
    expect(textOf(count), 'счёта и срока закрытой нет').toContain('12 шагов');
    expect(textOf(count), 'срока закрытой нет').toContain('15 ч 13 мин');
    expect(styleOf(count).color, 'обычный срок получил свой цвет').toBeFalsy();
    const num = count.children.find((n) => n.nodeName === 'B');
    expect(num, 'числа без жирного нет').toBeTruthy();
    expect(textOf(num!), 'не то число').toBe('12');
    expect(styleOf(num!).fontWeight, 'число не 700').toBe('700');
    expect(styleOf(num!).color, 'число не textHeading').toBe(C.textHeading);
  });

  it('aria-haspopup="dialog", aria-expanded отражает проп; «часы стоят» — C.warningText по etaPaused', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-alex', sessionId: 's-alex', taskId: 't-1' },
      eta: { minutes: 120, source: 'note', clockStopped: 'wait' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    const c = renderSubtitle(m, false, false);
    const el = findNodeByAttr(c, 'data-testid', 'feature-chat-subtitle')!;
    expect(el.getAttribute('aria-haspopup'), 'aria-haspopup').toBe('dialog');
    expect(el.getAttribute('aria-expanded'), 'aria-expanded=false').toBe('false');
    const count = findNodeByAttr(c, 'data-testid', 'feature-subtitle-count')!;
    expect(textOf(count), '«часы стоят» нет').toContain('часы стоят');
    const paused = count.childNodes.find((n) => textNodes(n).join('').includes('часы стоят'));
    expect(paused, 'обёртки «часы стоят» нет').toBeTruthy();
    expect(styleOf(paused!).color, '«часы стоят» не warningText').toBe(C.warningText);
    const c2 = renderSubtitle(m, false, true);
    expect(findNodeByAttr(c2, 'data-testid', 'feature-chat-subtitle')!.getAttribute('aria-expanded'), 'aria-expanded=true').toBe('true');
  });
});

describe('FeatureChatSubtitle: строка 360 (макет §5 «360: строка»)', () => {
  it('цель 40, точка тона и «{count} · {eta}» без слова; aria-haspopup=dialog, aria-expanded отражает проп', () => {
    const m = runningModel();
    const c = renderSubtitle(m, true, false);
    const el = findNodeByAttr(c, 'data-testid', 'feature-chat-subtitle')!;
    expect(styleOf(el).minHeight, 'цель тапа меньше 40').toBe('40px');
    expect(styleOf(el).fontSize, 'шрифт строки не FS.xs').toBe('11px');
    expect(el.getAttribute('aria-haspopup'), 'aria-haspopup').toBe('dialog');
    expect(el.getAttribute('aria-expanded'), 'aria-expanded=false').toBe('false');
    const count = findNodeByAttr(c, 'data-testid', 'feature-subtitle-count');
    expect(textOf(count!), 'счёта на мобиле нет').toContain('6 из 44');
    // на спокойной 360-строке слова состояния нет (макет: счёт/срок)
    expect(findNodeByAttr(c, 'data-testid', 'feature-subtitle-word'), 'слово на спокойной 360-строке').toBeNull();
    const svg = findSvgUnder(el);
    expect(svg, 'точки тона нет').not.toBeNull();
    expect(svg!.getAttribute('width'), 'точка не 6 px').toBe('6');
    const c2 = renderSubtitle(m, true, true);
    expect(findNodeByAttr(c2, 'data-testid', 'feature-chat-subtitle')!.getAttribute('aria-expanded'), 'aria-expanded=true').toBe('true');
  });

  it('при тревоге (warn) — слово «ждёт тебя» вместо счёта, тон warningText', () => {
    const c = renderSubtitle(waitModelMobile(), true, false);
    const word = findNodeByAttr(c, 'data-testid', 'feature-subtitle-word');
    expect(word, 'слова тревоги на мобиле нет').not.toBeNull();
    // без ask имя в текст фразы не вынесено (phrase.name=null) — слово без имени, без двоеточия
    expect(textOf(word!)).toBe('ждёт тебя');
    expect(styleOf(word!).color, 'тон не warn').toBe(C.warningText);
    expect(findNodeByAttr(c, 'data-testid', 'feature-subtitle-count'), 'счёт при слове').toBeNull();
    const svg = findSvgUnder(findNodeByAttr(c, 'data-testid', 'feature-chat-subtitle')!);
    expect(svg, 'точки тона нет').not.toBeNull();
  });
});
