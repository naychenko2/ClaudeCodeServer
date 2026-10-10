import { describe, expect, it } from 'vitest';
import { listStatus } from './characterListView';

describe('listStatus', () => {
  it('без фото — «Нет фото»', () => expect(listStatus(0, null)).toBe('Нет фото'));
  it('с фото — «Узнаётся · N фото»', () => expect(listStatus(4, undefined)).toBe('Узнаётся · 4 фото'));
  it('с подключённой LoRA — «Обучен», даже если фото нет', () => {
    expect(listStatus(4, 'ccs/x.safetensors')).toBe('Обучен');
    expect(listStatus(0, 'ccs/x.safetensors')).toBe('Обучен');
  });
});
