import { describe, expect, it } from 'vitest';
import { parseVgl, refineLabel, VGL_FIELDS, vglFieldValue } from './vgl';

const scene = parseVgl(JSON.stringify({
  short_description: 'Женщина в красном пальто',
  objects: [{ description: 'женщина', clothing: 'crimson woolen coat', shape_and_color: 'tall, red' }],
  background_setting: 'old European street',
  lighting: { conditions: 'dusk', direction: 'side-lit from left', shadows: 'soft' },
  photographic_characteristics: { camera_angle: 'eye-level', lens_focal_length: 'standard lens' },
  aesthetics: { composition: 'rule of thirds' },
  style_medium: 'photograph',
  artistic_style: 'realistic',
}));

describe('доводка Fibo 2.0: поля JSON-описания', () => {
  it('ключи панели совпадают с серверными Fibo2VglFields', () => {
    expect(VGL_FIELDS.map(f => f.key)).toEqual(['light', 'camera', 'subject', 'background', 'style']);
  });

  it('показывает, что сейчас в описании по каждому полю', () => {
    expect(vglFieldValue(scene, 'light')).toBe('dusk; side-lit from left');
    expect(vglFieldValue(scene, 'camera')).toBe('eye-level; standard lens; rule of thirds');
    expect(vglFieldValue(scene, 'subject')).toBe('crimson woolen coat');
    expect(vglFieldValue(scene, 'background')).toBe('old European street');
    expect(vglFieldValue(scene, 'style')).toBe('photograph; realistic');
  });

  it('у главного объекта без одежды берёт форму и цвет', () => {
    const vase = parseVgl('{"objects":[{"shape_and_color":"round blue vase"}]}');
    expect(vglFieldValue(vase, 'subject')).toBe('round blue vase');
  });

  it('битый JSON и пустые поля — без падения', () => {
    expect(parseVgl('{не json')).toBeNull();
    expect(parseVgl('[1,2]')).toBeNull();
    expect(vglFieldValue(null, 'light')).toBe('');
    expect(vglFieldValue(parseVgl('{}'), 'camera')).toBe('');
  });

  it('подпись запуска называет поле и новое значение', () => {
    expect(refineLabel('light', '  закат ')).toBe('Доводка: свет → закат');
  });
});
