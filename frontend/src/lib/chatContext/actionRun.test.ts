import { beforeEach, describe, expect, it, vi } from 'vitest';

const h = vi.hoisted(() => ({ toast: vi.fn() }));
vi.mock('../offline', () => ({ request: vi.fn() }));
vi.mock('../signalr', () => ({ onMessage: () => () => {}, onReconnected: () => () => {} }));
vi.mock('../toast', () => ({ showToast: h.toast }));

import { objectKey, rememberAction, resetActionMemory, resolveAction } from './actionMemory';
import { buildActionRun, ensureQuote, resetAllRuns, runBlockReason, runLabel, runLabelParts } from './actionRun';
import { ReportedError } from './errors';
import { __applyChatContext, __resetChatContextStore, getChatContextState } from './store';
import type {
  ChatContextPrimary, ContextAction, ContextKindApi, LaunchHandle, LaunchParam, QuoteRequest,
} from './types';

const primary = (ref: Record<string, unknown> = { threadId: 't', versionId: 'v1' }): ChatContextPrimary => ({
  id: 'p1', kind: 'image', ref, by: 'human', addedAt: '', label: 'hero.png', version: 'v1', thumb: null, missing: false, role: null,
});
const edit: ContextAction = { id: 'edit', kind: 'run', label: 'Изменить', hint: 'h', op: 'edit', text: 'required', placeholder: 'Что изменить на картинке…' };
const stems: ContextAction = {
  id: 'stems', kind: 'run', label: 'Стемы', hint: 'h', op: 'separate', text: 'none',
  question: { param: 'stemSet', title: 'Набор', options: [{ value: 'vocals', label: 'Вокал + минус' }, { value: '4', label: '4 стема' }] },
};
const params: LaunchParam[] = [{ kind: 'variants', min: 1, max: 4, value: 1 }, { kind: 'duration', options: [4, 8], value: 8 }];
const ctx = { projectId: 'p', sessionId: 's1', isMobile: false };

const handle: LaunchHandle = { id: 'job1', watch: () => () => {} };
function apiOf(over: Partial<ContextKindApi> = {}, actions: ContextAction[] = [edit]): ContextKindApi & { launch: ReturnType<typeof vi.fn>; quote: ReturnType<typeof vi.fn> } {
  return {
    kinds: ['image'], icon: () => null, preview: () => null, actions: () => actions, params: () => params,
    launch: vi.fn(async () => handle), quote: vi.fn(async () => ({ price: '$0.12' })), ...over,
  } as never;
}
const build = (api: ContextKindApi, over: { mobile?: boolean; p?: ChatContextPrimary; revision?: number } = {}) =>
  buildActionRun({ sessionId: 's1', api, ctx: { ...ctx, isMobile: over.mobile ?? false }, primary: over.p ?? primary(), refs: [], revision: over.revision ?? 7 });

beforeEach(() => {
  resetActionMemory(); resetAllRuns(); __resetChatContextStore(); h.toast.mockReset();
});

describe('подпись кнопки (Р3): одна функция', () => {
  const base = { action: edit, params: [] as LaunchParam[], price: null as string | null, mobile: false, state: 'idle' as const, progress: null };
  it('варианты и цена: «✦ Изменить · 3 вар. · $0.12», на телефоне «×3»', () => {
    const p: LaunchParam[] = [{ kind: 'variants', min: 1, max: 4, value: 3 }];
    expect(runLabel({ ...base, params: p, price: '$0.12' })).toBe('✦ Изменить · 3 вар. · $0.12');
    expect(runLabel({ ...base, params: p, price: '$0.12', mobile: true })).toBe('✦ Изменить · ×3 · $0.12');
  });
  it('один вариант не пишется, длительность — всегда, порядок «длительность · варианты · цена»', () => {
    expect(runLabel({ ...base, params: [{ kind: 'variants', min: 1, max: 4, value: 1 }], price: 'бесплатно' })).toBe('✦ Изменить · бесплатно');
    const scene: LaunchParam[] = [{ kind: 'duration', options: [4, 8], value: 8 }, { kind: 'variants', min: 1, max: 4, value: 2 }];
    expect(runLabel({ ...base, action: { ...edit, label: 'Снять' }, params: scene, price: '$3.20' })).toBe('✦ Снять · 8 с · 2 вар. · $3.20');
  });
  it('во время хода — прогресс; без действия подписи нет', () => {
    expect(runLabel({ ...base, state: 'running', progress: 0.4 })).toBe('✦ Изменить… 40 %');
    expect(runLabel({ ...base, action: null })).toBe('');
  });
  it('кнопка серая: обязательный текст и серое действие', () => {
    expect(runBlockReason(edit, '  ')).toBe('Напишите текст: Что изменить на картинке…');
    expect(runBlockReason(edit, 'x')).toBeNull();
    expect(runBlockReason({ ...edit, disabledReason: 'Выделите кусок' }, 'x')).toBe('Выделите кусок');
    expect(runBlockReason(null, '')).toBeNull();
  });
});

describe('части подписи (Р3): имя ужимается, хвост с ценой — никогда', () => {
  const base = { action: edit, params: [{ kind: 'variants', min: 1, max: 4, value: 3 }] as LaunchParam[], price: '$0.12', mobile: true, state: 'idle' as const, progress: null };
  it('имя и хвост разделены; склейка равна подписи', () => {
    const p = runLabelParts(base);
    expect(p).toEqual({ name: '✦ Изменить', tail: ' · ×3 · $0.12' });
    expect(p.name + p.tail).toBe(runLabel(base));
  });
  it('во время хода хвост — процент', () => {
    expect(runLabelParts({ ...base, state: 'running', progress: 0.4 })).toEqual({ name: '✦ Изменить…', tail: ' 40 %' });
  });
});

describe('паритет поля ввода и низа панели (useActionRun)', () => {
  it('на одном состоянии обе точки получают одну подпись, одну цену и один запуск', async () => {
    const api = apiOf();
    const field = build(api);
    const panel = build(api);
    expect(panel.label).toBe(field.label);
    // Текст публикует поле, панель запускает с ним же
    field.setText('убери тень');
    await ensureQuote('s1', field.scope, api, ctx, { ...field.req!, text: 'убери тень' });
    const a = build(api);
    const b = build(api);
    expect(a.label).toBe('✦ Изменить · 8 с · $0.12');
    expect(b.label).toBe(a.label);
    expect(b.text).toBe('убери тень');
    await a.run(a.text);
    expect(api.launch).toHaveBeenCalledTimes(1);
    expect(api.launch).toHaveBeenCalledWith(ctx, { op: 'edit', text: 'убери тень', params: { variants: 1, duration: 8 }, contextRevision: 7 });
  });

  it('параметры панели меняют подпись поля: «Вариантов» живёт в одном месте', () => {
    const api = apiOf();
    const field = build(api);
    field.setParam('variants', 3);
    expect(build(api).label).toContain('3 вар.');
    expect(build(api, { mobile: true }).label).toContain('×3');
  });

  it('вопрос под чипами: первое значение предвыбрано и уходит в params', async () => {
    const api = apiOf({}, [stems]);
    const r = build(api);
    expect(r.answer).toBe('vocals');
    r.setParam('stemSet', '4');
    const next = build(api);
    expect(next.answer).toBe('4');
    await next.run('');
    expect(api.launch).toHaveBeenCalledWith(ctx, expect.objectContaining({ op: 'separate', params: expect.objectContaining({ stemSet: '4' }) }));
  });

  it('без вида или без выбранного действия запуска и подписи нет', () => {
    expect(buildActionRun({ sessionId: 's1', api: null, ctx, primary: primary(), refs: [], revision: 1 }).label).toBe('');
    const chat = { ...apiOf() };
    // Ручной «Чат»: действие не выбрано
    resolveAction('s1', objectKey(primary()), 'agent', [edit]);
    expect(build(chat, { p: { ...primary(), by: 'agent' } }).action).toBeNull();
  });
});

describe('подписка на ход запуска и carry', () => {
  it('после result подписка снимается; по ошибке хода carry гаснет', async () => {
    const off = vi.fn();
    let emit!: (e: { progress?: number; result?: { summary: string }; error?: string }) => void;
    const api = apiOf({ launch: vi.fn(async () => ({ id: 'j', watch: (on: typeof emit) => { emit = on; return off; } })) as never }, [stems, edit]);
    const r = build(api);
    await r.run('x');
    emit({ result: { summary: 'готово' } });
    expect(off).toHaveBeenCalledTimes(1);
    // Новый запуск и ошибка хода: выбор за версией не едет — берётся умолчание.
    // Выбор «Изменить» ставится ДО сборки запуска, иначе carry не получает действия edit
    resetAllRuns(); resetActionMemory();
    rememberChoiceEdit();
    const r2 = build(apiOf({ launch: vi.fn(async () => ({ id: 'j', watch: (on: typeof emit) => { emit = on; return off; } })) as never }, [stems, edit]));
    expect(r2.action?.id).toBe('edit');
    await r2.run('x');
    emit({ error: 'сбой' });
    const v2 = primary({ threadId: 't', versionId: 'v2' });
    expect(resolveAction('s1', objectKey(v2), 'human', [stems, edit]).actionId).toBe('stems');
  });
});

function rememberChoiceEdit() { rememberAction('s1', objectKey(primary()), 'edit'); }

describe('409 context_changed и выбор после запуска', () => {
  const conflict = (revision: number) => Object.assign(new Error('Conflict'), {
    status: 409, body: { error: 'context_changed', context: { revision, primary: primary(), refs: [] } },
  });

  it('запуск на чужой ревизии: DTO перечитан из тела, цена сброшена, запуск сам не повторяется', async () => {
    const api = apiOf({ launch: vi.fn().mockRejectedValue(conflict(9)) as never });
    __applyChatContext('s1', { revision: 7, primary: primary(), refs: [] });
    const r = build(api);
    await expect(r.run('x')).rejects.toBeTruthy();
    expect(api.launch).toHaveBeenCalledTimes(1);
    expect(getChatContextState('s1').revision).toBe(9);
    expect(build(api).state).toBe('idle');
    expect(h.toast).toHaveBeenCalled();
  });

  it('отказ, причину которого вертикаль уже показала, второго тоста не даёт', async () => {
    const api = apiOf({ launch: vi.fn().mockRejectedValue(new ReportedError('Генерация не запущена')) as never });
    __applyChatContext('s1', { revision: 7, primary: primary(), refs: [] });
    await expect(build(api).run('x')).rejects.toThrow('Генерация не запущена');
    expect(h.toast).not.toHaveBeenCalled();
    expect(build(api).state).toBe('error');
  });

  it('солт вида входит в ключ цены: другие отметки — другой запрос цены', () => {
    let salt = 'a';
    const api = apiOf({ priceSalt: () => salt } as never);
    __applyChatContext('s1', { revision: 7, primary: primary(), refs: [] });
    const first = JSON.stringify(build(api).req);
    salt = 'b';
    expect(JSON.stringify(build(api).req)).not.toBe(first);
  });

  it('запрос цены на устаревшей ревизии молча подтягивает свежий контекст, без тоста', async () => {
    const api = apiOf({ quote: vi.fn().mockRejectedValue(conflict(10)) as never });
    __applyChatContext('s1', { revision: 7, primary: primary(), refs: [] });
    const r = build(api);
    await ensureQuote('s1', r.scope, api, ctx, r.req as QuoteRequest);
    expect(getChatContextState('s1').revision).toBe(10);
    expect(h.toast).not.toHaveBeenCalled();
  });

  it('ответы цены не по порядку: старый ответ не затирает цену текущего запроса', async () => {
    let resolveA!: (v: { price: string }) => void;
    const quote = vi.fn()
      .mockImplementationOnce(() => new Promise(r => { resolveA = r; }))
      .mockResolvedValueOnce({ price: '$0.20' });
    const api = apiOf({ quote: quote as never });
    const r = build(api);
    const a = ensureQuote('s1', r.scope, api, ctx, { ...r.req!, text: 'a' });
    await ensureQuote('s1', r.scope, api, ctx, { ...r.req!, text: 'ab' });
    resolveA({ price: '$0.10' });
    await a;
    r.setText('ab');
    expect(build(api).quote?.price).toBe('$0.20');
  });

  it('тот же запрос цены второй раз не уходит', async () => {
    const api = apiOf();
    const r = build(api);
    await ensureQuote('s1', r.scope, api, ctx, r.req!);
    await ensureQuote('s1', r.scope, api, ctx, r.req!);
    expect(api.quote).toHaveBeenCalledTimes(1);
  });

  it('после запуска выбор остаётся, если у новой версии есть то же действие, иначе «Чат»', async () => {
    const api = apiOf();
    const r = build(api);
    await r.run('x');
    // Новая версия: тот же объект другой ref → другой ключ
    const v2 = primary({ threadId: 't', versionId: 'v2' });
    expect(resolveAction('s1', objectKey(v2), 'human', [edit]).actionId).toBe('edit');
    // У версии со стемами «Изменить» нет — «Чат»
    resetActionMemory(); resetAllRuns();
    const r2 = build(api);
    await r2.run('x');
    expect(resolveAction('s1', objectKey(v2), 'human', [{ ...edit, id: 'mix' }]).actionId).toBeNull();
  });
});
