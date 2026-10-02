import { describe, expect, it } from 'vitest';
import { C } from '../../../lib/design';
import { pillStyle } from './OpSection';

describe('пилюли операций', () => {
  it('невыбранная — на фоне поверхности, выбранная — на accent-light', () => {
    expect(pillStyle(false)).toMatchObject({ background: C.bgWhite, border: `1px solid ${C.border}` });
    expect(pillStyle(true)).toMatchObject({ background: C.accentLight, border: `1px solid ${C.accent}` });
  });

  it('недоступная — пунктир и пониженная непрозрачность', () => {
    const s = pillStyle(false, true);
    expect(s.border).toContain('dashed');
    expect(s.opacity).toBeLessThan(0.7);
    expect(pillStyle(false).opacity).toBeUndefined();
  });
});
