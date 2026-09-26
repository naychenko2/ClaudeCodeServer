import { describe, expect, it } from 'vitest';
import type { FileEntry } from '../../types';
import { checkKey, defaultStem, nameStem, projectFolders, saveBlocked, suggestionStem, type SaveCheckState } from './saveAs';

const entry = (path: string, isDirectory = false): FileEntry =>
  ({ name: path.split('/').pop() ?? path, path, isDirectory, modified: '', isModified: false });

describe('«Сохранить как…»: имя', () => {
  it('вписанное расширение срезается, а не удваивается', () => {
    expect(nameStem('hero.png')).toBe('hero');
    expect(nameStem(' hero.v3.PNG ')).toBe('hero.v3');
    expect(nameStem('photo.jpeg')).toBe('photo');
    expect(nameStem('hero.v2')).toBe('hero.v2');
  });

  it('по умолчанию — следующая версия исходника без расширения', () => {
    expect(defaultStem('hero.png')).toBe('hero.v2');
    expect(defaultStem('hero.v2.png')).toBe('hero.v3');
  });

  it('подсказка сервера подставляется именем без папки и расширения', () => {
    expect(suggestionStem('images/generated/hero.v3.png')).toBe('hero.v3');
  });
});

describe('«Сохранить как…»: кнопка', () => {
  const key = checkKey('images', 'hero.v2', 'png');
  const free: SaveCheckState = { key, result: { path: 'images/hero.v2.png', taken: false, suggestion: null }, error: null };
  const taken: SaveCheckState = { key, result: { path: 'images/hero.v2.png', taken: true, suggestion: 'images/hero.v3.png' }, error: null };

  it('свободное проверенное имя — можно сохранять', () => {
    expect(saveBlocked('hero.v2', key, free)).toBe(false);
  });

  it('занятое имя блокирует кнопку', () => {
    expect(saveBlocked('hero.v2', key, taken)).toBe(true);
  });

  it('пустое, ещё не проверенное или отвергнутое имя блокирует кнопку', () => {
    expect(saveBlocked('', key, free)).toBe(true);
    expect(saveBlocked('hero.v2', key, null)).toBe(true);
    expect(saveBlocked('hero.v2', checkKey('images/blog', 'hero.v2', 'png'), free)).toBe(true);
    expect(saveBlocked('hero.v2', key, { key, result: null, error: 'Путь вне папки проекта' })).toBe(true);
  });

  it('смена формата требует новой проверки: у WebP своё расширение', () => {
    expect(saveBlocked('hero.v2', checkKey('images', 'hero.v2', 'webp'), free)).toBe(true);
  });
});

describe('«Сохранить как…»: папки', () => {
  it('корень первым, число файлов прямо в папке', () => {
    const list = projectFolders([
      entry('README.md'), entry('images', true), entry('images/hero.png'), entry('images/logo.png'),
      entry('images/blog', true), entry('images/generated', true), entry('images/generated/hero.v2.png'),
    ]);
    expect(list).toEqual([
      { path: '', files: 1 },
      { path: 'images', files: 2 },
      { path: 'images/blog', files: 0 },
      { path: 'images/generated', files: 1 },
    ]);
  });
});
