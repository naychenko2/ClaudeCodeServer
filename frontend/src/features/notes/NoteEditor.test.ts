// Хвост S0 (фронт): редактор не подсвечивает [[…]] и #тег внутри кода и не считает
// чисто числовой #807 тегом — тот же контракт, что у бэкенда (NotesService.CodeMask +
// «числовой тег — не тег»). Окружение node: проверяется чистая выборка меток на EditorState,
// без DOM-редактора.

import { describe, it, expect } from 'vitest';
import { EditorState } from '@codemirror/state';
import { ensureSyntaxTree } from '@codemirror/language';
import { markdown } from '@codemirror/lang-markdown';
import { findNoteMarks } from './NoteEditor';

function marks(doc: string) {
  const state = EditorState.create({ doc, extensions: [markdown()] });
  // Без редактора дерево ещё не разобрано — доводим до конца документа (в живом
  // редакторе его уже разобрал соседний ViewPlugin подсветки).
  ensureSyntaxTree(state, state.doc.length, 5000);
  return findNoteMarks(state, 0, state.doc.length);
}

const wikiTargets = (doc: string) => marks(doc).wikis.map(w => w.target);
const tagTexts = (doc: string) => marks(doc).tags.map(t => doc.slice(t.from, t.to));

describe('NoteEditor: метки только вне кода', () => {
  it('обычные ссылка и тег подсвечиваются (положительный контроль)', () => {
    expect(wikiTargets('См. [[Заметка]] и [[Другая|подпись]].')).toEqual(['Заметка', 'Другая|подпись']);
    expect(tagTexts('Теги: #работа и #проект/дом')).toEqual(['#работа', '#проект/дом']);
  });

  it('[[…]] внутри inline-кода — не ссылка', () => {
    expect(wikiTargets('Пример: `[[Заметка]]`')).toEqual([]);
  });

  it('[[…]] внутри ```-забора — не ссылка', () => {
    expect(wikiTargets(['```md', '[[Заметка]]', '```'].join('\n'))).toEqual([]);
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

  it('смешанный документ: живые метки вне кода, кодовые — нет', () => {
    const doc = ['См. [[Живая]] и #живой.', '', '```', '[[ВКоде]] #вКоде', '```', '', 'И `[[Inline]] #inline`.'].join('\n');
    expect(wikiTargets(doc)).toEqual(['Живая']);
    expect(tagTexts(doc)).toEqual(['#живой']);
  });
});
