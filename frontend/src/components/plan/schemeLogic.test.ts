// Юнит-тесты для чистой логики разворота плана схемой. Покрывают:
//  • resolveHeading — резолв по паре (anchor, anchorIndex) среди одноимённых,
//  • headingHasDuplicates — то же правило «(N-й)» что в buildPlanFeedback,
//  • sliceSection — нарезка markdown с inline-разметкой внутри заголовков,
//  • stripInlineMarkdown — нормализация для сопоставления DOM ↔ исходник.

import { describe, expect, it } from 'vitest';
import type { Heading } from '../../hooks/useHeadings';
import { resolveHeading, headingHasDuplicates, sliceSection, stripInlineMarkdown } from './schemeLogic';

// Заголовок — минимальный набор полей, нужных логике; el не используется,
// поэтому достаточно пустого объекта с tagName (резолвом занимается сама логика).
function makeHeading(text: string, occurrence: number, level = 2): Heading {
  return { level, text, occurrence, el: { tagName: `H${level}` } as unknown as HTMLElement };
}

describe('resolveHeading', () => {
  const headings = [
    makeHeading('Дизайн', 0),
    makeHeading('Дизайн', 1),
    makeHeading('Тесты', 0),
    makeHeading('Заключение', 0),
  ];

  it('находит первое вхождение по паре (text, occurrence)', () => {
    expect(resolveHeading('Дизайн', 0, headings)?.text).toBe('Дизайн');
    expect(resolveHeading('Дизайн', 0, headings)?.occurrence).toBe(0);
  });

  it('находит ВТОРОЕ вхождение по паре (text, occurrence)', () => {
    // Главный кейс: два «Дизайн» в плане — второй блок должен вести во второй.
    const second = resolveHeading('Дизайн', 1, headings);
    expect(second?.occurrence).toBe(1);
  });

  it('возвращает null, если такого раздела нет', () => {
    expect(resolveHeading('Риски', 0, headings)).toBeNull();
  });

  it('возвращает null, если occurrence не существует', () => {
    // третьего «Дизайн» нет — защита от опечатки в карте
    expect(resolveHeading('Дизайн', 5, headings)).toBeNull();
  });
});

describe('headingHasDuplicates', () => {
  it('один заголовок в плане — дубликатов нет', () => {
    const h = [makeHeading('Тесты', 0)];
    expect(headingHasDuplicates('Тесты', h)).toBe(false);
  });

  it('два одноимённых — подпись показывается', () => {
    const h = [makeHeading('Тесты', 0), makeHeading('Тесты', 1)];
    expect(headingHasDuplicates('Тесты', h)).toBe(true);
  });

  it('другой текст не считается дубликатом', () => {
    const h = [makeHeading('Тесты', 0), makeHeading('Дизайн', 0)];
    expect(headingHasDuplicates('Тесты', h)).toBe(false);
  });
});

describe('stripInlineMarkdown', () => {
  it('снимает backticks', () => {
    expect(stripInlineMarkdown('Шаг — `код`')).toBe('Шаг — код');
  });

  it('снимает **жирный** и __жирный__', () => {
    expect(stripInlineMarkdown('**важно** и __тоже__')).toBe('важно и тоже');
  });

  it('снимает *курсив* и _курсив_', () => {
    expect(stripInlineMarkdown('*раз* и _два_')).toBe('раз и два');
  });

  it('снимает [link](url)', () => {
    expect(stripInlineMarkdown('смотри [раздел X](#x)')).toBe('смотри раздел X');
  });

  it('снимает autolink', () => {
    expect(stripInlineMarkdown('см <https://example.com>')).toBe('см https://example.com');
  });

  it('оставляет обычный текст как есть', () => {
    expect(stripInlineMarkdown('просто текст')).toBe('просто текст');
  });
});

describe('sliceSection', () => {
  it('находит простой раздел и режет до следующего того же уровня', () => {
    const plan = [
      '# План',
      '',
      '## Введение',
      '',
      'текст введения',
      '',
      '## Детали',
      '',
      'текст деталей',
      '',
    ].join('\n');
    const headings = [makeHeading('Введение', 0)];
    const sec = sliceSection(plan, headings[0]);
    expect(sec).toContain('## Введение');
    expect(sec).toContain('текст введения');
    expect(sec).not.toContain('## Детали');
    expect(sec).not.toContain('текст деталей');
  });

  it('режет до заголовка БОЛЕЕ высокого уровня', () => {
    const plan = [
      '# План',
      '',
      '## Шаг 1',
      '',
      'детали шага',
      '',
      '## Шаг 2',
      '',
      'детали шага 2',
    ].join('\n');
    const headings = [makeHeading('Шаг 1', 0)];
    const sec = sliceSection(plan, headings[0]);
    expect(sec).toContain('## Шаг 1');
    expect(sec).toContain('детали шага');
    expect(sec).not.toContain('Шаг 2');
  });

  it('НЕ режет заголовок БОЛЕЕ низкого уровня (h3 внутри h2)', () => {
    const plan = [
      '## Главный',
      '',
      'текст главного',
      '',
      '### Подпункт',
      '',
      'текст подпункта',
      '',
      '## Следующий',
      '',
      'другой раздел',
    ].join('\n');
    const headings = [makeHeading('Главный', 0, 2)];
    const sec = sliceSection(plan, headings[0]);
    expect(sec).toContain('### Подпункт');
    expect(sec).not.toContain('## Следующий');
  });

  it('сопоставляет заголовок с inline-кодом через нормализацию', () => {
    // Главный кейс Киры: в исходнике `Шаг — \`код\``, в DOM «Шаг — код».
    const plan = [
      '## Шаг — `код`',
      '',
      'текст',
      '',
      '## Дальше',
      '',
      'прочее',
    ].join('\n');
    const headings = [makeHeading('Шаг — код', 0, 2)];
    const sec = sliceSection(plan, headings[0]);
    expect(sec).toContain('## Шаг — `код`');
    expect(sec).toContain('текст');
    expect(sec).not.toContain('## Дальше');
  });

  it('сопоставляет заголовок с inline-жирным через нормализацию', () => {
    const plan = [
      '## **Важный** раздел',
      '',
      'содержимое',
    ].join('\n');
    const headings = [makeHeading('Важный раздел', 0, 2)];
    const sec = sliceSection(plan, headings[0]);
    expect(sec).toContain('**Важный** раздел');
  });

  it('возвращает пустую строку, если заголовок не найден', () => {
    const plan = '# Другой план\n\n## Другой раздел';
    const headings = [makeHeading('Несуществующий', 0, 2)];
    expect(sliceSection(plan, headings[0])).toBe('');
  });

  it('берёт раздел до конца плана, если дальше ничего нет', () => {
    const plan = [
      '## Последний',
      '',
      'хвост',
    ].join('\n');
    const headings = [makeHeading('Последний', 0, 2)];
    const sec = sliceSection(plan, headings[0]);
    expect(sec).toContain('## Последний');
    expect(sec).toContain('хвост');
  });

  it('режет ВТОРОЕ одноимённое вхождение по паре (text, occurrence)', () => {
    // Главный кейс починки: при двух «Дизайн» срез для occurrence=1
    // должен вернуть тело ВТОРОГО раздела, а не первого.
    const plan = [
      '# План',
      '',
      '## Дизайн',
      '',
      'тело первого дизайна',
      '',
      '## Тесты',
      '',
      'первые тесты',
      '',
      '## Дизайн',
      '',
      'тело второго дизайна',
      '',
      '## Заключение',
      '',
      'итог',
    ].join('\n');
    const headings = [
      makeHeading('Дизайн', 0, 2),
      makeHeading('Тесты', 0, 2),
      makeHeading('Дизайн', 1, 2),
      makeHeading('Заключение', 0, 2),
    ];
    const second = sliceSection(plan, headings[2]);
    expect(second).toContain('тело второго дизайна');
    expect(second).not.toContain('тело первого дизайна');
    expect(second).not.toContain('первые тесты');
    expect(second).not.toContain('итог');
  });

  it('возвращает пустую строку, если occurrence выходит за число вхождений', () => {
    const plan = [
      '## Дизайн',
      '',
      'единственное тело',
    ].join('\n');
    expect(sliceSection(plan, makeHeading('Дизайн', 5, 2))).toBe('');
  });
});