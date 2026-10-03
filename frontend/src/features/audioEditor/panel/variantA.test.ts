import { describe, expect, it } from 'vitest';
import type { AudioCatalog, AudioModelInfo, AudioProvider } from '../api';
import { AUTO_EXECUTOR, FAL_NOTE, executorPatch, executorRows, executorSummary, executorValue } from './executorRows';
import { nextSettings, resolvePanel, splitSchema, tuckSchema, type PanelState } from './model';
import type { AudioParamSchema } from '../api';
import { composerHintOf, OP_GROUP, opOptions } from './opGroups';
import { stemChoices, stemPatch, stemValue } from './stems';
import { opPlaceholder } from '../ops';
import { resolveLaunch } from '../strip/summary';

const model = (id: string, ops: AudioModelInfo['caps']['ops'], extra: Partial<AudioModelInfo['caps']> = {}, hint?: AudioModelInfo['priceHint']): AudioModelInfo => ({
  id, label: id.toUpperCase(),
  caps: {
    ops, languages: ['ru', 'en'], voiceKinds: ['preset'], producesFiles: [], license: { label: 'Apache-2.0', kind: 'permissive' },
    priceUnit: hint?.unit ?? 'free', ...extra,
  },
  priceHint: hint ?? null,
});

const neutral = { languages: [], languageNeutral: true };
const LOCAL: AudioProvider = {
  key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
  models: [
    model('qwen', ['speak']),
    model('yue', ['song'], { languages: ['en'], license: { label: 'CC BY-NC 4.0', kind: 'nonCommercial' } }),
    model('ace', ['song', 'extract'], { heavyOps: ['extract'] }),
    model('bs-roformer', ['separate'], { ...neutral, stemSet: 'vocals' }),
    model('htdemucs-4', ['separate'], { ...neutral, stemSet: '4' }),
    model('htdemucs-6', ['separate'], { ...neutral, stemSet: '6' }),
    model('karaoke', ['separate'], { ...neutral, stemSet: 'karaoke' }),
  ],
};
const FAL: AudioProvider = {
  key: 'fal', label: 'fal', priceUnit: 'usd', available: true, reason: null,
  models: [
    model('fal-ai/minimax/speech', ['speak'], {}, { amount: 0.0001, unit: 'chars', per: 'char' }),
    model('fal-ai/demucs', ['separate'], { ...neutral, stemSet: '6' }, { amount: 0.0007, unit: 'sec', per: 'sec' }),
    model('fal-ai/sam-audio', ['separate'], neutral, { amount: 0.05, unit: 'run', per: 'run' }),
  ],
};
const YANDEX: AudioProvider = {
  key: 'yandex', label: 'Яндекс', priceUnit: 'rub', available: false, reason: 'Ключ не задан',
  models: [model('speechkit', ['speak'], {}, { amount: 0.0002, unit: 'chars', per: 'char' })],
};
// Облако в списке каталога первым: группы и «Авто» не должны от этого зависеть
const CATALOG: AudioCatalog = { providers: [FAL, YANDEX, LOCAL], autoModelId: 'auto', maxCount: 4, autoProviders: ['local', 'fal', 'yandex'] };
const NO_PREFS = { voice: null, music: null, process: null };

const st = (patch: Partial<PanelState> & Pick<PanelState, 'op'>): PanelState => {
  const base = resolvePanel(null, NO_PREFS, CATALOG, 'voice');
  return { ...base, ...patch };
};

describe('операция одним списком с группами', () => {
  const groupOf = (o: { group?: unknown }) => (typeof o.group === 'string' ? o.group : (o.group as { label: string } | undefined)?.label);

  it('без звука группа «С выбранным звуком» серая с подсказкой, её операции выключены', () => {
    const opts = opOptions('music', false, 'song');
    expect(opts.find(o => o.value === 'song')).toMatchObject({ group: OP_GROUP.create });
    expect(opts.find(o => o.value === 'song')?.disabled).toBeUndefined();
    const cover = opts.find(o => o.value === 'cover')!;
    expect(cover.group).toEqual({ label: 'С выбранным звуком · выберите звук в ленте', disabled: true });
    expect(cover.disabled).toBe(true);
  });

  it('со звуком группа живая, у выбранной операции — «· над выбранным звуком»', () => {
    const opts = opOptions('music', true, 'cover');
    const cover = opts.find(o => o.value === 'cover')!;
    expect(cover).toMatchObject({ group: OP_GROUP.source, label: 'Кавер · над выбранным звуком' });
    expect(cover.disabled).toBeUndefined();
    expect(opts.find(o => o.value === 'repaint')?.label).toBe('Перегенерировать кусок');
  });

  it('«Обработка»: правка без ИИ и склейка — в своих группах; склейке звук не нужен', () => {
    const off = opOptions('process', false, 'separate');
    expect(groupOf(off.find(o => o.value === 'trim')!)).toBe(OP_GROUP.noAi);
    expect(off.find(o => o.value === 'trim')?.disabled).toBe(true);
    expect(off.find(o => o.value === 'concat')).toMatchObject({ group: OP_GROUP.many });
    expect(off.find(o => o.value === 'concat')?.disabled).toBeUndefined();
    expect(groupOf(off.find(o => o.value === 'separate')!)).toBe('С выбранным звуком · выберите звук в ленте');
    const on = opOptions('process', true, 'trim');
    expect(on.find(o => o.value === 'trim')).toMatchObject({ group: OP_GROUP.noAi, label: 'Обрезка и громкость · над выбранным звуком' });
  });

  it('источники озвучки — одной строкой «Озвучить»: подпись у клона ставится на неё', () => {
    const opts = opOptions('voice', true, 'cloneVoice');
    expect(opts.map(o => o.value)).not.toContain('cloneVoice');
    expect(opts.find(o => o.value === 'speak')?.group).toBe(OP_GROUP.create);
    expect(opts.find(o => o.value === 'convertVoice')?.group).toBe(OP_GROUP.source);
  });

  it('подсказка композера — у озвучки и музыки со стилем, у стемов её нет', () => {
    expect(composerHintOf('speak')).toBe('Текст для озвучки');
    expect(composerHintOf('song')).toBe('Стиль музыки');
    expect(composerHintOf('separate')).toBeNull();
  });
});

describe('список «Исполнитель»', () => {
  it('порядок: «Авто», своя видеокарта, облако; поставщики без операции не попадают', () => {
    const rows = executorRows(CATALOG, 'speak');
    expect(rows.map(r => [r.id, r.group])).toEqual([
      [AUTO_EXECUTOR, 'auto'],
      ['local|qwen', 'local'],
      ['fal|fal-ai/minimax/speech', 'cloud'],
      ['yandex|speechkit', 'cloud'],
    ]);
  });

  it('цены: локально — бесплатно, облако — ориентир каталога; недоступный — серый с причиной', () => {
    const rows = executorRows(CATALOG, 'speak');
    expect(rows.find(r => r.id === 'local|qwen')?.price).toBe('бесплатно');
    expect(rows.find(r => r.id === 'fal|fal-ai/minimax/speech')).toMatchObject({ price: '$0.1 за 1000 симв.' });
    expect(rows.find(r => r.id === 'yandex|speechkit')).toMatchObject({ disabled: true, reason: 'Ключ не задан' });
  });

  it('подпись «отобранные · остальные — по запросу» — один раз, у первой строки fal', () => {
    const sep = executorRows(CATALOG, 'separate');
    expect(sep.find(r => r.id === 'fal|fal-ai/demucs')?.sub).toBe(`fal · ${FAL_NOTE}`);
    expect(sep.find(r => r.id === 'fal|fal-ai/sam-audio')?.sub).toBe('fal');
    expect(sep.filter(r => r.sub?.includes(FAL_NOTE))).toHaveLength(1);
    expect(executorRows(CATALOG, 'speak').find(r => r.id === 'yandex|speechkit')?.sub).toBe('Яндекс');
  });

  it('в личном чате «Локально» — с замком, причина каталога остаётся; в проекте замка нет', () => {
    const reason = 'Локальные модели работают только в чате проекта';
    const personal = { ...CATALOG, providers: [FAL, { ...LOCAL, available: false, reason }] };
    const rows = executorRows(personal, 'speak', true);
    expect(rows.find(r => r.id === 'local|qwen')).toMatchObject({ locked: true, disabled: true, reason });
    expect(rows.find(r => r.id === 'fal|fal-ai/minimax/speech')?.locked).toBeUndefined();
    expect(executorRows(CATALOG, 'speak').some(r => r.locked)).toBe(false);
  });

  it('«Авто · сейчас X» — по порядку перебора сервера, а не по списку каталога', () => {
    expect(executorRows(CATALOG, 'speak')[0]).toMatchObject({ name: 'Авто', sub: 'сейчас: локально · QWEN', now: 'локально · QWEN', price: 'бесплатно' });
    const cloudFirst = { ...CATALOG, autoProviders: ['fal', 'local'] };
    expect(executorRows(cloudFirst, 'speak')[0]).toMatchObject({ sub: 'сейчас: fal · FAL-AI/MINIMAX/SPEECH', now: 'fal · FAL-AI/MINIMAX/SPEECH', price: '$0.1 за 1000 симв.' });
  });

  it('бейджи: RU, ограничивающая лицензия, тяжёлая; свободная лицензия и язык обработки — без бейджа', () => {
    const song = executorRows(CATALOG, 'song');
    expect(song.find(r => r.id === 'local|yue')?.badges).toEqual([{ label: 'без RU' }, { label: 'CC BY-NC 4.0' }]);
    expect(song.find(r => r.id === 'local|ace')?.badges).toEqual([{ label: 'RU', tone: 'success' }]);
    expect(executorRows(CATALOG, 'extract').find(r => r.id === 'local|ace')?.badges)
      .toEqual([{ label: 'RU', tone: 'success' }, { label: 'тяжёлая', tone: 'warning' }]);
    expect(executorRows(CATALOG, 'separate').find(r => r.id === 'local|bs-roformer')?.badges).toBeUndefined();
  });

  it('выбор строки ставит поставщика вместе с моделью; «Авто» снимает обоих', () => {
    const cur = st({ op: 'speak' });
    const next = nextSettings(cur, executorPatch('fal|fal-ai/minimax/speech'));
    expect(next).toMatchObject({ provider: 'fal', model: 'fal-ai/minimax/speech' });
    const back = nextSettings({ ...cur, providerKey: 'fal', modelId: 'fal-ai/minimax/speech' }, executorPatch(AUTO_EXECUTOR));
    expect(back).toMatchObject({ provider: null, model: null });
  });

  it('подсветка и сводка «Чем»', () => {
    const auto = st({ op: 'speak' });
    expect(executorValue(CATALOG, auto)).toBe(AUTO_EXECUTOR);
    expect(executorSummary(CATALOG, auto)).toEqual({ name: 'Авто', parts: ['локально', 'QWEN'] });
    // Явный поставщик с моделью «Авто» подсвечивает модель, которой пойдёт запуск
    const falAuto = st({ op: 'speak', providerKey: 'fal', modelId: 'auto', provider: FAL, model: FAL.models[0] });
    expect(executorValue(CATALOG, falAuto)).toBe('fal|fal-ai/minimax/speech');
    expect(executorSummary(CATALOG, falAuto)).toEqual({ name: 'fal · Авто', parts: ['FAL-AI/MINIMAX/SPEECH'] });
    const local = st({ op: 'speak', providerKey: 'local', modelId: 'qwen', provider: LOCAL, model: LOCAL.models[0] });
    expect(executorSummary(CATALOG, local)).toEqual({ name: 'QWEN', parts: ['локально'] });
  });
});

describe('«Что получить» в стемах', () => {
  it('сегменты по stemSet поставщика: у fal только 6, остальное серое с причиной', () => {
    expect(stemChoices(LOCAL).every(c => !c.disabled)).toBe(true);
    const fal = stemChoices(FAL);
    expect(fal.filter(c => !c.disabled).map(c => c.value)).toEqual(['6']);
    expect(fal.find(c => c.value === 'karaoke')).toMatchObject({ disabled: true, title: 'У «fal» нет: караоке' });
  });

  it('сегмент → модель поставщика с этим набором; та же раскладка — без правки', () => {
    const roformer = LOCAL.models.find(m => m.id === 'bs-roformer')!;
    expect(stemValue(roformer)).toBe('vocals');
    expect(stemPatch(LOCAL, roformer, 'vocals')).toBeNull();
    expect(stemPatch(LOCAL, roformer, '4')).toEqual({ provider: 'local', model: 'htdemucs-4' });
    expect(stemPatch(LOCAL, roformer, 'karaoke')).toEqual({ provider: 'local', model: 'karaoke' });
    expect(stemPatch(FAL, FAL.models[2], 'vocals')).toBeNull();
    expect(stemValue(FAL.models[2])).toBeNull();
  });
});

describe('«Ещё настройки» у музыки', () => {
  const schema = { fields: ['bpm', 'key', 'abc', 'seed'].map(key => ({ key, type: 'string' })) } as unknown as AudioParamSchema;

  it('темп и тональность уходят с виду с русскими подписями, партитура остаётся', () => {
    const t = tuckSchema('music', splitSchema(schema, 'music'));
    expect(t.main.map(f => f.key)).toEqual(['abc']);
    expect(t.extra.map(f => f.key)).toEqual(['bpm', 'key', 'seed']);
    expect(t.labels).toEqual({ bpm: 'Темп, BPM', key: 'Тональность' });
  });

  it('вне «Музыки» раскладка splitSchema как есть', () => {
    const split = splitSchema(schema, 'voice');
    expect(tuckSchema('voice', split)).toEqual({ ...split, labels: {} });
  });
});

describe('плейсхолдер композера в «Стемах» — по модели запуска', () => {
  it('«Авто» берёт BS-RoFormer с готовым набором: описание не нужно, чужой подсказки нет', () => {
    const L = resolveLaunch(null, NO_PREFS, CATALOG, 'process');
    expect(L.op).toBe('separate');
    expect(L.model?.id).toBe('bs-roformer');
    const ph = opPlaceholder(L.op, L.model);
    expect(ph).toBe('Комментарий не нужен — выберите, что получить');
    expect(ph).not.toMatch(/SAM/);
  });

  it('модель без набора стемов выделяет звук по описанию — просим описание', () => {
    const sam = FAL.models.find(m => m.id === 'fal-ai/sam-audio')!;
    expect(opPlaceholder('separate', sam)).toBe('Что выделить: например «лай собаки»');
  });

  it('у остальных операций — подсказка операции как была', () => {
    expect(opPlaceholder('denoise', LOCAL.models[0]!)).toBe('Комментарий не нужен');
  });
});
