import { describe, it, expect } from 'vitest';
import { collectHeadings } from '../../hooks/useHeadings';
import { anchorForHeadingEl, headingForRange, PLAN_GENERAL_HEADING } from './PlanRemarks';

// Заголовок-заглушка вместо настоящего <h*>: прогон идёт в окружении node, а
// collectHeadings трогает у узла только tagName, textContent и querySelector
function h(tag: string, text: string): HTMLElement {
  return {
    tagName: tag,
    textContent: text,
    querySelector: () => null,
  } as unknown as HTMLElement;
}

// Минимальный узел-потомок контейнера плана: реальный DOM не нужен — алгоритм
// резолва заголовка ходит только по parentElement и previousElementSibling, и
// никаких других API не вызывает.
type NodeStub = {
  tagName?: string;
  parentElement: ElementStub | null;
  previousElementSibling: ElementStub | null;
  nodeType: number;
};
type ElementStub = NodeStub & { tagName: string };

function elem(tag: string, parent: ElementStub | null, prev: ElementStub | null): ElementStub {
  return { tagName: tag, parentElement: parent, previousElementSibling: prev, nodeType: 1 };
}
function text(parent: ElementStub): NodeStub {
  return { parentElement: parent, previousElementSibling: null, nodeType: 3 };
}
// Range-обёртка: алгоритму нужен только startContainer; остальное Range-API
// тест-раннер не вызывает
function rangeOf(start: NodeStub): Range {
  return { startContainer: start } as unknown as Range;
}

describe('якорь замечания к разделу плана', () => {
  // Два «Тесты» разнесены другими разделами: DOM-позиция второго — 3, а номер
  // среди одноимённых — 1
  const nodes = [
    h('H2', 'Цель'),
    h('H2', 'Тесты'),
    h('H2', 'Реализация'),
    h('H2', 'Тесты'),
  ];
  const headings = collectHeadings(nodes);

  it('выделение во втором одноимённом разделе даёт occurrence 1, а не позицию в списке', () => {
    expect(anchorForHeadingEl(nodes[3], headings)).toEqual({ heading: 'Тесты', occurrence: 1 });
    expect(anchorForHeadingEl(nodes[1], headings)).toEqual({ heading: 'Тесты', occurrence: 0 });
  });

  it('маркер у заголовка и выделение в нём дают один и тот же якорь', () => {
    const marker = headings[3];   // путь «маркер у заголовка» берёт h.text/h.occurrence
    const selection = anchorForHeadingEl(nodes[3], headings);
    expect(selection).toEqual({ heading: marker.text, occurrence: marker.occurrence });
  });

  it('узел вне оглавления якоря не даёт', () => {
    expect(anchorForHeadingEl(h('H2', 'Чужой'), headings)).toBeNull();
    expect(anchorForHeadingEl(null, headings)).toBeNull();
  });
});

describe('headingForRange: ближайший заголовок над выделением', () => {
  // Плоская структура, как рендерит ReactMarkdown: h2 и p — соседи, не вложены.
  // <h2>Проверка</h2>
  // <p>текст раздела</p>
  // <ul>...</ul>
  // <h2>Приёмка</h2>
  // <p>текст приёмки</p>
  const root = elem('DIV', null, null);
  const hCheck = elem('H2', root, null);
  const pCheck = elem('P', root, hCheck);
  const ulCheck = elem('UL', root, pCheck);
  const hAccept = elem('H2', root, ulCheck);
  const pAccept = elem('P', root, hAccept);

  it('выделение в <p> второго раздела находит второй <h2>, а не null', () => {
    const t = text(pAccept);
    const r = rangeOf(t);
    expect(headingForRange(root as unknown as HTMLElement, r)).toBe(hAccept);
  });

  it('выделение в <ul> второго раздела идёт к предыдущему заголовку', () => {
    const t = text(ulCheck);
    const r = rangeOf(t);
    expect(headingForRange(root as unknown as HTMLElement, r)).toBe(hCheck);
  });

  it('выделение в <p> первого раздела находит первый <h2>', () => {
    const t = text(pCheck);
    const r = rangeOf(t);
    expect(headingForRange(root as unknown as HTMLElement, r)).toBe(hCheck);
  });

  it('выделение выше первого заголовка возвращает null — общий якорь', () => {
    const t = text(elem('P', root, null));   // шапка плана до первого h2
    const r = rangeOf(t);
    expect(headingForRange(root as unknown as HTMLElement, r)).toBeNull();
  });
});

describe('общий якорь PLAN_GENERAL_HEADING', () => {
  // Если headingForRange не нашёл заголовок над выделением — замечание
  // привязывается к этому якоре-заглушке, а не теряется молча
  it('попадает в отдельную группу в обратной связи и идёт ПОСЛЕ разделов', async () => {
    const { buildPlanFeedback } = await import('./buildPlanFeedback');
    const out = buildPlanFeedback(
      [
        { anchorHeading: PLAN_GENERAL_HEADING, text: 'в целом — не хватает дедлайна' },
        { anchorHeading: 'Тесты', anchorIndex: 0, text: 'добавить пример' },
        { anchorHeading: 'Цель', anchorIndex: 0, text: 'расплывчато' },
      ],
      ['Цель', 'Тесты'],
    );
    // Общий якорь — НЕ раздел, оборачивать в «Раздел «…»» нельзя:
    // планировщик прочёл бы это как имя раздела и пошёл искать его в документе
    expect(out).toContain(`${PLAN_GENERAL_HEADING} → в целом — не хватает дедлайна`);
    expect(out).not.toContain(`Раздел «${PLAN_GENERAL_HEADING}»`);
    // У реальных заголовков формат прежний
    expect(out).toContain('Раздел «Тесты» → добавить пример');
    expect(out).toContain('Раздел «Цель» → расплывчато');
    // Порядок: сначала разделы по headingOrder, общий якорь — в хвосте
    expect(out.indexOf('Раздел «Цель»')).toBeLessThan(out.indexOf('Раздел «Тесты»'));
    expect(out.indexOf('Раздел «Тесты»')).toBeLessThan(out.indexOf(PLAN_GENERAL_HEADING));
  });
});
