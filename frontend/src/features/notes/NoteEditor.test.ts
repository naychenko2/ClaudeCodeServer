// Хвост S0 (фронт): редактор не подсвечивает [[…]] и #тег внутри кода и не считает
// чисто числовой #807 тегом — тот же контракт, что у бэкенда (NotesService.CodeMask +
// «числовой тег — не тег»). «Код» редактор определяет той же маской, что вьюер
// (codeMask из MarkdownViewer), а не деревом lezer: расхождений правил InlineCode/
// FencedCode и бэкенда здесь быть не должно. Окружение node: проверяется чистая
// выборка меток на EditorState, без DOM-редактора; синтакс-дерево не нужно вовсе.

import { describe, it, expect, vi } from 'vitest';
import { EditorState } from '@codemirror/state';
import { findNoteMarks } from './NoteEditor';

// Счётчик реальных вызовов маски: доказательство кэша (тот же текст — один расчёт,
// новый текст — пересчёт). Поведение mock'а — настоящая функция, обёрнута только счётчиком.
let maskCalls = 0;
vi.mock('../../components/MarkdownViewer', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../components/MarkdownViewer')>();
  return { ...actual, codeMask: (md: string) => { maskCalls++; return actual.codeMask(md); } };
});
const { codeMask } = await import('../../components/MarkdownViewer');

function marks(doc: string) {
  const state = EditorState.create({ doc });
  return findNoteMarks(state, 0, state.doc.length);
}

const wikiTargets = (doc: string) => marks(doc).wikis.map(w => w.target);
const tagTexts = (doc: string) => marks(doc).tags.map(t => doc.slice(t.from, t.to));

describe('NoteEditor: метки только вне кода (маска бэкенда, как у вьюера)', () => {
  it('обычные ссылка и тег подсвечиваются (положительный контроль)', () => {
    expect(wikiTargets('См. [[Заметка]] и [[Другая|подпись]].')).toEqual(['Заметка', 'Другая|подпись']);
    expect(tagTexts('Теги: #работа и #проект/дом')).toEqual(['#работа', '#проект/дом']);
  });

  it('[[…]] внутри inline-кода — не ссылка', () => {
    expect(wikiTargets('Пример: `[[Заметка]]`')).toEqual([]);
  });

  it('inline-код с НЕРАВНЫМИ длинами бэктиков — тоже код (правило бэкенда, как у вьюера)', () => {
    // lezer (CommonMark) открывает inline-код только равными бэктиками и подсвечивал
    // бы ссылку; бэкенд (`[^`\n]+`) и вьюер считают этот текст кодом — здесь то же самое.
    expect(wikiTargets('`` [[Живая]]`')).toEqual([]);
    expect(wikiTargets('` [[Живая]]``')).toEqual([]);
    // равные длины — код и раньше, и теперь (контроль совпадения)
    expect(wikiTargets('`` [[Живая]]``')).toEqual([]);
    expect(wikiTargets('` [[Живая]]`')).toEqual([]);
  });

  it('забор внутри цитаты — по бэкенду НЕ забор: ссылка живая', () => {
    // lezer признавал FencedCode в цитате и глушил ссылку; бэкенд сканирует построчно
    // (MarkdownFence) и «> ```» забором не считает — здесь та же граница.
    expect(wikiTargets(['> ```', '> [[Живая]]', '> ```'].join('\n'))).toEqual(['Живая']);
  });

  it('[[…]] внутри ```-забора — не ссылка', () => {
    expect(wikiTargets(['```md', '[[Заметка]]', '```'].join('\n'))).toEqual([]);
  });

  it('[[…]] внутри ~~~-забора — не ссылка', () => {
    expect(wikiTargets(['~~~', '[[Заметка]]', '~~~'].join('\n'))).toEqual([]);
  });

  it('[[…]] в незакрытом заборе — не ссылка', () => {
    expect(wikiTargets(['```', '[[Заметка]]'].join('\n'))).toEqual([]);
  });

  it('#тег внутри кода — не тег', () => {
    expect(tagTexts('В коде `#работа` и\n```\n#проект\n```')).toEqual([]);
  });

  it('чисто числовой тег — не тег; цифро-буквенный — тег (как на бэкенде)', () => {
    // eslint-disable-next-line design/no-raw-color -- здесь это текст тега, не цвет
    expect(tagTexts('см. #807 в тексте')).toEqual([]);
    // eslint-disable-next-line design/no-raw-color -- здесь это текст тега, не цвет
    expect(tagTexts('#1abc')).toEqual(['#1abc']);
  });

  it('«числовой» — только десятичные цифры (char.IsDigit = \\p{Nd}): #² — тег', () => {
    // прежний \p{N} глушил и No-символы (², ½); бэкенд (it.All(char.IsDigit)) считает
    // числом только Nd — «²» для него текст тега.
    expect(tagTexts('степень #² — тег')).toEqual(['#²']);
    expect(tagTexts('половина #½ тоже тег')).toEqual(['#½']);
  });

  it('смешанный документ: живые метки вне кода, кодовые — нет', () => {
    const doc = ['См. [[Живая]] и #живой.', '', '```', '[[ВКоде]] #вКоде', '```', '', 'И `[[Inline]] #inline`.'].join('\n');
    expect(wikiTargets(doc)).toEqual(['Живая']);
    expect(tagTexts(doc)).toEqual(['#живой']);
  });
});

describe('NoteEditor: маска кэшируется по версии текста', () => {
  it('тот же текст — один расчёт маски; изменённый текст — пересчёт', () => {
    const doc = '[[Живая]] `[[ВКоде]]`';
    const state = EditorState.create({ doc });
    maskCalls = 0;
    const first = findNoteMarks(state, 0, doc.length);
    expect(first.wikis.map(w => w.target)).toEqual(['Живая']);
    expect(maskCalls).toBe(1);
    // повторные вызовы на том же документе — из кэша, нового расчёта нет
    findNoteMarks(state, 0, doc.length);
    findNoteMarks(state, 0, 5);
    expect(maskCalls).toBe(1);
    // новая версия текста — маска пересчитывается (старый кэш не держим): бэктик в начале
    // сдвигает пары inline-кода, «Живая» заходит в код, «ВКоде» из него выходит.
    // Устаревший кэш дал бы прежний результат ['Живая'] — здесь проверяется пересчёт.
    const edited = EditorState.create({ doc: '`' + doc });
    const second = findNoteMarks(edited, 0, edited.doc.length);
    expect(maskCalls).toBe(2);
    expect(second.wikis.map(w => w.target)).toEqual(['ВКоде']);
  });

  it('1 МБ: расчёт маски укладывается в ~120 мс, метки линейны', () => {
    const line = (i: number) => `заполнение строки ${'x'.repeat(40)} [[Заметка${i}]] #tag${i}`;
    const big = Array.from({ length: 18000 }, (_, i) => line(i)).join('\n');
    expect(big.length).toBeGreaterThan(1_000_000);
    const state = EditorState.create({ doc: big });

    const tMask0 = performance.now();
    codeMask(big);
    const tMask = performance.now() - tMask0;

    const t10 = performance.now();
    const r = findNoteMarks(state, 0, state.doc.length);
    const t1 = performance.now() - t10;
    const t20 = performance.now();
    findNoteMarks(state, 0, state.doc.length);
    const t2 = performance.now() - t20;
    console.log(`[perf] 1 МБ (${big.length} симв.): маска ${tMask.toFixed(1)} мс, ` +
      `первый findNoteMarks ${t1.toFixed(1)} мс, повторный (кэш) ${t2.toFixed(1)} мс`);

    expect(r.wikis.length).toBe(18000);
    expect(r.tags.length).toBe(18000);
    expect(tMask).toBeLessThan(120);
    // повторный вызов не пересчитывает маску (см. счётчик выше) и потому заметно легче
    expect(t2).toBeLessThan(t1);
  });
});
