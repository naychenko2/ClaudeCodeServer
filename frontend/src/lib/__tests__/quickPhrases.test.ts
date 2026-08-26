import { describe, it, expect } from 'vitest';
import { flattenSections, groupQuickPhrases, movePhrase, toSections } from '../quickPhrases';

// Перестановка фраз в форме набора: порядок строк = порядок в попапе композера
describe('movePhrase', () => {
  const list = ['a', 'b', 'c'];

  it('меняет строку местами с соседом сверху', () => {
    expect(movePhrase(list, 1, -1)).toEqual(['b', 'a', 'c']);
  });

  it('меняет строку местами с соседом снизу', () => {
    expect(movePhrase(list, 1, 1)).toEqual(['a', 'c', 'b']);
  });

  it('на границах списка возвращает исходный порядок', () => {
    expect(movePhrase(list, 0, -1)).toEqual(list);
    expect(movePhrase(list, 2, 1)).toEqual(list);
  });

  it('не мутирует исходный массив', () => {
    const src = ['a', 'b'];
    movePhrase(src, 0, 1);
    expect(src).toEqual(['a', 'b']);
  });
});

// Раскладка набора по уровням попапа: корень и группы
describe('groupQuickPhrases', () => {
  it('делит фразы на корневые и группы, сохраняя порядок набора', () => {
    const { root, groups } = groupQuickPhrases([
      { text: 'продолжай' },
      { text: 'закоммить', group: 'ГИТ' },
      { text: 'статус', group: 'Задачи' },
      { text: 'дифф', group: 'ГИТ' },
    ]);

    expect(root.map(p => p.text)).toEqual(['продолжай']);
    expect(groups.map(g => g.name)).toEqual(['ГИТ', 'Задачи']);
    // Фразы группы собираются вместе, даже если в наборе лежат вразнобой
    expect(groups[0].phrases.map(p => p.text)).toEqual(['закоммить', 'дифф']);
  });

  it('пустую и пробельную группу считает отсутствием группы', () => {
    const { root, groups } = groupQuickPhrases([
      { text: 'а', group: '' },
      { text: 'б', group: '   ' },
      { text: 'в', group: null },
    ]);

    expect(root.map(p => p.text)).toEqual(['а', 'б', 'в']);
    expect(groups).toHaveLength(0);
  });

  it('имя группы берёт без крайних пробелов', () => {
    const { groups } = groupQuickPhrases([
      { text: 'а', group: ' ГИТ ' },
      { text: 'б', group: 'ГИТ' },
    ]);

    expect(groups).toHaveLength(1);
    expect(groups[0].name).toBe('ГИТ');
  });
});

// Секции формы правки: набор ↔ секции. Форма ведёт список секциями, потому что
// попап двухуровневый — плоский список врал бы про порядок
describe('секции формы', () => {
  let n = 0;
  const ids = () => `id-${++n}`;

  it('toSections кладёт корень первым и разводит группы', () => {
    const sections = toSections([
      { text: 'продолжай' },
      { text: 'закоммить', group: 'ГИТ' },
      { text: 'дифф', group: 'ГИТ' },
    ], ids);

    expect(sections.map(s => s.name)).toEqual([null, 'ГИТ']);
    expect(sections[0].rows.map(r => r.text)).toEqual(['продолжай']);
    expect(sections[1].rows.map(r => r.text)).toEqual(['закоммить', 'дифф']);
    // id у строк стабильные и разные — по ним React держит фокус при перестановке
    const rowIds = sections.flatMap(s => s.rows.map(r => r.id));
    expect(new Set(rowIds).size).toBe(rowIds.length);
  });

  it('пустой набор даёт одну корневую секцию без строк', () => {
    const sections = toSections([], ids);
    expect(sections).toHaveLength(1);
    expect(sections[0].name).toBeNull();
    expect(sections[0].rows).toEqual([]);
  });

  it('flattenSections собирает плоский набор в порядке секций', () => {
    const flat = flattenSections([
      { id: 'a', name: null, rows: [{ id: '1', text: 'продолжай' }] },
      { id: 'b', name: 'ГИТ', rows: [{ id: '2', text: 'закоммить' }, { id: '3', text: 'дифф' }] },
    ]);

    expect(flat).toEqual([
      { text: 'продолжай' },
      { text: 'закоммить', group: 'ГИТ' },
      { text: 'дифф', group: 'ГИТ' },
    ]);
  });

  it('flattenSections выбрасывает пустые строки и обрезает пробелы', () => {
    const flat = flattenSections([
      { id: 'a', name: null, rows: [{ id: '1', text: '  продолжай  ' }, { id: '2', text: '   ' }] },
      { id: 'b', name: ' ГИТ ', rows: [{ id: '3', text: 'дифф' }] },
    ]);

    expect(flat).toEqual([{ text: 'продолжай' }, { text: 'дифф', group: 'ГИТ' }]);
  });

  it('секция без непустых строк не попадает в набор', () => {
    const flat = flattenSections([
      { id: 'a', name: null, rows: [] },
      { id: 'b', name: 'Пустая', rows: [{ id: '1', text: '' }] },
    ]);

    expect(flat).toEqual([]);
  });

  it('набор переживает круг «в секции и обратно»', () => {
    const src = [
      { text: 'продолжай' },
      { text: 'закоммить', group: 'ГИТ' },
      { text: 'что в работе', group: 'Задачи' },
      { text: 'дифф', group: 'ГИТ' },
    ];
    const back = flattenSections(toSections(src, ids));

    // Фразы группы собрались вместе — ровно так их и показывает попап
    expect(back).toEqual([
      { text: 'продолжай' },
      { text: 'закоммить', group: 'ГИТ' },
      { text: 'дифф', group: 'ГИТ' },
      { text: 'что в работе', group: 'Задачи' },
    ]);
  });
});
