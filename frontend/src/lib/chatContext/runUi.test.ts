import { describe, expect, it } from 'vitest';
import { etaFraction } from './etaProgress';
import { baseName } from './labels';
import { getRunVersion, runLabelParts, setRunParam } from './actionRun';
import type { ContextAction } from './types';

const edit: ContextAction = { id: 'edit', kind: 'run', label: 'Изменить', verb: 'Изменяем', hint: '' };
const base = { action: edit, params: [], price: null, mobile: false, state: 'running' as const, progress: 0.4 };

describe('подпись хода и прогресс', () => {
  it('во время хода: глагол, имя объекта без папки и процент', () => {
    expect(runLabelParts({ ...base, objectName: baseName('img/hero.png') })).toEqual({ name: '✦ Изменяем hero.png…', tail: ' 40 %' });
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
  });

  it('ответ вопроса («набор стемов») поднимает версию запуска — по ней перерисовывается строка «Чем»', () => {
    const v = getRunVersion();
    setRunParam('s-q', 'audio:x:stems', 'stemSet', '4');
    expect(getRunVersion()).toBe(v + 1);
  });
});
