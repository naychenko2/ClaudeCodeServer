import { describe, expect, it, vi } from 'vitest';
import { listStatus, resetSelectionOnDelete } from './characterListView';

describe('listStatus', () => {
  it('без фото — «Нет фото»', () => expect(listStatus(0, null)).toBe('Нет фото'));
  it('с фото — «Узнаётся · N фото»', () => expect(listStatus(4, undefined)).toBe('Узнаётся · 4 фото'));
  it('с подключённой LoRA — «Обучен», даже если фото нет', () => {
    expect(listStatus(4, 'ccs/x.safetensors')).toBe('Обучен');
    expect(listStatus(0, 'ccs/x.safetensors')).toBe('Обучен');
  });
});

describe('resetSelectionOnDelete', () => {
  it('удалили выбранного — выбор сбрасывается', () => {
    const reset = vi.fn();
    resetSelectionOnDelete('anya', 'anya', reset);
    expect(reset).toHaveBeenCalledOnce();
  });
  it('удалили другого или выбора нет — не трогаем', () => {
    const reset = vi.fn();
    resetSelectionOnDelete('anya', 'olya', reset);
    resetSelectionOnDelete(null, 'olya', reset);
    resetSelectionOnDelete(undefined, 'olya', reset);
    expect(reset).not.toHaveBeenCalled();
  });
});
