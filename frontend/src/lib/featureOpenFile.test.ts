import { describe, it, expect } from 'vitest';
import { nextFileTaskAnchor, anchorFor } from './featureOpenFile';

describe('якорь файла со страницы фичи (FS-F1c)', () => {
  it('материал задачи → якорь с taskId и branch', () => {
    expect(nextFileTaskAnchor('a.md', { taskId: 't1', branch: 'feat/x' })).toEqual({ path: 'a.md', taskId: 't1', branch: 'feat/x' });
  });
  it('открытие без opts (дерево/чат) → якоря нет, вьюер читает main', () => {
    expect(nextFileTaskAnchor('a.md')).toBeNull();
    expect(nextFileTaskAnchor('a.md', {})).toBeNull();
  });
  it('сценарий: материал задачи, затем файл из дерева → taskId и branch сброшены', () => {
    let anchor = nextFileTaskAnchor('a.md', { taskId: 't1', branch: 'b' });
    expect(anchorFor(anchor, 'a.md')).toEqual({ taskId: 't1', branch: 'b' });
    anchor = nextFileTaskAnchor('b.md');
    expect(anchorFor(anchor, 'b.md').taskId).toBeUndefined();
    expect(anchorFor(anchor, 'b.md').branch).toBeUndefined();
  });
  it('якорь не действует на другой файл', () => {
    expect(anchorFor({ path: 'a.md', taskId: 't1' }, 'other.md')).toEqual({});
  });
});
