import { describe, expect, it } from 'vitest';
import { buildFrameMenu, type FrameMenuInput } from './frameMenu';

const base: FrameMenuInput = {
  slot: 'B', frame: null, personal: false, prev: { exists: true, hasFrameB: true, usable: true },
  editInImages: () => {}, drawInImages: () => {}, fromProject: () => {}, fromPrevious: () => {}, clear: () => {},
};
const ids = (i: FrameMenuInput) => buildFrameMenu(i).map(x => x.id);
const item = (i: FrameMenuInput, id: string) => buildFrameMenu(i).find(x => x.id === id)!;

describe('меню кадра', () => {
  it('слот пуст: шапка «не выбран», «Нарисовать», «Из проекта», «Кадр B прошлой сцены», без «Убрать»', () => {
    expect(ids(base)).toEqual(['head', 'draw', 'project', 'prev']);
    expect(item(base, 'head').label).toBe('Кадр B · не выбран');
    expect(item(base, 'draw').label).toBe('Нарисовать в «Картинках»');
  });

  it('кадр есть: шапка с именем, «Править в „Картинках“» вместо «Нарисовать», «Убрать кадр»', () => {
    const i = { ...base, slot: 'A' as const, frame: { label: 'рассвет.png' } };
    expect(ids(i)).toEqual(['head', 'edit', 'project', 'prev', 'clear']);
    expect(item(i, 'head').label).toBe('Кадр A · рассвет.png');
    expect(item(i, 'edit').label).toBe('Править в «Картинках»');
  });

  it('шапка — не действие: серая', () => {
    expect(item(base, 'head').disabledReason).toBeTruthy();
  });

  it('«Из проекта» серый в личном чате; «Кадр B прошлой сцены» — по состоянию прошлой сцены', () => {
    expect(item({ ...base, personal: true }, 'project').disabledReason).toMatch(/нет проекта/);
    expect(item(base, 'project').disabledReason).toBeUndefined();
    expect(item(base, 'prev').disabledReason).toBeUndefined();
    expect(item({ ...base, prev: { exists: false, hasFrameB: false, usable: false } }, 'prev').disabledReason).toMatch(/нет предыдущей/);
    expect(item({ ...base, prev: { exists: true, hasFrameB: false, usable: false } }, 'prev').disabledReason).toMatch(/нет кадра B/);
    expect(item({ ...base, prev: { exists: true, hasFrameB: true, usable: false } }, 'prev').disabledReason).toMatch(/нельзя взять/);
  });

  it('пункты зовут свои обработчики', () => {
    const calls: string[] = [];
    const i: FrameMenuInput = {
      ...base, frame: { label: 'k.png' },
      editInImages: () => calls.push('edit'), drawInImages: () => calls.push('draw'), fromProject: () => calls.push('project'),
      fromPrevious: () => calls.push('prev'), clear: () => calls.push('clear'),
    };
    for (const id of ['edit', 'project', 'prev', 'clear']) item(i, id).run();
    expect(calls).toEqual(['edit', 'project', 'prev', 'clear']);
  });
});
