import { describe, expect, it } from 'vitest';
import { nextComposerMode, nextPrefill, type ComposerModeEntry, type ComposerModeSeen, type PrefillState } from './composerModes';
import type { ComposerModeApi } from './subsystems/registryCore';

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

// Режим-заглушка: повод задаёт тест, доступность — фильтр по списку, как в Composer
const CTX = { projectId: null, sessionId: 's1' };
const mode = (name: string, reason: () => string | null): ComposerModeEntry =>
  ({ name, action: { autoSelect: () => reason() } as unknown as ComposerModeApi });

describe('самовключение режима поля', () => {
  it('новый повод включает режим, тот же повод после ручного «Чата» — нет', () => {
    const m = mode('image', () => 'request:1');
    const first = nextComposerMode([m], CTX, {}, null);
    expect(first.modeId).toBe('image');
    expect(nextComposerMode([m], CTX, first.seen, null).modeId).toBeNull();
  });

  it('режим пропал и вернулся с тем же поводом — не навязывается повторно', () => {
    const m = mode('image', () => 'request:1');
    const first = nextComposerMode([m], CTX, {}, null);
    // Человек ушёл в «Чат», затем режим временно недоступен (снят фокус)
    const gone = nextComposerMode([], CTX, first.seen, null);
    expect(gone.modeId).toBeNull();
    const back = nextComposerMode([m], CTX, gone.seen, null);
    expect(back.modeId).toBeNull();
  });

  it('режим пропал и вернулся с новым поводом — включается', () => {
    let n = 1;
    const m = mode('image', () => `request:${n}`);
    const first = nextComposerMode([m], CTX, {}, null);
    const gone = nextComposerMode([], CTX, first.seen, null);
    n = 2;
    expect(nextComposerMode([m], CTX, gone.seen, null).modeId).toBe('image');
  });

  it('повод другого режима не стирает память первого', () => {
    let active: 'a' | 'b' = 'a';
    const a = mode('a', () => (active === 'a' ? 'request:1' : null));
    const b = mode('b', () => (active === 'b' ? 'request:1' : null));
    let seen: ComposerModeSeen = nextComposerMode([a, b], CTX, {}, null).seen;
    active = 'b';
    const viaB = nextComposerMode([a, b], CTX, seen, null);
    expect(viaB.modeId).toBe('b');
    seen = viaB.seen;
    active = 'a';
    expect(nextComposerMode([a, b], CTX, seen, null).modeId).toBeNull();
  });
});
