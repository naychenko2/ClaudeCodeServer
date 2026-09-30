// Подсветка `кода` в простом тексте: карточка онбординга и список изменений claude CLI.
import { describe, expect, it } from 'vitest';
import { isValidElement } from 'react';
import { renderInlineCode } from '../inlineCode';

const codeText = (n: unknown) => (isValidElement<{ children: string }>(n) && n.type === 'code' ? n.props.children : null);

describe('renderInlineCode', () => {
  it('выделяет код в обратных кавычках, текст вокруг — как есть', () => {
    const parts = renderInlineCode('Added `claude --desktop` to open `app`.');
    expect(parts[0]).toBe('Added ');
    expect(codeText(parts[1])).toBe('claude --desktop');
    expect(parts[2]).toBe(' to open ');
    expect(codeText(parts[3])).toBe('app');
    expect(parts[4]).toBe('.');
  });

  it('текст без кавычек — одна строка', () => {
    expect(renderInlineCode('Fixed a bug')).toEqual(['Fixed a bug']);
  });

  it('незакрытая кавычка остаётся текстом', () => {
    expect(renderInlineCode('Fixed `half')).toEqual(['Fixed `half']);
  });
});
