import { describe, expect, it, vi } from 'vitest';
import type { TrainingPhotosDto } from '../api';
import { createTrainPhotoCountController } from './useTrainPhotoCount';

const dto = (count: number) => ({ count, items: [], min: 20, max: 50 }) as unknown as TrainingPhotosDto;

describe('число фото для обучения', () => {
  it('грузит один раз, переподписки запрос не повторяют', async () => {
    const photos = vi.fn(async () => dto(24));
    const ctl = createTrainPhotoCountController('p', 's', { photos });
    expect(ctl.getSnapshot()).toBeNull();
    for (let i = 0; i < 5; i++) ctl.subscribe(() => {})();
    const fn = vi.fn();
    ctl.subscribe(fn);
    await new Promise(r => setTimeout(r, 0));
    expect(photos).toHaveBeenCalledTimes(1);
    expect(ctl.getSnapshot()).toBe(24);
    expect(fn).toHaveBeenCalled();
  });

  it('set снаружи меняет число и будит подписчиков', async () => {
    const ctl = createTrainPhotoCountController('p', 's', { photos: async () => dto(10) });
    const fn = vi.fn();
    ctl.subscribe(fn);
    await new Promise(r => setTimeout(r, 0));
    fn.mockClear();
    ctl.set(11);
    expect(ctl.getSnapshot()).toBe(11);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('set раньше ответа не затирается устаревшим ответом', async () => {
    let release!: (d: TrainingPhotosDto) => void;
    const ctl = createTrainPhotoCountController('p', 's', { photos: () => new Promise(r => { release = r; }) });
    ctl.subscribe(() => {});
    ctl.set(30);
    release(dto(5));
    await new Promise(r => setTimeout(r, 0));
    expect(ctl.getSnapshot()).toBe(30);
  });

  it('ошибка запроса → null', async () => {
    const ctl = createTrainPhotoCountController('p', 's', { photos: async () => { throw new Error('x'); } });
    ctl.subscribe(() => {});
    await new Promise(r => setTimeout(r, 0));
    expect(ctl.getSnapshot()).toBeNull();
  });
});
