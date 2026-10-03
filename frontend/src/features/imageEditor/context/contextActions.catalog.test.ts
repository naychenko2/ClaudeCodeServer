import { describe, expect, it } from 'vitest';
import { actionChips, chipSelectable, CHAT_CHIP_ID, MAX_VIEW_ACTIONS } from '../../../lib/chatContext/actionChips';
import { pickDefaultAction } from '../../../lib/chatContext/actionMemory';
import type { ImageEditOp } from '../api';
import { ASPECT_OPTIONS, buildImageActions, imageState, type ImageActionInput } from './actions';

// Каталог действий картинки (ADR-023 §Д2.2): три состояния нити

// ImageEditOp перечислен значениями, чтобы op вне типа не прошёл ни сборку, ни этот тест
const IMAGE_OPS: readonly ImageEditOp[] = ['generate', 'edit', 'inpaint', 'outpaint', 'removeBackground', 'upscale', 'enhanceFaces'];

const base: ImageActionInput = {
  hasFile: true, marks: 0, hasMask: false,
  offered: { removeBackground: true, upscale: true, outpaint: true },
  openBrush: () => {},
};
const FIXTURES: Record<string, ImageActionInput> = {
  'черновик': { ...base, hasFile: false },
  'с файлом': base,
  'с отметками (кисть)': { ...base, marks: 2, hasMask: true },
  'с отметками (стрелки, без кисти)': { ...base, marks: 1, hasMask: false },
};

describe.each(Object.entries(FIXTURES))('каталог картинки · %s', (_name, input) => {
  const actions = buildImageActions(input);

  it('вид отдаёт не больше пяти действий: с «Чатом» хоста чипов не больше шести, «Чат» первым', () => {
    expect(actions.length).toBeLessThanOrEqual(MAX_VIEW_ACTIONS);
    const chips = actionChips(actions);
    expect(chips.length).toBeLessThanOrEqual(6);
    expect(chips[0].id).toBe(CHAT_CHIP_ID);
  });

  it('id уникальны', () => {
    expect(new Set(actions.map(a => a.id)).size).toBe(actions.length);
  });

  it('op каждого run-действия — операция редактора картинок, лица вне чипов', () => {
    for (const a of actions.filter(x => x.kind === 'run')) {
      expect(IMAGE_OPS).toContain(a.op);
      expect(a.op).not.toBe('enhanceFaces');
    }
  });

  it('у объекта человека выбран ровно один чип: первое run без серости', () => {
    const selectable = actionChips(actions).filter(chipSelectable).map(c => c.id);
    const picked = pickDefaultAction(actions, 'human');
    expect(picked).toBe(actions.find(a => a.kind === 'run' && !a.disabledReason)!.id);
    expect(selectable).toContain(picked);
    // Объект агента — всегда «Чат»
    expect(pickDefaultAction(actions, 'agent')).toBeNull();
  });
});

describe('каталог картинки: состав по состояниям', () => {
  const ids = (i: ImageActionInput) => buildImageActions(i).map(a => a.id);

  it('состояние читается из нити и отметок', () => {
    expect(imageState({ hasFile: false, marks: 3 })).toBe('draft');
    expect(imageState({ hasFile: true, marks: 0 })).toBe('file');
    expect(imageState({ hasFile: true, marks: 1 })).toBe('marks');
  });

  it('черновик — одно «Нарисовать» с обязательным текстом', () => {
    expect(buildImageActions(FIXTURES['черновик'])).toMatchObject([
      { id: 'draw', kind: 'run', op: 'generate', text: 'required' },
    ]);
  });

  it('с файлом: изменить, убрать фон, увеличить, дорисовать и вход «Отметить» — порядок и есть умолчание', () => {
    expect(ids(base)).toEqual(['edit', 'removeBg', 'upscale', 'outpaint', 'mark']);
    const [edit, removeBg, upscale, outpaint, mark] = buildImageActions(base);
    expect(edit).toMatchObject({ label: 'Изменить', op: 'edit', text: 'required' });
    expect(removeBg).toMatchObject({ op: 'removeBackground', text: 'none' });
    expect(upscale).toMatchObject({ op: 'upscale', text: 'none' });
    expect(outpaint).toMatchObject({ op: 'outpaint', text: 'none', question: { param: 'aspect', title: 'Пропорции' } });
    expect(mark).toMatchObject({ kind: 'editor', label: 'Отметить' });
  });

  it('с отметками «Изменить» становится «Изменить отмеченное»: с кистью — инпейнт, со стрелками — правка', () => {
    const [masked] = buildImageActions(FIXTURES['с отметками (кисть)']);
    expect(masked).toMatchObject({ id: 'edit', label: 'Изменить отмеченное', op: 'inpaint' });
    const [arrows] = buildImageActions(FIXTURES['с отметками (стрелки, без кисти)']);
    expect(arrows).toMatchObject({ label: 'Изменить отмеченное', op: 'edit' });
  });

  it('вопрос «Пропорции» — только то, что принимает сервер, первое значение предвыбрано', () => {
    const q = buildImageActions(base).find(a => a.id === 'outpaint')!.question!;
    expect(q.options.map(o => o.value)).toEqual([...ASPECT_OPTIONS]);
    expect(q.options[0].value).toBe('1:1');
  });

  it('операцию, которой не умеет ни один поставщик, делает серой с причиной, умолчание на «Изменить»', () => {
    const noBg = buildImageActions({ ...base, offered: { ...base.offered, removeBackground: false } });
    expect(noBg.find(a => a.id === 'removeBg')!.disabledReason).toMatch(/убирать фон/);
    expect(chipSelectable(actionChips(noBg).find(c => c.id === 'removeBg')!)).toBe(false);
    expect(pickDefaultAction(noBg, 'human')).toBe('edit');
  });

  it('«Отметить» открывает редактор на кисти', () => {
    let opened = 0;
    buildImageActions({ ...base, openBrush: () => { opened++; } }).find(a => a.id === 'mark')!.open!();
    expect(opened).toBe(1);
  });
});
