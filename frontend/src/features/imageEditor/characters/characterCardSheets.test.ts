// Карточка персонажа: какая шторка открывается с чипа или пункта меню, раскладка коллажа, точка «нужны фото»,
// тексты удаления (прежние из CharacterForm).
import { describe, expect, it } from 'vitest';
import {
  chipKeys, collageLayout, deleteConfirmTexts, menuKeys, orderedPhotos, photosNeedDot, sheetForChip, sheetForMenu,
} from './characterCardSheets';

describe('шторки карточки', () => {
  it('пункты меню открывают свои шторки, «Удалить» — никакую', () => {
    expect(sheetForMenu('name')).toBe('name');
    expect(sheetForMenu('photos')).toBe('photos');
    expect(sheetForMenu('versions')).toBe('versions');
    expect(sheetForMenu('tune')).toBe('tune');
    expect(sheetForMenu('remove')).toBeNull();
  });
  it('чипы открывают свои шторки', () => {
    expect(sheetForChip('photos')).toBe('photos');
    expect(sheetForChip('versions')).toBe('versions');
    expect(sheetForChip('tune')).toBe('tune');
  });
  it('«Версии» в меню и чипах — только если версии есть', () => {
    expect(menuKeys(0)).toEqual(['name', 'photos', 'tune', 'remove']);
    expect(menuKeys(2)).toEqual(['name', 'photos', 'versions', 'tune', 'remove']);
    expect(chipKeys(0)).toEqual(['photos', 'tune']);
    expect(chipKeys(1)).toEqual(['photos', 'versions', 'tune']);
  });
});

describe('точка «Нужны фото для обучения»', () => {
  it('мало фото — есть, хватает — нет, неизвестно — нет', () => {
    expect(photosNeedDot(19, false)).toBe(true);
    expect(photosNeedDot(20, false)).toBe(false);
    expect(photosNeedDot(null, false)).toBe(false);
  });
  it('ни одно фото не подошло — есть при любом числе', () => {
    expect(photosNeedDot(30, true)).toBe(true);
  });
});

describe('коллаж', () => {
  it('до трёх фото без «+N», больше — остаток на третьем', () => {
    expect(collageLayout(1)).toEqual({ shown: 1, extra: 0 });
    expect(collageLayout(3)).toEqual({ shown: 3, extra: 0 });
    expect(collageLayout(7)).toEqual({ shown: 3, extra: 4 });
  });
  it('главное фото первым', () => {
    const a = { file: 'a' }, b = { file: 'b', primary: true }, c = { file: 'c' };
    expect(orderedPhotos([a, b, c]).map(p => p.file)).toEqual(['b', 'a', 'c']);
    expect(orderedPhotos([a, c]).map(p => p.file)).toEqual(['a', 'c']);
    expect(orderedPhotos([])).toEqual([]);
  });
});

describe('удаление персонажа', () => {
  it('тексты прежние', () => {
    expect(deleteConfirmTexts('Аня', 'characters/anya')).toEqual({
      title: 'Удалить «Аня»?',
      subtitle: 'Папка characters/anya/ с фото удалится из проекта',
      confirmLabel: 'Удалить',
    });
  });
});
