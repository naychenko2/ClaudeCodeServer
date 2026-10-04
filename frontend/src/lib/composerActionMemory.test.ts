import { describe, expect, it } from 'vitest';
import { modeSubmitButton, nextPrefill, type PrefillState } from './composerActionMemory';
import type { ActionMode } from './chatContext/actionMode';

const EMPTY: PrefillState = { key: null, auto: null };

// Прогон рендеров композера: каждый шаг — что вернул prefill и что сейчас в поле
function run(steps: { next: { key: string; text: string | null } | null; field?: string }[]): string {
  let state = EMPTY;
  let field = '';
  for (const s of steps) {
    if (s.field !== undefined) field = s.field;
    const r = nextPrefill(state, s.next, field);
    state = r.state;
    field = r.field;
  }
  return field;
}

describe('затравка поля режима', () => {
  it('подставляется в пустое поле', () => {
    expect(run([{ next: { key: 'x', text: 'p' } }])).toBe('p');
    expect(run([{ next: { key: 'x', text: 'p' }, field: '   ' }])).toBe('p');
  });

  it('набранный человеком текст не перезаписывается', () => {
    expect(run([{ next: { key: 'x', text: 'p' }, field: 'моё' }])).toBe('моё');
  });

  it('первый запуск свежей нити после отправки не возвращает промпт в очищенное поле', () => {
    expect(run([
      { next: { key: 'd1', text: null } },             // пустой черновик: повод есть, текста нет
      { next: { key: 'd1', text: null }, field: 'нарисуй кота' }, // человек печатает
      { next: { key: 'd1', text: null }, field: '' },  // отправил — ядро очистило поле
      { next: { key: 'd1', text: 'нарисуй кота' } },   // пришёл первый запуск
    ])).toBe('');
  });

  it('тот же повод второй раз не подставляет', () => {
    expect(run([
      { next: { key: 't1', text: 'a' } },
      { next: { key: 't1', text: 'a' }, field: '' },
    ])).toBe('');
  });

  it('смена нити заменяет нетронутую затравку прежней нити', () => {
    expect(run([
      { next: { key: 't1', text: 'закат' } },
      { next: { key: 't2', text: 'рассвет' } },
    ])).toBe('рассвет');
    expect(run([
      { next: { key: 't1', text: 'закат' } },
      { next: { key: 'd1', text: null } },
    ])).toBe('');
  });

  it('смена нити не трогает поправленную человеком затравку', () => {
    expect(run([
      { next: { key: 't1', text: 'закат' } },
      { next: { key: 't2', text: 'рассвет' }, field: 'закат над морем' },
    ])).toBe('закат над морем');
  });

  it('без повода (нет нити) поле и повод не меняются', () => {
    expect(run([
      { next: { key: 't1', text: 'a' } },
      { next: null, field: '' },
      { next: { key: 't1', text: 'a' } },
    ])).toBe('');
  });
});

describe('кнопка строки режима и emptySubmit', () => {
  const ctx = { projectId: 'p', sessionId: 's' };
  const base: ActionMode = {
    title: 'Картинка', icon: null, placeholder: () => '', onSubmit: () => {},
    submitLabel: () => 'Изменить',
  };
  const again = { label: 'Ещё 2', run: () => {} };

  it('без вклада emptySubmit — прежнее поведение: подпись режима, на пустом поле кнопка гаснет', () => {
    expect(modeSubmitButton(base, ctx, false, false)).toEqual({ kind: 'text', disabled: true, label: 'Изменить', run: null });
    expect(modeSubmitButton(base, ctx, true, false)).toEqual({ kind: 'text', disabled: false, label: 'Изменить', run: null });
    expect(modeSubmitButton(base, ctx, true, true).disabled).toBe(true);
    // Без submitLabel — стрелка ядра (label null)
    expect(modeSubmitButton({ ...base, submitLabel: undefined }, ctx, true, false).label).toBeNull();
  });

  it('вклад вернул null — как без вклада', () => {
    expect(modeSubmitButton({ ...base, emptySubmit: () => null }, ctx, false, false))
      .toEqual({ kind: 'text', disabled: true, label: 'Изменить', run: null });
  });

  it('пустое поле и вклад есть — его подпись и запуск, кнопка активна', () => {
    const b = modeSubmitButton({ ...base, emptySubmit: () => again }, ctx, false, false);
    expect(b).toEqual({ kind: 'empty', disabled: false, label: 'Ещё 2', run: again.run });
  });

  it('с текстом вклад не спрашивается: отправка набранного', () => {
    let asked = 0;
    const b = modeSubmitButton({ ...base, emptySubmit: () => { asked++; return again; } }, ctx, true, false);
    expect(b.kind).toBe('text');
    expect(asked).toBe(0);
  });

  it('ход недоступен — и «Ещё» гаснет', () => {
    expect(modeSubmitButton({ ...base, emptySubmit: () => again }, ctx, false, true).disabled).toBe(true);
  });
});
