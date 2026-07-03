import { describe, it, expect } from 'vitest';
import { toRelative, relPath, stripRoot } from '../paths';

const ROOT = 'C:\\Sources\\MyProject';

// --- toRelative: нормализация путей ---

describe('toRelative', () => {
  it('windows-путь с backslash внутри корня → относительный с forward slash', () => {
    expect(toRelative('C:\\Sources\\MyProject\\src\\app.ts', ROOT)).toBe('src/app.ts');
  });

  it('путь с forward slash внутри корня', () => {
    expect(toRelative('C:/Sources/MyProject/src/app.ts', ROOT)).toBe('src/app.ts');
  });

  it('разный регистр диска и папок — сравнение регистронезависимое, регистр остатка сохраняется', () => {
    expect(toRelative('c:\\sources\\myproject\\SRC\\App.ts', ROOT)).toBe('SRC/App.ts');
  });

  it('путь вне корня → null', () => {
    expect(toRelative('D:\\Other\\file.ts', ROOT)).toBeNull();
    expect(toRelative('C:\\Sources\\OtherProject\\file.ts', ROOT)).toBeNull();
  });

  it('похожий префикс без разделителя (MyProjectX) — не считается внутри корня', () => {
    expect(toRelative('C:\\Sources\\MyProjectX\\file.ts', ROOT)).toBeNull();
  });

  it('сам корень → null (не файл)', () => {
    expect(toRelative('C:\\Sources\\MyProject', ROOT)).toBeNull();
  });

  it('trailing slash у корня не мешает', () => {
    expect(toRelative('C:\\Sources\\MyProject\\a.ts', 'C:\\Sources\\MyProject\\')).toBe('a.ts');
  });

  it('уже относительный путь возвращается как есть (без ./)', () => {
    expect(toRelative('src/foo.ts', ROOT)).toBe('src/foo.ts');
    expect(toRelative('./src/foo.ts', ROOT)).toBe('src/foo.ts');
    expect(toRelative('src\\foo.ts', ROOT)).toBe('src/foo.ts');
  });

  it('относительный с выходом за корень (../) → null', () => {
    expect(toRelative('../outside.ts', ROOT)).toBeNull();
    expect(toRelative('src/../../x.ts', ROOT)).toBeNull();
  });

  it('unix-пути', () => {
    expect(toRelative('/home/user/proj/file.ts', '/home/user/proj')).toBe('file.ts');
    expect(toRelative('/home/user/other/file.ts', '/home/user/proj')).toBeNull();
  });

  it('пустой rootPath: абсолютный путь → null, относительный проходит', () => {
    expect(toRelative('C:/x/a.ts', '')).toBeNull();
    expect(toRelative('src/a.ts', '')).toBe('src/a.ts');
  });
});

// --- relPath: путь для показа в UI ---

describe('relPath', () => {
  it('абсолютный путь внутри корня → относительный', () => {
    expect(relPath('C:\\Sources\\MyProject\\src\\app.ts', ROOT)).toBe('src/app.ts');
    expect(relPath('c:/sources/myproject/SRC/App.ts', ROOT)).toBe('SRC/App.ts');
  });

  it('сам корень → «.»', () => {
    expect(relPath('C:\\Sources\\MyProject', ROOT)).toBe('.');
    expect(relPath('c:/sources/myproject', ROOT)).toBe('.');
  });

  it('вне корня — без изменений (не отсекает, в отличие от toRelative)', () => {
    expect(relPath('D:\\Other\\file.ts', ROOT)).toBe('D:\\Other\\file.ts');
  });

  it('относительный путь — без изменений (в т.ч. backslash)', () => {
    expect(relPath('src/foo.ts', ROOT)).toBe('src/foo.ts');
    expect(relPath('src\\foo.ts', ROOT)).toBe('src\\foo.ts');
  });

  it('без корня (чат вне проекта) — как есть', () => {
    expect(relPath('C:\\anything\\a.ts')).toBe('C:\\anything\\a.ts');
    expect(relPath('C:\\anything\\a.ts', null)).toBe('C:\\anything\\a.ts');
    expect(relPath('', ROOT)).toBe('');
  });
});

// --- stripRoot: вырезание корня из произвольного текста ---

describe('stripRoot', () => {
  it('путь с корнем внутри текста → остаток', () => {
    expect(stripRoot('cat C:\\Sources\\MyProject\\src\\a.ts', ROOT)).toBe('cat src\\a.ts');
  });

  it('оба разделителя и регистр', () => {
    expect(stripRoot('ls c:/sources/myproject/src', ROOT)).toBe('ls src');
  });

  it('голый корень → «.»', () => {
    expect(stripRoot(`cd ${ROOT}`, ROOT)).toBe('cd .');
  });

  it('несколько вхождений', () => {
    expect(stripRoot('C:\\Sources\\MyProject\\a && C:\\Sources\\MyProject\\b', ROOT)).toBe('a && b');
  });

  it('без корня — текст без изменений', () => {
    expect(stripRoot('echo hi', ROOT)).toBe('echo hi');
    expect(stripRoot('echo hi')).toBe('echo hi');
  });
});
