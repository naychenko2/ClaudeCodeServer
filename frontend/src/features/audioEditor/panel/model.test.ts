import { describe, expect, it } from 'vitest';
import type { AudioCatalog, AudioModelInfo, AudioParamSchema, AudioPrefs, AudioProvider, AudioQuote, AudioThread } from '../api';
import {
  modelOptions, nextSettings, panelOps, pillOf, priceLines, providerOptions, pruneFields, resolvePanel, runReason,
  splitSchema, widgetOf, type PanelState, type ReasonInput,
} from './model';

const model = (id: string, ops: AudioModelInfo['caps']['ops'], extra: Partial<AudioModelInfo['caps']> = {}, hint?: AudioModelInfo['priceHint']): AudioModelInfo => ({
  id, label: id.toUpperCase(),
  caps: {
    ops, languages: ['ru', 'en'], voiceKinds: ['preset'], producesFiles: [], license: { label: 'Apache-2.0', kind: 'permissive' },
    priceUnit: hint?.unit ?? 'free', ...extra,
  },
  priceHint: hint ?? null,
});

const LOCAL: AudioProvider = {
  key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
  models: [
    model('qwen', ['speak', 'designVoice', 'cloneVoice']),
    model('ace', ['song', 'extract'], { heavyOps: ['extract'], languages: ['en'] }),
    model('demucs', ['separate'], { languages: [], languageNeutral: true }),
  ],
};
const FAL: AudioProvider = {
  key: 'fal', label: 'fal', priceUnit: 'usd', available: true, reason: null,
  models: [model('minimax', ['speak'], {}, { amount: 0.0001, unit: 'chars', per: 'char' })],
};
const YANDEX: AudioProvider = { key: 'yandex', label: 'Яндекс', priceUnit: 'rub', available: false, reason: 'Ключ не задан', models: [] };
const CATALOG: AudioCatalog = { providers: [LOCAL, FAL, YANDEX], autoModelId: 'auto', maxCount: 4 };
const NO_PREFS: AudioPrefs = { voice: null, music: null, process: null };

const thread = (settings: AudioThread['settings'], current: string | null = 'v1'): AudioThread => ({
  id: 't1', file: 'a.mp3', lineage: [], draftFolder: null, createdAt: '', versions: [], currentVersionId: current, launches: [], settings,
});

describe('операции режима', () => {
  it('источники озвучки — под пилюлей «Озвучить», громкость и сведение стемов — не пилюли', () => {
    const voice = panelOps('voice').map(o => o.op);
    expect(voice).toContain('speak');
    expect(voice).not.toContain('designVoice');
    expect(voice).not.toContain('cloneVoice');
    expect(pillOf('cloneVoice')).toBe('speak');
    const proc = panelOps('process').map(o => o.op);
    expect(proc).toEqual(expect.arrayContaining(['trim', 'concat', 'separate', 'transcribe']));
    expect(proc).not.toContain('gainFade');
    expect(proc).not.toContain('mixStems');
  });
});

describe('поставщики', () => {
  it('недоступный — серый с причиной сервера, без операции — серый со своей причиной', () => {
    const opts = providerOptions(CATALOG, 'separate', false);
    expect(opts.find(o => o.key === 'yandex')).toMatchObject({ disabled: true, reason: 'Ключ не задан' });
    expect(opts.find(o => o.key === 'fal')).toMatchObject({ disabled: true, reason: 'У «fal» нет операции «Стемы»' });
    expect(opts.find(o => o.key === 'local')).toMatchObject({ disabled: false, reason: null, locked: false });
  });

  it('в личном чате «Локально» — с замком', () => {
    const personal = { ...CATALOG, providers: [{ ...LOCAL, available: false, reason: 'Локальные модели работают только в чате проекта' }] };
    expect(providerOptions(personal, 'speak', true)[0]).toMatchObject({ locked: true, disabled: true });
    expect(providerOptions(CATALOG, 'speak', false)[0].locked).toBe(false);
  });
});

describe('модели', () => {
  it('«Авто» первой, дальше только умеющие операцию, со значками RU, лицензии, тяжести и единицей цены', () => {
    const opts = modelOptions(LOCAL, 'extract', 'auto');
    expect(opts.map(o => o.id)).toEqual(['auto', 'ace']);
    expect(opts[1]).toMatchObject({ ru: 'без RU', license: 'Apache-2.0', heavy: true, unit: 'бесплатно' });
    expect(modelOptions(LOCAL, 'separate', 'auto')[1].ru).toBeNull();
    expect(modelOptions(FAL, 'speak', 'auto')[1]).toMatchObject({ ru: 'RU', unit: '$0.1 за 1000 симв.', heavy: false });
  });
});

describe('цепочка настроек', () => {
  it('нить старше префов режима, префы — старше умолчания', () => {
    const prefs: AudioPrefs = { ...NO_PREFS, voice: { operation: 'designVoice', provider: 'local', model: 'qwen', count: 2, fields: { voice: 'бас', speaker: 'Eric' } } };
    const byPrefs = resolvePanel(null, prefs, CATALOG, 'voice');
    expect(byPrefs).toMatchObject({ op: 'designVoice', providerKey: 'local', modelId: 'qwen', count: 2 });
    const own = resolvePanel(thread({ mode: 'voice', operation: 'speak', provider: 'fal', model: 'minimax', fields: { voice: 'тенор' } }), prefs, CATALOG, 'voice');
    expect(own).toMatchObject({ op: 'speak', providerKey: 'fal', modelId: 'minimax', count: 2 });
    expect(own.fields).toEqual({ voice: 'тенор', speaker: 'Eric' });
    const none = resolvePanel(null, NO_PREFS, CATALOG, 'process');
    expect(none).toMatchObject({ mode: 'process', op: 'separate', providerKey: null, modelId: 'auto' });
  });

  it('смена режима, операции, поставщика и модели сбрасывает зависимое', () => {
    const cur = resolvePanel(thread({ mode: 'voice', operation: 'speak', provider: 'local', model: 'qwen', fields: { speaker: 'Eric' }, count: 3 }), NO_PREFS, CATALOG, 'voice');
    expect(nextSettings(cur, { mode: 'process' })).toEqual({ mode: 'process', operation: 'separate', provider: null, model: null, fields: {}, count: 3, inputs: null });
    expect(nextSettings(cur, { operation: 'designVoice' })).toMatchObject({ operation: 'designVoice', provider: 'local', model: null, fields: {} });
    expect(nextSettings(cur, { provider: 'fal' })).toMatchObject({ provider: 'fal', model: null, fields: {} });
    expect(nextSettings(cur, { model: 'auto' })).toMatchObject({ model: 'auto', fields: {} });
    expect(nextSettings(cur, { count: 2 })).toMatchObject({ count: 2, fields: { speaker: 'Eric' }, model: 'qwen' });
  });
});

const quote = (amount: number | null, extra: Partial<AudioQuote['price']> = {}): AudioQuote => ({
  quoteId: 'q', mode: 'voice', op: 'speak', provider: 'fal', model: 'minimax', count: 2, voiceKind: null, license: '', heavy: false,
  expiresAt: '', price: { amount, unit: 'chars', approx: true, source: 'catalog', eta: null, queueLength: null, ...extra },
});

describe('цена в две строки', () => {
  it('облако: итог и расшифровка по символам', () => {
    expect(priceLines({ op: 'speak', provider: FAL, model: FAL.models[0], quote: quote(0.0136), count: 2, textLength: 68 }))
      .toEqual(['≈ $0.0136', '68 симв. по $0.1 за 1000 × 2']);
  });
  it('облако без суммы — ориентир каталога', () => {
    expect(priceLines({ op: 'speak', provider: FAL, model: FAL.models[0], quote: null, count: 1, textLength: 0 }))
      .toEqual(['≈ $0.1 за 1000 симв.', 'сумма — по длине текста в поле ввода']);
    expect(priceLines({ op: 'speak', provider: FAL, model: FAL.models[0], quote: quote(0), count: 1, textLength: 0 }))
      .toEqual(['≈ $0.1 за 1000 симв.', 'сумма — по длине текста в поле ввода']);
  });
  it('локально — бесплатно, ETA и очередь GPU', () => {
    expect(priceLines({ op: 'speak', provider: LOCAL, model: LOCAL.models[0], quote: quote(0, { eta: 15, queueLength: 1 }), count: 1, textLength: 5 }))
      .toEqual(['Бесплатно', '~15 с · очередь GPU: 1']);
  });
  it('без ИИ и склейка', () => {
    expect(priceLines({ op: 'trim', provider: null, model: null, quote: null, count: 1, textLength: 0 })).toEqual(['Без ИИ', 'мгновенно · бесплатно']);
    expect(priceLines({ op: 'concat', provider: null, model: null, quote: null, count: 1, textLength: 0, pieces: 3 }))
      .toEqual(['Бесплатно · без ИИ', '3 куска · мгновенно']);
  });
});

describe('причина запуска', () => {
  const st = (over: Partial<PanelState>): PanelState => ({
    ...resolvePanel(null, NO_PREFS, CATALOG, 'voice'), ...over,
  });
  const base = (over: Partial<ReasonInput>): ReasonInput => ({
    sessionId: 's1', thread: thread(null), state: st({}), provider: providerOptions(CATALOG, 'speak', false)[0], text: 'привет',
    hasReference: false, hasVoiceModel: false, clips: 0, replicas: 0, trimReady: false, pieces: 0, quoteError: null, ...over,
  });

  it('готово — причины нет', () => expect(runReason(base({}))).toBeNull());
  it('без чата, без текста, серый поставщик', () => {
    expect(runReason(base({ sessionId: null }))).toBe('Сначала начните чат');
    expect(runReason(base({ text: '  ' }))).toBe('Напишите текст в поле ввода — он уйдёт модели');
    const grey = providerOptions(CATALOG, 'speak', false).find(p => p.key === 'yandex')!;
    expect(runReason(base({ provider: grey }))).toBe('Ключ не задан');
  });
  it('операции над готовым звуком требуют версию', () => {
    const s = st({ mode: 'process', op: 'separate', model: LOCAL.models[2] });
    expect(runReason(base({ state: s, thread: null }))).toBe('Выберите звук в ленте — операция работает с готовой версией');
  });
  it('клон без образца, обрезка без правок, склейка из одного куска', () => {
    expect(runReason(base({ state: st({ op: 'cloneVoice' }) }))).toBe('Загрузите образец голоса');
    expect(runReason(base({ state: st({ mode: 'process', op: 'trim' }) }))).toBe('Задайте кусок, громкость, нарастание, затухание или нормализацию');
    expect(runReason(base({ state: st({ mode: 'process', op: 'trim' }), trimReady: true }))).toBeNull();
    expect(runReason(base({ state: st({ mode: 'process', op: 'concat' }), pieces: 1, thread: null }))).toBe('Нужно хотя бы два куска — добавьте ещё один');
  });
  it('отказ котировки — последняя причина', () => {
    expect(runReason(base({ quoteError: 'Параметр «x» — число' }))).toBe('Параметр «x» — число');
  });
});

describe('автоформа по схеме', () => {
  const schema: AudioParamSchema = {
    provider: 'local', model: 'qwen', source: 'local-catalog', reserved: ['text'],
    fields: [
      { key: 'speaker', type: 'string', enum: ['Vivian', 'Eric'] },
      { key: 'expressiveness', type: 'number', min: 0.25, max: 2 },
      { key: 'steps', type: 'integer' },
      { key: 'loop', type: 'boolean' },
      { key: 'voice', type: 'string', maxLength: 500 },
      { key: 'tag', type: 'string', maxLength: 20 },
      { key: 'plan', type: 'object' },
      { key: 'cfg_weight', type: 'number', passed: false, notPassed: 'шов не принимает' },
    ],
  };
  it('тип поля → виджет', () => {
    const w = Object.fromEntries(schema.fields.map(f => [f.key, widgetOf(f)]));
    expect(w).toEqual({
      speaker: 'select', expressiveness: 'slider', steps: 'number', loop: 'checkbox', voice: 'textarea', tag: 'text', plan: 'json',
      cfg_weight: 'number',
    });
  });
  it('известные ключи — на виду, прочее и непередаваемое — в «Дополнительно»', () => {
    const { main, extra } = splitSchema(schema, 'voice');
    expect(main.map(f => f.key)).toEqual(['speaker', 'expressiveness', 'voice']);
    expect(extra.map(f => f.key)).toEqual(['steps', 'loop', 'tag', 'plan', 'cfg_weight']);
  });
  it('ключи музыки на виду только в «Музыке»: у голосовой модели они остаются в «Дополнительно»', () => {
    const withMusic: AudioParamSchema = { ...schema, fields: [
      { key: 'speaker', type: 'string' }, { key: 'key', type: 'string' }, { key: 'strength', type: 'number' }, { key: 'bpm', type: 'integer' },
    ] };
    const voice = splitSchema(withMusic, 'voice');
    expect(voice.main.map(f => f.key)).toEqual(['speaker']);
    expect(voice.extra.map(f => f.key)).toEqual(['key', 'strength', 'bpm']);
    expect(splitSchema(withMusic, 'process').extra.map(f => f.key)).toEqual(['key', 'strength', 'bpm']);
    const music = splitSchema(withMusic, 'music');
    expect(music.main.map(f => f.key)).toEqual(['speaker', 'key', 'strength', 'bpm']);
    expect(music.extra).toEqual([]);
  });
  it('в params — только передаваемые ключи схемы', () => {
    expect(pruneFields({ speaker: 'Eric', cfg_weight: 0.5, alien: 1, tag: '' }, schema)).toEqual({ speaker: 'Eric' });
    expect(pruneFields({ speaker: 'Eric' }, null)).toEqual({});
  });
});
