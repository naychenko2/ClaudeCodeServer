import { beforeEach, describe, expect, it } from 'vitest';
import { __resetScope, enterScope, imageBase, imageScope, PERSONAL_SCOPE, projectBase, scopeBase, threadsBase } from './scope';

// Область редактора: проект или личный чат вне проекта — и URL ручек каждой из них

beforeEach(() => __resetScope());

describe('область редактора', () => {
  it('без проекта — personal, с проектом — его id', () => {
    expect(imageScope(null)).toBe(PERSONAL_SCOPE);
    expect(imageScope('p1')).toBe('p1');
  });

  it('imageBase: проект — по проекту, личная — по чату', () => {
    expect(imageBase('p 1', 's1')).toBe('/projects/p%201/image-editor');
    expect(imageBase(PERSONAL_SCOPE, 's 1')).toBe('/image-editor/chats/s%201');
    expect(() => imageBase(PERSONAL_SCOPE, null)).toThrow();
  });

  it('нити: у проекта — через sessions/{id}, у личной — чат уже в базе', () => {
    expect(threadsBase('p1', 's1')).toBe('/projects/p1/image-editor/sessions/s1/threads');
    expect(threadsBase(PERSONAL_SCOPE, 's1')).toBe('/image-editor/chats/s1/threads');
  });

  it('ручки без чата в сигнатуре берут личный чат, в котором модуль рисуется', () => {
    expect(() => scopeBase(PERSONAL_SCOPE)).toThrow();
    expect(enterScope(null, 's2')).toBe(PERSONAL_SCOPE);
    expect(scopeBase(PERSONAL_SCOPE)).toBe('/image-editor/chats/s2');
    // Чат проекта личный чат не перебивает
    expect(enterScope('p1', 's3')).toBe('p1');
    expect(scopeBase(PERSONAL_SCOPE)).toBe('/image-editor/chats/s2');
    expect(scopeBase('p1')).toBe('/projects/p1/image-editor');
  });

  it('проектные ручки (save, characters) у личной области — отказ до запроса', () => {
    enterScope(null, 's1');
    expect(() => projectBase(PERSONAL_SCOPE)).toThrow();
    expect(projectBase('p1')).toBe('/projects/p1/image-editor');
  });
});
