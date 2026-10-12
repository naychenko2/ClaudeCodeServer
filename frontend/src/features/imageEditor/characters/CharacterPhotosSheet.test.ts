// faceSection — чистая функция: что шторка «Фото» передаёт блоку «Лицо для узнавания».
// При выключенном character-smart-photos — {} (ровно как master: прежнее согласие, фото не
// нажимаются, без подписей и редактора); при включённом — новое согласие, нажим, подписи, редактор.

import { describe, expect, it } from 'vitest';
import { faceSection } from './CharacterPhotosSheet';
import { CHARACTER_PHOTOS_NOTICE_LABELS } from './characterTexts';
import type { PhotoLabelState } from './PhotoLabelChips';

const labels = new Map<string, PhotoLabelState>([
  ['a.jpg', { angle: 'front', emotion: 'neutral', labelSource: 'auto' }],
  ['b.jpg', { angle: 'three-quarter', emotion: 'smile', labelSource: 'auto' }],
]);

const ctx = {
  labels,
  selected: null as string | null,
  setSelected: (_fn: (cur: string | null) => string | null) => {},
  savingLabel: null as string | null,
  labelError: null as string | null,
  saveLabel: (_f: string, _n: { angle?: string | null; pitch?: string | null; emotion?: string | null }) => {},
  removePhoto: (_f: string) => {},
  ordered: [{ file: 'a.jpg' }, { file: 'b.jpg' }],
};

describe('faceSection (флаг character-smart-photos)', () => {
  it('выключен — пустой объект: как master (без согласия-меток, без нажима, без редактора)', () => {
    expect(faceSection(false, ctx)).toEqual({});
  });

  it('включён, редактор закрыт — новое согласие + нажим + подписи, редактора нет', () => {
    const r = faceSection(true, { ...ctx, selected: null });
    expect(r.notice).toBe(CHARACTER_PHOTOS_NOTICE_LABELS);
    expect(typeof r.onSelectPhoto).toBe('function');
    expect(typeof r.renderCaption).toBe('function');
    expect(r.selectedKey).toBeNull();
    expect(r.labelsEditor).toBeUndefined();
  });

  it('включен, фото выбрано — редактор меток на месте', () => {
    const r = faceSection(true, { ...ctx, selected: 'b.jpg' });
    expect(r.selectedKey).toBe('b.jpg');
    expect(r.labelsEditor).toBeTruthy();
  });

  it('onSelectPhoto переключает выбор: повторный тап по тому же фото закрывает', () => {
    let cur: string | null = null;
    const r = faceSection(true, { ...ctx, selected: null, setSelected: fn => { cur = fn(cur); } });
    r.onSelectPhoto!({ key: 'b.jpg', url: '/b.jpg', primary: false });
    expect(cur).toBe('b.jpg');
    r.onSelectPhoto!({ key: 'b.jpg', url: '/b.jpg', primary: false });
    expect(cur).toBeNull();
  });
});
