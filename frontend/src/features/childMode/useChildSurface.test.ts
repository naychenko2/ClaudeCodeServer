import { describe, expect, it } from 'vitest';
import { useChildSurface } from './useChildSurface';
import type { ChildSurface } from '../account/types';

// Разбор — чистая функция без состояния и опросов (useChildSurface.ts), поэтому кейсы —
// прямые вызовы на формах me-ответа: jsdom и testing-library не нужны (среда vitest — node).
// Формы соответствуют серверу: взрослый — childSurface: null (Me_Adult_ReturnsNullChildSurface),
// ребёнок — объект (Me_Child_ReturnsFrozenChildSurface), «нет связи» — поля нет вовсе.

const surface: ChildSurface = {
  panels: ['files', 'knowledge', 'notes', 'chatContext', 'tasks'],
  composerFeatures: ['text', 'attachFromSubject', 'voiceInput', 'askUserAnswer'],
  feedKinds: ['user_message', 'text', 'result', 'error'],
};

describe('разбор детской поверхности (И11)', () => {
  it('взрослый: kind=adult — без снимка, независимо от childSurface', () => {
    expect(useChildSurface({ isChild: false, childSurface: null })).toEqual({ kind: 'adult' });
    // me ещё не пришёл (стор пуст) — тоже взрослый: детская оболочка не строится вслепую
    expect(useChildSurface(undefined)).toEqual({ kind: 'adult' });
    // аномалия (не ребёнок, но снимок пришёл): снимок не читается — не ребёнок
    expect(useChildSurface({ isChild: false, childSurface: surface })).toEqual({ kind: 'adult' });
  });

  it('ребёнок со снимком: kind=child и тот же surface, ключи не тронуты', () => {
    const r = useChildSurface({ isChild: true, childSurface: surface });
    expect(r.kind).toBe('child');
    if (r.kind !== 'child') return;
    expect(r.surface).toBe(surface); // тот же объект, не копия и не пересобранный
    expect(r.surface.panels).toEqual(surface.panels);
  });

  it('ребёнок без поля: kind=child-no-surface — default-deny, не взрослый набор', () => {
    expect(useChildSurface({ isChild: true })).toEqual({ kind: 'child-no-surface' });
    expect(useChildSurface({ isChild: true, childSurface: null })).toEqual({ kind: 'child-no-surface' });
  });
});
