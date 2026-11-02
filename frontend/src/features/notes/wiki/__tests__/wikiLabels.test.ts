// Тексты отказов фичи «Вики» (S7b-0): каждый код разбора по требованию (analyze /
// analyze-status / ingest/note) имеет свой русский текст — не сырой код и не общий
// статусный fallback; текст not_found выбирается по контексту вызова, а не по коду
// (W4-3, хвост R-S7b-0 H4). Прямой тест словаря: UI для этих кодов — S7b, его рендер — там.
// Согласование по числу (countWord, W4-3): формы для чисел с краевыми случаями
// правил (11–14 как 5+, 21 как 1, 22 как 2).

import { describe, it, expect } from 'vitest';
import { WikiError, type WikiErrorCode } from '../../../../lib/wiki';
import { countWord, wikiErrorText } from '../wikiLabels';

describe('тексты кодов разбора по требованию (S7b-0)', () => {
  it('каждый код имеет свой русский текст, отличный от чужих, а не сырой код и не общий fallback', () => {
    const codes = [
      'not_found', 'task_execution', 'marked_skip', 'personal_section',
      'wiki_not_initialized', 'packages_limit', 'wiki_note_id',
      // хвост ревью R-S7c-2: общий словарь должен покрывать и эти коды
      'wiki_llm_wiki_off', 'wiki_class_key', 'wiki_triage_invalid',
    ];
    const texts = codes.map(code => wikiErrorText(new WikiError(409, code as WikiErrorCode, 'x')));
    codes.forEach((code, i) => {
      const text = texts[i];
      expect(text).toBeTruthy();
      expect(text).not.toBe(code);
      // своего текста нет → wikiErrorText отдаёт общий 409-fallback — это и есть провал
      expect(text).not.toBe('Действие отказано');
    });
    // попарная различность (доработка H1): подмена текста одного кода текстом другого —
    // провал, отдельная проверка каждого кода его не видит
    expect(new Set(texts).size).toBe(codes.length);
  });
});

describe('not_found — текст по контексту вызова (W4-3, хвост R-S7b-0 H4)', () => {
  it('чат и заметка — разные тексты; без контекста — нейтральный, не любой из них', () => {
    // бэк даёт один код для двух разных отказов: analyze — чужой/несуществующий чат
    // (WikiChatIngestService.cs:95-100), ingest/note — заметка не стала источником
    // (WikiController.cs:290-291)
    const e = new WikiError(404, 'not_found', 'x');
    const chat = wikiErrorText(e, 'chat');
    const note = wikiErrorText(e, 'note');
    expect(chat).toBe('Чат не найден');
    expect(note).toBe('Заметка не стала источником');
    // мутация «один текст на оба» краснит эту проверку
    expect(chat).not.toBe(note);
    const plain = wikiErrorText(e);
    expect(plain).not.toBe(chat);
    expect(plain).not.toBe(note);
  });
});

describe('согласование по числу (countWord, W4-3)', () => {
  it('1/2/5/11/12/14/21/22/25 — верные формы, в т.ч. 11–14 как 5+, 21 как 1, 22 как 2', () => {
    const cases: Array<[number, string]> = [
      [1, '1 пакет'], [2, '2 пакета'], [5, '5 пакетов'],
      [11, '11 пакетов'], [12, '12 пакетов'], [14, '14 пакетов'],
      [21, '21 пакет'], [22, '22 пакета'], [25, '25 пакетов'],
    ];
    for (const [n, want] of cases) {
      expect(countWord(n, 'пакет', 'пакета', 'пакетов')).toBe(want);
    }
  });
});

describe('текст wiki_triage_invalid (R-W4-1)', () => {
  it('файл разбора чатов не читается — русским текстом, не общим fallback', () => {
    const t = wikiErrorText(new WikiError(409, 'wiki_triage_invalid', 'x'));
    expect(t).toBe('Файл разбора чатов не читается');
  });
});
