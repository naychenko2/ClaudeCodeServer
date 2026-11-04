import { describe, it, expect } from 'vitest';
import { lineDiff, LINE_DIFF_MAX_LINES } from '../lineDiff';
import { parseDiffToHunks } from '../gitPatch';
import { parseDiff } from '../../components/DiffView';

// Построчный дифф «в проекте → ваша версия» для диалога конфликта (S2c-F2a, К8).
// Формат проверяется настоящими парсерами проекта: lib/gitPatch (parseDiffToHunks)
// и components/DiffView (parseDiff) — тот же вход, что им отдаст S2c-F2b.

describe('lineDiff', () => {
  it('одинаковые тексты → пусто', () => {
    expect(lineDiff('a\nb\nc', 'a\nb\nc')).toBe('');
    expect(lineDiff('одинаковый\nтекст', 'одинаковый\nтекст')).toBe('');
  });

  it('пустые файлы → пусто', () => {
    expect(lineDiff('', '')).toBe('');
  });

  it('вставка: контекст 3 строки, ваша строка с «+»', () => {
    const d = lineDiff('line1\nline2\nline3', 'line1\nline2\nnew\nline3')!;
    expect(d).toBe('@@ -1,3 +1,4 @@\n line1\n line2\n+new\n line3\n');
  });

  it('удаление: чужая строка с «-»', () => {
    const d = lineDiff('a\nb\nc', 'a\nc')!;
    expect(d).toBe('@@ -1,3 +1,2 @@\n a\n-b\n c\n');
  });

  it('замена: чужая «-» перед вашей «+»', () => {
    const d = lineDiff('keep\nold\ntail', 'keep\nnew\ntail')!;
    expect(d).toBe('@@ -1,3 +1,3 @@\n keep\n-old\n+new\n tail\n');
  });

  it('пустой файл с одной стороны: нумерация как у git (0,0)', () => {
    expect(lineDiff('', 'x\ny')).toBe('@@ -0,0 +1,2 @@\n+x\n+y\n');
    expect(lineDiff('x\ny', '')).toBe('@@ -1,2 +0,0 @@\n-x\n-y\n');
  });

  it('CRLF: правка в CRLF-файле даётся построчно', () => {
    const d = lineDiff('a\r\nb', 'a\r\nNEW\r\nb')!;
    expect(d).toBe('@@ -1,2 +1,3 @@\n a\n+NEW\n b\n');
  });

  it('CRLF против LF одного содержимого — различий нет', () => {
    expect(lineDiff('a\r\nb\r\nc', 'a\nb\nc')).toBe('');
  });

  it('хвостовой перевод строки — не строка файла', () => {
    expect(lineDiff('a\nb\n', 'a\nb')).toBe('');
    expect(lineDiff('a\nb', 'a\nb\n')).toBe('');
  });

  it('пустая строка внутри текста — обычная строка', () => {
    const d = lineDiff('a\n\nc', 'a\nx\nc')!;
    expect(d).toBe('@@ -1,3 +1,3 @@\n a\n-\n+x\n c\n');
  });

  it('дальние правки — отдельные хунки', () => {
    const theirs = Array.from({ length: 12 }, (_, i) => `L${i + 1}`).join('\n');
    const mine = ['X', ...Array.from({ length: 7 }, (_, i) => `L${i + 2}`), 'Y', 'L10', 'L11', 'L12'].join('\n');
    const d = lineDiff(theirs, mine)!;
    const parsed = parseDiffToHunks(d);
    expect(parsed.hunks.length).toBe(2);
    expect(parsed.hunks[0].lines.some(l => l.kind === 'del' && l.text === 'L1')).toBe(true);
    expect(parsed.hunks[1].lines.some(l => l.kind === 'add' && l.text === 'Y')).toBe(true);
  });

  it('правки через 6 строк контекста — один хунк (как у git)', () => {
    const theirs = Array.from({ length: 12 }, (_, i) => `L${i + 1}`).join('\n');
    const mine = ['X', ...Array.from({ length: 6 }, (_, i) => `L${i + 2}`), 'Y', 'L9', 'L10', 'L11', 'L12'].join('\n');
    const d = lineDiff(theirs, mine)!;
    expect(d.split('\n').filter(l => l.startsWith('@@')).length).toBe(1);
  });

  it('потолок строк: выше LINE_DIFF_MAX_LINES → null (диалог покажет вариант без диффа)', () => {
    const big = Array.from({ length: LINE_DIFF_MAX_LINES + 1 }, (_, i) => `L${i}`).join('\n');
    expect(lineDiff(big, 'changed')).toBeNull();
    expect(lineDiff('changed', big)).toBeNull();
    // ровно на потолке — работает
    const at = Array.from({ length: LINE_DIFF_MAX_LINES }, (_, i) => `L${i}`).join('\n');
    expect(lineDiff(at, at)).toBe('');
  });

  it('формат читают настоящие парсеры проекта: gitPatch и DiffView', () => {
    const d = lineDiff('keep\nold\ntail', 'keep\nnew\ntail')!;

    const parsed = parseDiffToHunks(d);
    expect(parsed.hunks.length).toBe(1);
    expect(parsed.hunks[0].oldStart).toBe(1);
    expect(parsed.hunks[0].newStart).toBe(1);
    expect(parsed.hunks[0].lines.map(l => [l.kind, l.text])).toEqual([
      ['ctx', 'keep'], ['del', 'old'], ['add', 'new'], ['ctx', 'tail'],
    ]);

    const rows = parseDiff(d);
    expect(rows.map(r => r.type)).toEqual(['hunk', 'ctx', 'del', 'add', 'ctx']);
    // номера строк, которые показывает DiffView: чужая строка — old, ваша — new
    expect(rows.filter(r => r.type === 'del').map(r => r.oldNo)).toEqual([2]);
    expect(rows.filter(r => r.type === 'add').map(r => r.newNo)).toEqual([2]);
  });
});
