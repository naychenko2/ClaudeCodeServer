// Логика версий макетов (волна 3, «Планы и макеты»): разбор имени, «текущая» версия
// группы, главный файл, разделение на живые/прошлые. Чистые функции — без DOM.

import { describe, it, expect } from 'vitest';
import type { FeatureMockup, FeatureMockupStatus } from '../types/featurePlans';
import {
  parseMockupVersion, currentVersion, mainHtmlFile, splitMockups,
  MOCKUP_STATUS_LABEL,
} from './mockupVersions';

// Макет с управляемыми полями (path, status, files, modifiedAt).
function mockup(over: Partial<FeatureMockup> = {}): FeatureMockup {
  return {
    path: 'docs/mockups/x.html',
    title: 'X',
    status: 'approved',
    files: [{ path: 'docs/mockups/x.html', exists: true }],
    source: 'header',
    taskId: null,
    authorPersonaId: null,
    modifiedAt: null,
    ...over,
  };
}

describe('parseMockupVersion', () => {
  it('корень без хвоста версии и без расширения', () => {
    expect(parseMockupVersion('docs/mockups/foo-1.html')).toEqual({ root: 'foo', version: 1 });
    expect(parseMockupVersion('docs/mockups/foo-2.html')).toEqual({ root: 'foo', version: 2 });
    expect(parseMockupVersion('docs/mockups/foo-v2.html')).toEqual({ root: 'foo', version: 2 });
    expect(parseMockupVersion('docs/mockups/foo-final.html')).toEqual({ root: 'foo', version: 'final' });
    // Без хвоста — версия null, корень = имя.
    expect(parseMockupVersion('docs/mockups/foo.html')).toEqual({ root: 'foo', version: null });
  });
});

describe('currentVersion', () => {
  it('-final старше любого номера', () => {
    expect(currentVersion([2, 'final'])).toBe('final');
    expect(currentVersion(['final', 1, 2])).toBe('final');
  });
  it('иначе наибольший номер', () => {
    expect(currentVersion([1, 2, 3])).toBe(3);
    expect(currentVersion([5, 2])).toBe(5);
  });
  it('без хвоста — null', () => {
    expect(currentVersion([null])).toBeNull();
    expect(currentVersion([])).toBeNull();
  });
});

describe('mainHtmlFile', () => {
  it('первый .html с exists', () => {
    const m = mockup({
      files: [
        { path: 'docs/mockups/a.md', exists: true },
        { path: 'docs/mockups/a-old.html', exists: false },
        { path: 'docs/mockups/a.html', exists: true },
      ],
    });
    expect(mainHtmlFile(m)).toBe('docs/mockups/a.html');
  });
  it('все html «не в master» → null', () => {
    const m = mockup({ files: [{ path: 'docs/mockups/a.html', exists: false }] });
    expect(mainHtmlFile(m)).toBeNull();
  });
  it('html нет → null', () => {
    const m = mockup({ files: [{ path: 'docs/mockups/a.md', exists: true }] });
    expect(mainHtmlFile(m)).toBeNull();
  });
});

describe('splitMockups', () => {
  it('текущая версия группы живая, заменённая — в прошлые', () => {
    const { live, past } = splitMockups([
      mockup({ path: 'docs/mockups/foo-1.html', title: 'foo 1' }),
      mockup({ path: 'docs/mockups/foo-2.html', title: 'foo 2' }),
    ]);
    expect(live.map(m => m.path)).toEqual(['docs/mockups/foo-2.html']);
    expect(past.map(m => m.path)).toEqual(['docs/mockups/foo-1.html']);
  });

  it('-final старше -2: -final живой, -2 в прошлые', () => {
    const { live, past } = splitMockups([
      mockup({ path: 'docs/mockups/foo-2.html', title: 'foo 2' }),
      mockup({ path: 'docs/mockups/foo-final.html', title: 'foo final' }),
    ]);
    expect(live.map(m => m.path)).toEqual(['docs/mockups/foo-final.html']);
    expect(past.map(m => m.path)).toEqual(['docs/mockups/foo-2.html']);
  });

  it('rejected текущей версии не живая → в прошлые', () => {
    const { live, past } = splitMockups([
      mockup({ path: 'docs/mockups/foo-final.html', title: 'foo final', status: 'rejected' }),
    ]);
    expect(live).toEqual([]);
    expect(past.map(m => m.path)).toEqual(['docs/mockups/foo-final.html']);
  });

  it('разные корни — разные группы, каждая со своей текущей', () => {
    const { live } = splitMockups([
      mockup({ path: 'docs/mockups/foo-1.html', title: 'foo 1' }),
      mockup({ path: 'docs/mockups/foo-2.html', title: 'foo 2' }),
      mockup({ path: 'docs/mockups/bar-1.html', title: 'bar 1' }),
    ]);
    // foo-2 (текущая foo) + bar-1 (единственная bar) — обе живые.
    expect(live.map(m => m.path).sort()).toEqual([
      'docs/mockups/bar-1.html',
      'docs/mockups/foo-2.html',
    ]);
  });
});

describe('MOCKUP_STATUS_LABEL', () => {
  it('маппинг статусов', () => {
    expect(MOCKUP_STATUS_LABEL.draft).toBe('на утверждении');
    expect(MOCKUP_STATUS_LABEL.approved).toBe('утверждён');
    expect(MOCKUP_STATUS_LABEL.rejected).toBe('отклонён');
  });
});
