import { describe, expect, it } from 'vitest';
import {
  chunk, photosCounter, photosState, photosStatus, rejectText, splitByRoom, uploadPercent, uploadProgressText,
} from './trainingPhotos';

describe('состояние секции фото обучения', () => {
  it('различает пусто, мало, хватает, предел и загрузку', () => {
    expect(photosState(0, false)).toBe('empty');
    expect(photosState(12, false)).toBe('low');
    expect(photosState(19, false)).toBe('low');
    expect(photosState(20, false)).toBe('enough');
    expect(photosState(49, false)).toBe('enough');
    expect(photosState(50, false)).toBe('limit');
    expect(photosState(0, true)).toBe('uploading');
  });

  it('тексты статуса — дословно из заметки', () => {
    expect(photosCounter(12)).toBe('12 из 50 · нужно минимум 20');
    expect(photosStatus('low', 12)).toEqual({ text: 'Нужно ещё 8 фото, чтобы начать.', tone: 'warning' });
    expect(photosStatus('enough', 34)?.text).toBe('Фото достаточно. Можно обучать.');
    expect(photosStatus('limit', 50)?.text).toBe('Достигнут предел — 50 фото.');
    expect(photosStatus('empty', 0)).toBeNull();
  });

  it('пороги серверных причин подставляются в текст', () => {
    expect(rejectText('too_small')).toBe('Фото слишком маленькое. Нужна короткая сторона от 512 пикселей.');
    expect(rejectText('too_big')).toBe('Фото слишком тяжёлое. Максимум 8 МБ.');
    expect(rejectText('format')).toBe('Формат не подходит. Нужен JPG, PNG или WebP.');
    expect(rejectText('duplicate')).toBe('Такое фото уже добавлено.');
  });
});

describe('загрузка пачками', () => {
  it('режет по 10 и считает прогресс', () => {
    expect(chunk(Array.from({ length: 23 }, (_, i) => i)).map(c => c.length)).toEqual([10, 10, 3]);
    expect(uploadProgressText(3, 8)).toBe('Загружаю 8 фото… 3 из 8');
    expect(uploadPercent(3, 8)).toBe(38);
    expect(uploadPercent(0, 0)).toBe(0);
  });

  it('сверх предела отсекает лишнее', () => {
    const files = Array.from({ length: 5 }, (_, i) => ({ name: `f${i}` }));
    const r = splitByRoom(files, 47);
    expect(r.accepted.map(f => f.name)).toEqual(['f0', 'f1', 'f2']);
    expect(r.overflow.map(f => f.name)).toEqual(['f3', 'f4']);
    expect(splitByRoom(files, 50).accepted).toEqual([]);
  });
});
