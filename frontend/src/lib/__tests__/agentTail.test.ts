import { describe, it, expect } from 'vitest';
import { splitAgentResultTail, formatTailTokens, formatTailDuration } from '../agentTail';

// Реальный формат хвоста из транскриптов CLI: строка agentId (с подсказкой SendMessage
// в скобках) + блок <usage> с переводами строк между парами ключ-значение
const FULL_TAIL =
  "Ответ консультанта по существу.\n\n" +
  "agentId: a011da168d23b9e32 (use SendMessage with to: 'a011da168d23b9e32', summary: '<5-10 word recap>' to continue this agent)\n" +
  '<usage>subagent_tokens: 30161\ntool_uses: 1\nduration_ms: 31510</usage>';

describe('splitAgentResultTail', () => {
  it('вырезает agentId и usage, отдаёт метрики', () => {
    const { body, tail } = splitAgentResultTail(FULL_TAIL);
    expect(body).toBe('Ответ консультанта по существу.');
    expect(tail).toEqual({
      agentId: 'a011da168d23b9e32',
      tokens: 30161,
      toolUses: 1,
      durationMs: 31510,
    });
  });

  it('переживает хвост только с usage (без agentId)', () => {
    const { body, tail } = splitAgentResultTail(
      'Текст.\n<usage>subagent_tokens: 500\ntool_uses: 2\nduration_ms: 900</usage>');
    expect(body).toBe('Текст.');
    expect(tail).toEqual({ tokens: 500, toolUses: 2, durationMs: 900 });
  });

  it('переживает хвост только с agentId (без usage)', () => {
    const { body, tail } = splitAgentResultTail('Текст.\nagentId: abc123');
    expect(body).toBe('Текст.');
    expect(tail).toEqual({ agentId: 'abc123' });
  });

  it('не трогает agentId в середине текста', () => {
    const text = 'В логе видно agentId: xyz — это причина бага.\nИтог: чинить.';
    const { body, tail } = splitAgentResultTail(text);
    expect(body).toBe(text);
    expect(tail).toBeNull();
  });

  it('не трогает текст без хвоста', () => {
    const { body, tail } = splitAgentResultTail('Просто ответ.');
    expect(body).toBe('Просто ответ.');
    expect(tail).toBeNull();
  });

  it('usage в одну строку тоже парсится', () => {
    // ClaudeSession склеивает блоки через AppendLine, но подстрахуемся от однострочного вида
    const { tail } = splitAgentResultTail(
      'Ок.\n<usage>subagent_tokens: 100\ntool_uses: 3\nduration_ms: 4000</usage>');
    expect(tail?.toolUses).toBe(3);
  });
});

describe('форматтеры', () => {
  it('токены', () => {
    expect(formatTailTokens(999)).toBe('999');
    expect(formatTailTokens(30161)).toBe('30k');
    expect(formatTailTokens(1500)).toBe('1,5k');
    expect(formatTailTokens(133903)).toBe('134k');
  });

  it('длительность', () => {
    expect(formatTailDuration(31510)).toBe('32с');
    expect(formatTailDuration(772726)).toBe('12м 53с');
    expect(formatTailDuration(120000)).toBe('2м');
  });
});
