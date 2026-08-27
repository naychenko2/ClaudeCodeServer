import { describe, it, expect } from 'vitest';
import { buildPlanFeedback, PLAN_FEEDBACK_FOOTER } from './buildPlanFeedback';

// Контракт «Разделы без замечаний согласованы»: одни и те же слова независимо от
// реализации — их видит и планировщик, и пользователь, и тест на согласованную
// картину ответа
const FOOTER = PLAN_FEEDBACK_FOOTER;

describe('buildPlanFeedback', () => {
  it('пустой список замечаний → только строка о согласованных разделах', () => {
    expect(buildPlanFeedback([], [])).toBe(FOOTER);
  });

  it('одно замечание без цитаты — заголовок, текст и подвал', () => {
    const out = buildPlanFeedback(
      [{ anchorHeading: 'Контекст', text: 'Уточнить вводные.' }],
      ['Контекст'],
    );
    expect(out).toBe(`Раздел «Контекст» → Уточнить вводные.\n\n${FOOTER}`);
  });

  it('замечание с цитатой добавляет строку «> цитата» после «заголовок → текст»', () => {
    const out = buildPlanFeedback(
      [
        {
          anchorHeading: 'Решение',
          text: 'не сходится с ADR-007',
          quote: 'выбираем DeepSeek',
        },
      ],
      ['Решение'],
    );
    expect(out).toBe(
      `Раздел «Решение» → не сходится с ADR-007\n> выбираем DeepSeek\n\n${FOOTER}`,
    );
  });

  it('два замечания на разных разделах идут в порядке плана', () => {
    const out = buildPlanFeedback(
      [
        { anchorHeading: 'Проверка', text: 'добавить интеграционный сценарий' },
        { anchorHeading: 'Контекст', text: 'ссылка на источник' },
      ],
      ['Контекст', 'Проверка'],
    );
    expect(out).toBe(
      [
        'Раздел «Контекст» → ссылка на источник',
        '',
        'Раздел «Проверка» → добавить интеграционный сценарий',
        '',
        FOOTER,
      ].join('\n'),
    );
  });

  it('два замечания на одном разделе идут подряд, через пустую строку', () => {
    const out = buildPlanFeedback(
      [
        { anchorHeading: 'Границы', text: 'не упомянули мобилу', quote: 'узкое место' },
        { anchorHeading: 'Границы', text: 'что с локальной моделью?' },
      ],
      ['Границы'],
    );
    expect(out).toBe(
      [
        'Раздел «Границы» → не упомянули мобилу',
        '> узкое место',
        '',
        'Раздел «Границы» → что с локальной моделью?',
        '',
        FOOTER,
      ].join('\n'),
    );
  });

  it('замечание на отсутствующий в плане заголовок попадает в конец', () => {
    const out = buildPlanFeedback(
      [
        { anchorHeading: 'Известный', text: 'ок' },
        { anchorHeading: 'Опечатка', text: 'что это?' },
      ],
      ['Известный'],
    );
    expect(out).toBe(
      [
        'Раздел «Известный» → ок',
        '',
        'Раздел «Опечатка» → что это?',
        '',
        FOOTER,
      ].join('\n'),
    );
  });

  it('разделы без замечаний упоминаются только подвалом', () => {
    // Явное покрытие требования: «разделы без замечаний попадают в строку согласованы»
    const out = buildPlanFeedback(
      [{ anchorHeading: 'Только этот', text: 'правка' }],
      ['Не тронут 1', 'Только этот', 'Не тронут 2'],
    );
    expect(out).toContain(FOOTER);
    expect(out).not.toContain('Не тронут 1');
    expect(out).not.toContain('Не тронут 2');
    expect(out).toContain('Только этот');
  });

  it('пустая цитата после trim не добавляется в вывод', () => {
    const out = buildPlanFeedback(
      [{ anchorHeading: 'Шапка', text: 'правка', quote: '   ' }],
      ['Шапка'],
    );
    expect(out).toBe(`Раздел «Шапка» → правка\n\n${FOOTER}`);
    expect(out).not.toContain('>');
  });
});
