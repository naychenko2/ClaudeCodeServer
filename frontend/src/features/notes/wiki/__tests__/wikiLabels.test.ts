// Тексты отказов фичи «Вики» (S7b-0): каждый код разбора по требованию (analyze /
// analyze-status / ingest/note) имеет свой русский текст — не сырой код и не общий
// статусный fallback. Прямой тест словаря: UI для этих кодов — S7b, его рендер — там.

import { describe, it, expect } from 'vitest';
import { WikiError, type WikiErrorCode } from '../../../../lib/wiki';
import { wikiErrorText } from '../wikiLabels';

describe('тексты кодов разбора по требованию (S7b-0)', () => {
  it('каждый код имеет свой русский текст, а не сырой код и не общий fallback', () => {
    const codes = [
      'not_found', 'task_execution', 'marked_skip', 'personal_section',
      'wiki_not_initialized', 'packages_limit', 'wiki_note_id',
    ];
    for (const code of codes) {
      const text = wikiErrorText(new WikiError(409, code as WikiErrorCode, 'x'));
      expect(text).toBeTruthy();
      expect(text).not.toBe(code);
      // своего текста нет → wikiErrorText отдаёт общий 409-fallback — это и есть провал
      expect(text).not.toBe('Действие отказано');
    }
  });
});
