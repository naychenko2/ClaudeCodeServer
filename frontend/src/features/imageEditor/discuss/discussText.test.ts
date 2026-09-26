import { describe, expect, it } from 'vitest';
import { buildDiscussText, extractImagePrompt, marksToWords } from './discussText';

describe('extractImagePrompt', () => {
  it('берёт последний закрытый блок image-prompt', () => {
    const text = 'Вот так:\n```image-prompt\nпервый\n```\nИли лучше:\n```image-prompt\nлатунный торшер справа\n```\n';
    expect(extractImagePrompt(text)).toBe('латунный торшер справа');
  });

  it('незакрытый блок и чужие языки не берёт', () => {
    expect(extractImagePrompt('```ts\nconst a = 1;\n```')).toBeNull();
    expect(extractImagePrompt('```image-prompt\nещё пишется')).toBeNull();
  });
});

describe('buildDiscussText', () => {
  it('собирает запрос, пометки словами и просьбу вернуть блок', () => {
    const text = buildDiscussText({
      fileName: 'hero.png', prompt: 'добавь торшер',
      marks: [{ type: 'arrow', x1: 10, y1: 10, x2: 90, y2: 10 }], size: { w: 100, h: 100 },
    });
    expect(text).toContain('hero.png');
    expect(text).toContain('Мой запрос: добавь торшер');
    expect(text).toContain('- стрелка указывает справа вверху');
    expect(text).toContain('```image-prompt');
  });
});

describe('marksToWords', () => {
  it('центр картинки называет «в центре»', () => {
    expect(marksToWords([{ type: 'rect', x: 40, y: 40, w: 20, h: 20 }], 100, 100)).toEqual(['рамка в центре']);
  });
});
