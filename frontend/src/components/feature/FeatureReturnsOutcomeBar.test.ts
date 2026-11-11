// Тесты карточки «Исход ревью» (FP-R1, вариант Б): outcomeBarModel — отдельно (числа
// эталонного эпика 11041582: accepted 80 / remarks 32 / returned 20 → «61 % с первого раза»,
// отозванные в знаменатель не входят); рендер — renderToStaticMarkup (полоса, легенда,
// состояния «0 ревью» и «все сразу»); фокус по чипу и мобильная легенда — на fakeDom
// (тот же приём, что в FeatureReviewReturns.test.ts).
//
// Мутации (проверены прогоном в копии дерева, 11.11): (1) включить withdrawn в total в
// outcomeBarModel — краснеет ровно «модель на числах эпика…» (total 135, firstPassPct 59);
// после отката зелёный. (2) рисовать нулевые сегменты в полосе — краснеет «нулевые сегменты
// не рисуются» и «все сразу — один сегмент». (3) не фильтровать сегменты по focusKey
// (прочие без 0.25) — краснеет «нажатие подсвечивает свой сегмент…».

import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureReturnsOutcomeBar, outcomeBarModel } from './FeatureReturnsOutcomeBar';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureVerdicts } from '../../types/featureRun';

const mk = (over: Partial<FeatureVerdicts> = {}): FeatureVerdicts => ({
  accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0, ...over,
});

// Числа эталонного эпика 11041582 (закрытый): 132 вердикта, 80 приняты сразу.
const V132 = mk({ accepted: 80, acceptedWithRemarks: 32, returned: 20, withdrawn: 3 });

const html = (v: FeatureVerdicts, isMobile?: boolean): string =>
  renderToStaticMarkup(createElement(FeatureReturnsOutcomeBar, { verdicts: v, isMobile }));

const count = (h: string, needle: string): number => (h.match(new RegExp(needle, 'g')) ?? []).length;
// Текст каждого чипа легенды (метки, счётчики и проценты — в порядке html).
const chipTexts = (h: string): string[] =>
  [...h.matchAll(/data-testid="returns-chip-\w+"[^>]*>([\s\S]*?)<\/button>/g)]
    .map((m) => m[1].replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim());

describe('outcomeBarModel (c1)', () => {
  // Мутация (1): withdrawn в total — здесь же краснеет (total 135, firstPassPct 59).
  it('числа эпика: total 132, firstPassPct 61, проценты сегментов, withdrawn 3 вне полосы', () => {
    const m = outcomeBarModel(V132);
    expect(m.total).toBe(132);
    expect(m.firstPassPct).toBe(61);
    expect(m.withdrawn).toBe(3);
    expect(m.segments).toEqual([
      { key: 'accepted', count: 80, pct: 61 },
      { key: 'remarks', count: 32, pct: 24 },
      { key: 'returned', count: 20, pct: 15 },
    ]);
  });

  it('total 0: firstPassPct и проценты — 0, деления нет', () => {
    const m = outcomeBarModel(mk({ withdrawn: 2 }));
    expect(m.total).toBe(0);
    expect(m.firstPassPct).toBe(0);
    expect(m.segments.every((s) => s.count === 0 && s.pct === 0)).toBe(true);
  });
});

describe('карточка на числах эпика (c1)', () => {
  it('заголовок «61 % ревью — с первого раза», «отозвано 3», полоса из трёх сегментов', () => {
    const h = html(V132);
    expect(h).toContain('61 % ревью — с первого раза');
    expect(h).toContain('отозвано 3');
    expect(count(h, 'data-testid="returns-seg"')).toBe(3);
    // Порядок и ширина — по числам (flex), цвета — success/warning/danger (макет .seg.*).
    for (const [seg, color] of [['accepted', 'var(--c-success)'], ['remarks', 'var(--c-warning)'], ['returned', 'var(--c-danger)']] as const) {
      const m = h.match(new RegExp(`data-testid="returns-seg" data-seg="${seg}"[^>]*style="([^"]*)"`));
      expect(m, `сегмент «${seg}»`).toBeTruthy();
      expect(m![1]).toContain(`flex:${seg === 'accepted' ? 80 : seg === 'remarks' ? 32 : 20}`);
      expect(m![1]).toContain(`background:${color}`);
    }
    // Полоса описана для скринридера (макет: role=img + aria-label).
    expect(h).toContain('aria-label="Исходы ревью: сразу 80, с замечаниями 32, возвращены 20 из 132"');
  });

  it('легенда — три чипа с числом и процентом, aria-pressed=false в покое', () => {
    const h = html(V132);
    expect(chipTexts(h)).toEqual(['Сразу 80 61 %', 'С замечаниями 32 24 %', 'Возвращены 20 15 %']);
    expect(count(h, 'aria-pressed="false"')).toBe(3);
  });

  // Мутация (2): рисовать нулевые сегменты — краснеет этот тест и «все сразу».
  it('нулевые сегменты в полосе не рисуются (в легенде 0 остаётся значением)', () => {
    const h = html(mk({ accepted: 10, returned: 2 }));
    expect(count(h, 'data-testid="returns-seg"')).toBe(2);
    expect(h).not.toContain('data-seg="remarks"');
    expect(chipTexts(h)).toEqual(['Сразу 10 83 %', 'С замечаниями 0 0 %', 'Возвращены 2 17 %']);
  });

  it('нет отозванных — подписи «отозвано» нет', () => {
    expect(html(mk({ accepted: 5, returned: 5 }))).not.toContain('отозвано');
  });

  // Состояние «все сразу» из записки: заголовок без процента, полоса из одного сегмента.
  it('все сразу: «Все 12 ревью — с первого раза», один сегмент', () => {
    const h = html(mk({ accepted: 12 }));
    expect(h).toContain('Все 12 ревью — с первого раза');
    expect(count(h, 'data-testid="returns-seg"')).toBe(1);
    expect(h).toContain('data-seg="accepted"');
  });

  it('0 ревью: карточки нет, только строка «Ревью ещё не было»', () => {
    const h = html(mk({ withdrawn: 4 }));
    expect(h).toContain('Ревью ещё не было');
    expect(h).not.toContain('data-testid="returns-outcome"');
    expect(h).not.toContain('data-testid="returns-bar"');
  });

  // П. 2 разбора Майи: высота блока не должна расти с числом ревью — значков на виток нет.
  it('число узлов карточки одинаково при 12 и при 300 ревью', () => {
    const nodes = (h: string) => (h.match(/<[a-z]/g) ?? []).length;
    expect(nodes(html(mk({ accepted: 8, acceptedWithRemarks: 2, returned: 2 })))).toBe(
      nodes(html(mk({ accepted: 200, acceptedWithRemarks: 60, returned: 40 }))));
  });
});

describe('фокус по чипу (состояние «Б») (c3)', () => {
  // Мутация (3): не гасить прочие сегменты — краснеет проверка opacity 0.25 ниже.
  it('нажатие подсвечивает свой сегмент (прочие — 0.25), повторное снимает', async () => {
    installFakeDom();
    try {
      const { container } = mount(createElement(FeatureReturnsOutcomeBar, { verdicts: V132 }));
      const chip = findNodeByAttr(container, 'data-testid', 'returns-chip-returned')!;
      expect(chip.getAttribute('aria-pressed'), 'в покое чип не нажат').toBe('false');
      await act(async () => { (reactProps(chip)!.onClick as () => void)(); });
      expect(chip.getAttribute('aria-pressed'), 'первое нажатие').toBe('true');
      expect(findNodeByAttr(container, 'data-testid', 'returns-chip-accepted')!.getAttribute('aria-pressed')).toBe('false');
      const segAcc = findNodeByAttr(container, 'data-seg', 'accepted')!;
      const segRet = findNodeByAttr(container, 'data-seg', 'returned')!;
      expect(String(segAcc.style.opacity), 'прочие сегменты — 0.25').toBe('0.25');
      expect(String(segRet.style.opacity), 'свой сегмент — без приглушения').toBe('1');
      await act(async () => { (reactProps(chip)!.onClick as () => void)(); });
      expect(chip.getAttribute('aria-pressed'), 'повторное нажатие снимает').toBe('false');
      expect(String(segAcc.style.opacity), 'приглушение снято').toBe('1');
    } finally {
      uninstallFakeDom();
    }
  });

  it('mobile: кнопки легенды minHeight 40 во всю ширину, столбиком; aria-pressed переключается повторным нажатием', async () => {
    const h = html(V132, true);
    expect(h).toContain('min-height:40px');
    expect(h).toContain('width:100%');
    expect(h).toContain('flex-direction:column');
    installFakeDom();
    try {
      const { container } = mount(createElement(FeatureReturnsOutcomeBar, { verdicts: V132, isMobile: true }));
      const chip = findNodeByAttr(container, 'data-testid', 'returns-chip-remarks')!;
      expect(chip.getAttribute('aria-pressed')).toBe('false');
      await act(async () => { (reactProps(chip)!.onClick as () => void)(); });
      expect(chip.getAttribute('aria-pressed'), 'первое нажатие').toBe('true');
      await act(async () => { (reactProps(chip)!.onClick as () => void)(); });
      expect(chip.getAttribute('aria-pressed'), 'повторное нажатие снимает').toBe('false');
    } finally {
      uninstallFakeDom();
    }
  });

  it('десктоп: чипы min-height 32 (не мобильные)', () => {
    const h = html(V132);
    expect(h).toContain('min-height:32px');
    expect(h).not.toContain('min-height:40px');
  });
});
