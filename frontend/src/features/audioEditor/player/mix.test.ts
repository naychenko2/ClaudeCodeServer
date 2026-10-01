import { describe, expect, it } from 'vitest';
import { EMPTY_MIXER, mixPlan, setGain, toggleMute, toggleSolo, type MixerState } from './mix';

const STEMS = [
  { id: 'v', name: 'вокал' },
  { id: 'd', name: 'барабаны' },
  { id: 'b', name: 'бас' },
  { id: 'o', name: 'прочее' },
];

describe('что сводим', () => {
  it('по умолчанию сводим все стемы', () => {
    const p = mixPlan(STEMS, EMPTY_MIXER);
    expect(p.stems.map(s => s.id)).toEqual(['v', 'd', 'b', 'o']);
    expect(p.canMix).toBe(true);
    expect(p.total).toBe(4);
  });

  it('M исключает стем из сведения', () => {
    const p = mixPlan(STEMS, toggleMute(EMPTY_MIXER, 'd'));
    expect(p.stems.map(s => s.id)).toEqual(['v', 'b', 'o']);
  });

  it('S оставляет в сведении только солирующий стем', () => {
    const p = mixPlan(STEMS, toggleSolo(EMPTY_MIXER, 'b'));
    expect(p.stems.map(s => s.id)).toEqual(['b']);
  });

  it('S перекрывает M: заглушённый солирующий стем звучит и сводится', () => {
    const st = toggleSolo(toggleMute(EMPTY_MIXER, 'v'), 'v');
    expect(mixPlan(STEMS, st).stems.map(s => s.id)).toEqual(['v']);
  });

  it('повторный S снимает соло — заглушки снова в силе', () => {
    const st = toggleSolo(toggleSolo(toggleMute(EMPTY_MIXER, 'v'), 'v'), 'v');
    expect(mixPlan(STEMS, st).stems.map(s => s.id)).toEqual(['d', 'b', 'o']);
  });

  it('все заглушены → сводить нечего, кнопка серая', () => {
    const st = STEMS.reduce<MixerState>((acc, s) => toggleMute(acc, s.id), EMPTY_MIXER);
    const p = mixPlan(STEMS, st);
    expect(p.canMix).toBe(false);
    expect(p.stems).toEqual([]);
    expect(p.description).toBe('');
  });

  it('громкость попадает в план и подпись, с ограничением −24…+6 дБ', () => {
    let st = setGain(EMPTY_MIXER, 'v', -3);
    st = setGain(st, 'd', 12);
    st = setGain(st, 'b', -40);
    const p = mixPlan(STEMS, st);
    expect(p.stems.map(s => s.gainDb)).toEqual([-3, 6, -24, 0]);
    expect(p.description).toBe('вокал −3 дБ, барабаны +6 дБ, бас −24 дБ, прочее');
  });
});
