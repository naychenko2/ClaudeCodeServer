// Текст «не сообщает» (NotReported) — общий helper фичи (format.ts): им берут и
// вкладка «LLM», и график «Нагрузка и кэш». Без имени раньше было «Движок движок
// это не сообщает» (повтор слова).

import { describe, expect, it } from 'vitest';
import { notReportedText } from './format';

describe('notReportedText — «Движок … это не сообщает»', () => {
  it('с именем движка — «Движок {имя} это не сообщает»', () => {
    expect(notReportedText('vLLM')).toBe('Движок vLLM это не сообщает');
    expect(notReportedText('  vllm  ')).toBe('Движок vllm это не сообщает');
  });
  it('без имени движка — «Движок этого не сообщает» (не «Движок движок»)', () => {
    expect(notReportedText(null)).toBe('Движок этого не сообщает');
    expect(notReportedText(undefined)).toBe('Движок этого не сообщает');
    expect(notReportedText('')).toBe('Движок этого не сообщает');
    expect(notReportedText('   ')).toBe('Движок этого не сообщает');
  });
});
