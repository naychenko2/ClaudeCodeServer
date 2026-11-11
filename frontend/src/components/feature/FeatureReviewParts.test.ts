// Каркас «Разбора» (FS-FR0). Рендер-тесты на fakeDom: порядок и ключи REVIEW_SECTIONS,
// секция с якорем и заголовком GroupHeader-стиля, «ключ — значение» (null-строки нет),
// ряд якорей (без состояния выбора, клик → один onJump, мобильная высота 40).
// Компоненты без данных: проверяем раскладку и события, не числа.
//
// Мутации (проверены прогоном в копии дерева, 10.11): (1) в ReviewKv `r.value != null` →
// `r.value !== undefined` — краснеют ровно 2 теста: «3 строки…2 строки» и «нет строк
// (или все null)»; (2) в ReviewAnchors кнопкам добавить `ariaPressed={true}` — краснеет
// ровно тест «ни aria-pressed, ни aria-selected»; (3) в FeatureStatsTiles игнорировать
// проп only — краснеют ровно 3 теста describe «only — подмножество плиток»
// (см. FeatureStatsTiles.test.ts), остальные зелёные.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { C, FS, SP, FONT } from '../../lib/design';
import { REVIEW_SECTIONS, ReviewSection, ReviewKv, ReviewAnchors, type ReviewSectionId } from './FeatureReviewParts';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

// Значения атрибута у всех элементов поддерева (для «ни одного aria-pressed/aria-selected»).
function attrDeep(node: unknown, attr: string): string[] {
  const n = node as { nodeType: number; childNodes: unknown[]; getAttribute(a: string): string | null };
  const out: string[] = [];
  if (n.nodeType === 1) {
    const v = n.getAttribute(attr);
    if (v != null) out.push(v);
  }
  for (const c of n.childNodes) out.push(...attrDeep(c, attr));
  return out;
}

function styleOf(node: unknown): Record<string, string> {
  return (node as { style: Record<string, string> }).style;
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('REVIEW_SECTIONS (порядок и ключи)', () => {
  it('шесть секций в порядке макета §5: time, volume, returns, who, spend, history', () => {
    expect(REVIEW_SECTIONS.map((s) => s.id)).toEqual(['time', 'volume', 'returns', 'who', 'spend', 'history']);
    expect(REVIEW_SECTIONS.map((s) => s.label)).toEqual([
      'Время и срок', 'Объём', 'Возвраты и потери', 'Кто работал', 'Расход', 'Хронология',
    ]);
  });
});

describe('ReviewSection', () => {
  it('section с якорем rz-<id> и testid review-sec-<id>, id = ключу REVIEW_SECTIONS', () => {
    const { container } = mount(createElement(ReviewSection, {
      id: 'spend' as ReviewSectionId,
      title: 'Расход',
      children: createElement('div', { 'data-testid': 'sec-body' }, 'тело'),
    }));
    const sec = findNodeByAttr(container, 'data-testid', 'review-sec-spend');
    expect(sec, 'секция не найдена').toBeTruthy();
    expect(sec.getAttribute('id'), 'якорь прокрутки').toBe('rz-spend');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'sec-body'))).toBe('тело');
  });

  it('заголовок — стиль GroupHeader (FS.xs 700, uppercase .04em, muted), счётчика нет; зазор SP.xl', () => {
    const { container } = mount(createElement(ReviewSection, {
      id: 'time' as ReviewSectionId,
      title: 'Время и срок',
      children: null,
    }));
    const sec = findNodeByAttr(container, 'data-testid', 'review-sec-time');
    const head = sec.children[0].children[0];
    expect(textOf(head)).toBe('Время и срок');
    const st = styleOf(head);
    expect(st.fontSize).toBe(`${FS.xs}px`);
    expect(st.fontWeight).toBe('700');
    expect(st.textTransform).toBe('uppercase');
    expect(st.letterSpacing).toBe('0.04em');
    expect(st.color).toBe(C.textMuted);
    expect(st.fontFamily).toBe(FONT.sans);
    // Счётчика нет: в заголовке ровно один текст, никаких mono-чисел.
    expect(sec.children[0].children.length).toBe(1);
    // Зазор между секциями.
    expect(styleOf(sec).marginBottom).toBe(`${SP.xl}px`);
  });
});

describe('ReviewKv', () => {
  it('3 строки, одна value=null → в DOM 2 строки; null-метки в тексте нет', () => {
    const { container } = mount(createElement(ReviewKv, {
      rows: [
        { label: 'Прошло', value: '3 ч' },
        { label: 'Ошибки', value: null },
        { label: 'Ждали тебя', value: '12 мин' },
      ],
    }));
    const kv = findNodeByAttr(container, 'data-testid', 'review-kv');
    expect(kv, 'блок kv не найден').toBeTruthy();
    expect(kv.children.length).toBe(2);
    const all = textOf(kv);
    expect(all).toContain('Прошло');
    expect(all).toContain('Ждали тебя');
    expect(all).not.toContain('Ошибки');
  });

  it('колонки: подпись secondary FS.sm, значение heading FS.sm 600 справа', () => {
    const { container } = mount(createElement(ReviewKv, {
      rows: [{ label: 'Обещали на старте', value: '40 мин' }],
    }));
    const row = findNodeByAttr(container, 'data-testid', 'review-kv-row');
    const label = styleOf(row.children[0]);
    const value = styleOf(row.children[1]);
    expect(label.color).toBe(C.textSecondary);
    expect(label.fontSize).toBe(`${FS.sm}px`);
    expect(value.color).toBe(C.textHeading);
    expect(value.fontSize).toBe(`${FS.sm}px`);
    expect(value.fontWeight).toBe('600');
    expect(value.textAlign).toBe('right');
    // Две колонки в ряд: flex + space-between.
    const rowStyle = styleOf(row);
    expect(rowStyle.display).toBe('flex');
    expect(rowStyle.justifyContent).toBe('space-between');
  });

  it('ReactNode в value рендерится как есть (не только строка)', () => {
    const { container } = mount(createElement(ReviewKv, {
      rows: [{ label: 'Состав времени', value: createElement('b', { 'data-testid': 'kv-rich' }, 'работа 2 ч') }],
    }));
    expect(findNodeByAttr(container, 'data-testid', 'kv-rich'), 'ReactNode не отрендерен').toBeTruthy();
  });

  it('нет строк (или все null) — компонент ничего не рендерит', () => {
    const empty = mount(createElement(ReviewKv, { rows: [] }));
    expect(empty.container.children.length).toBe(0);
    const allNull = mount(createElement(ReviewKv, { rows: [{ label: 'Ошибки', value: null }] }));
    expect(allNull.container.children.length).toBe(0);
  });
});

describe('ReviewAnchors', () => {
  it('6 кнопок в порядке REVIEW_SECTIONS; ни aria-pressed, ни aria-selected', () => {
    const jumps: ReviewSectionId[] = [];
    const { container } = mount(createElement(ReviewAnchors, {
      items: REVIEW_SECTIONS,
      onJump: (id) => { jumps.push(id); },
    }));
    const row = findNodeByAttr(container, 'data-testid', 'review-anchors');
    expect(row, 'ряд якорей не найден').toBeTruthy();
    const btns = row.children;
    expect(btns.length).toBe(6);
    btns.forEach((b, i) => {
      expect(b.getAttribute('data-testid')).toBe(`review-anchor-${REVIEW_SECTIONS[i].id}`);
      expect(textOf(b)).toBe(REVIEW_SECTIONS[i].label);
    });
    // Состояния выбора нет: это переход к месту, а не переключатель.
    expect(attrDeep(row, 'aria-pressed').length).toBe(0);
    expect(attrDeep(row, 'aria-selected').length).toBe(0);
  });

  it('клик «Расход» → ровно один onJump("spend")', () => {
    const jumps: ReviewSectionId[] = [];
    const { container } = mount(createElement(ReviewAnchors, {
      items: REVIEW_SECTIONS,
      onJump: (id) => { jumps.push(id); },
    }));
    const btn = findNodeByAttr(container, 'data-testid', 'review-anchor-spend');
    act(() => { (reactProps(btn)!.onClick as () => void)(); });
    expect(jumps).toEqual(['spend']);
  });

  it('ряд прокручивается сам (overflowX auto, класс без полосы); на 360 якорь ≥ 40 px', () => {
    const { container } = mount(createElement(ReviewAnchors, {
      items: REVIEW_SECTIONS,
      onJump: () => {},
      isMobile: true,
    }));
    const row = findNodeByAttr(container, 'data-testid', 'review-anchors');
    expect(styleOf(row).overflowX).toBe('auto');
    expect(reactProps(row)!.className).toBe('cc-feature-review-anchors');
    // Плитки ряда не сжимаются — скроллится ряд, не страница.
    for (const b of row.children) expect(styleOf(b).flexShrink).toBe('0');
    // Мобильная высота якоря — 40 (макет .w360 .btn.sm.touch).
    for (const b of row.children) expect(styleOf(b).minHeight).toBe('40px');
  });

  it('без сигнала мобилы — высота кита sm (32), пилюля (R.max) на обеих ширинах', () => {
    const { container } = mount(createElement(ReviewAnchors, {
      items: REVIEW_SECTIONS,
      onJump: () => {},
    }));
    const row = findNodeByAttr(container, 'data-testid', 'review-anchors');
    for (const b of row.children) {
      expect(styleOf(b).minHeight).toBe('32px');
      expect(styleOf(b).borderRadius).toBe('999px');
    }
  });
});
