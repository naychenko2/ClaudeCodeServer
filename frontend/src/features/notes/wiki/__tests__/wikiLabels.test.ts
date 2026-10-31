// Тексты отказов фичи «Вики» (S7b-0): каждый код разбора по требованию (analyze /
// analyze-status / ingest/note) имеет свой русский текст — не сырой код и не общий
// статусный fallback. Прямой тест словаря: UI для этих кодов — S7b, его рендер — там.

import { describe, it, expect } from 'vitest';
import { WikiError, type WikiErrorCode } from '../../../../lib/wiki';
import { wikiErrorText } from '../wikiLabels';

describe('тексты кодов разбора по требованию (S7b-0)', () => {
  it('каждый код имеет свой русский текст, отличный от чужих, а не сырой код и не общий fallback', () => {
    const codes = [
      'not_found', 'task_execution', 'marked_skip', 'personal_section',
      'wiki_not_initialized', 'packages_limit', 'wiki_note_id',
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
