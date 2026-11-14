// F28 (доработка по вердикту R-F28): сброс ячеек моделей —
//   • вторая отправка не уходит (захват права на отправку в форме, не только в хелпере);
//   • отказ PUT показывается ошибкой, текст ответа сохраняется целиком;
//   • ответ, пришедший после размонтирования экземпляра формы, не трогает ни форму,
//     ни родителя (правка чужой персоны не закрывается).
//
// Тесты идут через путь формы (performTierReset — тот же код, что зовёт компонент),
// поэтому снятие защит в форме (захват отправки, показ ошибки) красит их.

import { act, createElement } from 'react';
import { describe, expect, it } from 'vitest';
import { performTierReset, useMountedRef } from '../PersonaForm';
import { settleTierReset } from '../personaInheritModel';
import { installFakeDom, mount, uninstallFakeDom } from '../../../components/chat/__tests__/fakeDom';

interface Deps {
  flag: { current: boolean };
  put: () => Promise<unknown>;
  isAlive: () => boolean;
  setError: (m: string | null) => void;
  onApplied: (s: unknown) => void;
}

function deps(over: Partial<Deps> = {}): Deps {
  return {
    flag: { current: false },
    put: async () => ({ id: 'per-1' }),
    isAlive: () => true,
    setError: () => {},
    onApplied: () => {},
    ...over,
  };
}

describe('F28: отправка сброса ячеек — путь формы', () => {
  it('вторая отправка не уходит: PUT ровно один, применён один ответ', async () => {
    let puts = 0;
    const applied: unknown[] = [];
    const d = deps({
      put: async () => { puts += 1; await Promise.resolve(); return { id: 'per-1' }; },
      onApplied: (s) => { applied.push(s); },
    });
    await Promise.all([performTierReset(d), performTierReset(d)]);
    expect(puts).toBe(1);
    expect(applied.length).toBe(1);
  });

  it('отказ: ошибка показана, текст ответа сохранён, ответ не применён', async () => {
    const errors: (string | null)[] = [];
    const applied: unknown[] = [];
    await performTierReset(deps({
      put: async () => { throw new Error('уровни заняты другим ходом'); },
      setError: (m) => { errors.push(m); },
      onApplied: (s) => { applied.push(s); },
    }));
    expect(errors).toContain('уровни заняты другим ходом');
    expect(applied).toEqual([]);
  });

  it('ответ после размонтирования: onSaved не зовётся, ошибки нет', async () => {
    const errors: (string | null)[] = [];
    const applied: unknown[] = [];
    await performTierReset(deps({
      isAlive: () => false,
      setError: (m) => { errors.push(m); },
      onApplied: (s) => { applied.push(s); },
    }));
    expect(applied).toEqual([]);
    expect(errors.filter(e => e !== null)).toEqual([]);
  });
});

describe('F28: чистая функция исхода PUT', () => {
  it('успех — применяем, отказ — ошибка, умерший экземпляр — молча', () => {
    expect(settleTierReset({ ok: true, saved: { id: 'per-1' } }, true)).toEqual({
      apply: true, error: null, saved: { id: 'per-1' },
    });
    expect(settleTierReset({ ok: false, error: 'отказ сервера' }, true)).toEqual({
      apply: false, error: 'отказ сервера',
    });
    expect(settleTierReset({ ok: true, saved: { id: 'per-1' } }, false)).toEqual({
      apply: false, error: null,
    });
  });
});

describe('F28: живость экземпляра формы', () => {
  it('после размонтирования экземпляр мёртв', () => {
    installFakeDom();
    try {
      const holder: { ref: { current: boolean } | null } = { ref: null };
      const Probe = () => { holder.ref = useMountedRef(); return null; };
      const { root } = mount(createElement(Probe));
      expect(holder.ref?.current).toBe(true);
      act(() => { root.unmount(); });
      expect(holder.ref?.current).toBe(false);
    } finally {
      uninstallFakeDom();
    }
  });
});
