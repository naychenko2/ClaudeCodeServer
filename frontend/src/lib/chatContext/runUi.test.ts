import { describe, expect, it } from 'vitest';
import { etaFraction } from './etaProgress';
import { baseName } from './labels';
import { getRunVersion, runLabelParts, setRunParam } from './actionRun';
import type { ContextAction } from './types';

const edit: ContextAction = { id: 'edit', kind: 'run', label: 'Изменить', verb: 'Изменяем', hint: '' };
const base = { action: edit, params: [], price: null, mobile: false, state: 'running' as const, progress: 0.4 };

describe('подпись хода и прогресс', () => {
  it('во время хода: глагол, имя объекта без папки через двоеточие и процент', () => {
    expect(runLabelParts({ ...base, objectLabel: 'img/hero.png' })).toEqual({ name: '✦ Изменяем: hero.png…', tail: ' 40 %' });
  });

  it('безымянный черновик — без имени, глагол с предлогом — имя через пробел', () => {
    expect(runLabelParts({ ...base, objectLabel: 'Новая картинка · черновик' }).name).toBe('✦ Изменяем…');
    expect(runLabelParts({ ...base, objectLabel: 'Новый звук · черновик', action: { ...edit, verb: 'Пишем песню' } }).name).toBe('✦ Пишем песню…');
    expect(runLabelParts({ ...base, objectLabel: 'img/hero.png', action: { ...edit, verb: 'Убираем фон у' } }).name).toBe('✦ Убираем фон у hero.png…');
    expect(runLabelParts({ ...base, objectLabel: 'Сцена 1 · черновик' }).name).toBe('✦ Изменяем: Сцена 1…');
  });

  it('без глагола — имя действия', () => {
    expect(runLabelParts({ ...base, action: { ...edit, verb: undefined } }).name).toBe('✦ Изменить…');
  });

  it('полоса растёт от ожидаемой длительности, в очереди стоит, внутри прогона не выше 95 %', () => {
    expect(etaFraction({ etaSeconds: 40, queued: true }, 0, 20_000)).toBe(0);
    expect(etaFraction({ etaSeconds: 40 }, 0, 20_000)).toBeCloseTo(0.5);
    expect(etaFraction({ etaSeconds: 40 }, 0, 400_000)).toBeCloseTo(0.95);
    expect(etaFraction({ run: 2, runs: 2, etaSeconds: 40 }, 0, 20_000)).toBeCloseTo(0.75);
  });

  it('имя файла без папки', () => {
    expect(baseName('a/b/c.png')).toBe('c.png');
    expect(baseName('hero.png')).toBe('hero.png');
    // Пометка черновика: в подписи запуска срезается, на экране (keepDraft) остаётся
    expect(baseName('Сцена 1 · черновик')).toBe('Сцена 1');
    expect(baseName('Новая картинка · черновик', true)).toBe('Новая картинка · черновик');
    expect(baseName('a/b.png', true)).toBe('b.png');
  });

  it('ответ вопроса («набор стемов») поднимает версию запуска — по ней перерисовывается строка «Чем»', () => {
    const v = getRunVersion();
    setRunParam('s-q', 'audio:x:stems', 'stemSet', '4');
    expect(getRunVersion()).toBe(v + 1);
  });
});
