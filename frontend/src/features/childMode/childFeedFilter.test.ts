// Ф1d: фильтр детской ленты — чистая функция, кейсы перенесены из ChildLessonChat.test.tsx
// («сбой хода в ленте», «виджет и ошибки инструментов»), которые проверяли то же решение
// через рендер (удалён в Ф1g). Здесь — само решение: что проходит в ленту, что помечается
// плашкой, что опускается. DOM не нужен: как выглядит плашка (Notice + текст) — не здесь.
//
// Белый список задаёт тест сам (зеркало ChildModeAllowList.Surface.FeedKinds, бэк):
// смена бэкового списка без этой правки краснит тест — согласованность видна здесь.

import { describe, expect, it } from 'vitest';
import { filterChildFeed, CHILD_TURN_ERROR_TEXT } from './childFeedFilter';
import type { ChatItem } from '../../types';

const FEED_KINDS = [
  'user_message', 'text', 'result', 'error', 'error_group', 'rate_limit',
  'provider_limit', 'model_switched', 'work_loop_stopped', 'hands_notice', 'tool_use',
];

const run = (items: unknown[]) => filterChildFeed(items as ChatItem[], FEED_KINDS);
// Что осталось в ленте: «kind#tool» — по образцу civ() из ChildLessonChat.test.tsx
const kept = (entries: ReturnType<typeof filterChildFeed>) =>
  entries.map(e => `${e.item.kind}${e.item.kind === 'tool_use' ? '#' + e.item.name : ''}`);

describe('filterChildFeed: белый список видов', () => {
  it('вид из feedKinds остаётся, неизвестный вид — нет; индексы исходной ленты сохранены', () => {
    const entries = run([
      { kind: 'user_message', text: 'привет' },
      { kind: 'brand_new_kind', text: 'секрет' },
      { kind: 'session_started', model: 'm', mode: 'default' },
      { kind: 'text', text: 'здравствуй' },
    ]);
    expect(kept(entries)).toEqual(['user_message', 'text']);
    expect(entries.map(e => e.i)).toEqual([0, 3]);
    expect(entries.every(e => !e.plate)).toBe(true);
  });

  it('карточка правки файла (file_changed) — вне белого списка, опускается', () => {
    const entries = run([
      { kind: 'text', text: 'поправил' },
      { kind: 'file_changed', path: 'a/b.ts', added: 1, removed: 2 },
    ]);
    expect(kept(entries)).toEqual(['text']);
  });

  it.each([
    { kind: 'rate_limit', limitType: 'x' },
    { kind: 'provider_limit', providers: [] },
    { kind: 'model_switched', model: 'a', previousModel: 'b' },
    { kind: 'work_loop_stopped', reason: 'r', text: 'technical' },
    { kind: 'hands_notice', text: 'technical', tone: 'warning' },
    { kind: 'error', text: 'Invalid API key · Fix external API key' },
    { kind: 'error_group', date: 0, items: [{ kind: 'error', text: 'ошибка авторизации' }] },
  ])('технический вид $kind — в ленте помечен плашкой', item => {
    const entries = run([item]);
    expect(entries.length).toBe(1);
    expect(entries[0].plate).toBe(true);
    expect(entries[0].plateText).toBeUndefined();
  });

  // К9f (дефект 5f7854e7): отказ маршрута — обещание ребёнку, его текст идёт в плашку как есть.
  // Узнаём по action (контракт с бэком, ChildRoute.cs), а не по тексту: формулировки на бэке
  // могут меняться. Рендер plateText в ChatPanel — К9g.
  it('error с action child-route-refusal — плашка с текстом отказа как есть', () => {
    const refusal = 'Занятие пока недоступно, взрослым отправлено уведомление.';
    const entries = run([{ kind: 'error', text: refusal, action: 'child-route-refusal' }]);
    expect(entries.length).toBe(1);
    expect(entries[0].plate).toBe(true);
    expect(entries[0].plateText).toBe(refusal);
  });

  it('error без action и error с чужим action — общая плашка без plateText', () => {
    const entries = run([
      { kind: 'error', text: 'Invalid API key · Fix external API key' },
      { kind: 'error', text: 'Проект больше не в сфере «Работа» — смените собеседника', action: 'change-companion' },
    ]);
    expect(entries.map(e => e.plate)).toEqual([true, true]);
    expect(entries.map(e => e.plateText)).toEqual([undefined, undefined]);
  });

  it('успешный result — не плашка; result с ошибкой (401) — плашка', () => {
    const entries = run([
      { kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1 },
      { kind: 'result', subtype: 'error_during_execution', apiErrorStatus: '401', durationMs: 1, numTurns: 1 },
    ]);
    expect(entries.map(e => e.plate)).toEqual([false, true]);
  });

  it('спокойный текст ошибки (на «ты», без API-терминов)', () => {
    expect(CHILD_TURN_ERROR_TEXT).toContain('Попробуй');
    expect(CHILD_TURN_ERROR_TEXT).not.toMatch(/API|ключ|error_/i);
  });

  it('пустой белый список default-deny: ничего не показывается', () => {
    expect(filterChildFeed([{ kind: 'user_message', text: 'привет' } as ChatItem], [])).toEqual([]);
    expect(filterChildFeed([{ kind: 'text', text: 'x' } as ChatItem], [])).toEqual([]);
  });
});

// R-Б18a: бэковый FeedKinds перечисляет tool_use как вид; условие «только widget_show
// без ошибки» держится на фронте — иначе ребёнок увидел бы ошибки инструментов.
describe('filterChildFeed: tool_use (К8b, §8 п.5)', () => {
  it('widget_show без ошибки — в ленте на своём месте; другой tool_use — нет', () => {
    const entries = run([
      { kind: 'text', text: 'посмотри схему' },
      { kind: 'tool_use', id: 'w1', name: 'mcp__widgets__widget_show', input: { html: '<b>схема</b>' } },
      { kind: 'tool_use', id: 't1', name: 'mcp__tutor__read_material', input: { id: 'm' } },
    ]);
    expect(kept(entries)).toEqual(['text', 'tool_use#mcp__widgets__widget_show']);
    expect(entries.every(e => !e.plate)).toBe(true);
  });

  it('виджет с isError — ни карточки, ни плашки', () => {
    const entries = run([
      { kind: 'tool_use', id: 'w2', name: 'mcp__widgets__widget_show', input: { html: '<b>схема</b>' }, isError: true, result: 'technical' },
    ]);
    // Мутация «убрать && !item.isError»: виджет с ошибкой прошёл бы в ленту — строка краснеет
    expect(entries.length).toBe(0);
  });

  it('сбой отдельного инструмента — без плашки; сбой хода — плашка как раньше', () => {
    const entries = run([
      { kind: 'tool_use', id: 's1', name: 'mcp__tutor__search_materials', input: {}, isError: true, result: 'technical' },
      { kind: 'error', text: 'Invalid API key · Fix external API key' },
    ]);
    expect(kept(entries)).toEqual(['error']);
    expect(entries.map(e => e.plate)).toEqual([true]);
  });
});
