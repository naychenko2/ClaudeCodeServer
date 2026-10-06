import { describe, expect, it } from 'vitest';
import type { AudioCatalog, AudioModelInfo, AudioProvider } from '../api';
import { AUTO_EXECUTOR, FAL_NOTE, executorPatch, executorRows, executorValue } from './executorRows';
import { resolveLaunch } from './launch';
import type { PanelState } from './model';

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
  const L = resolveLaunch(null, NO_PREFS, CATALOG, 'voice');
  return { ...L, providerKey: null, modelId: 'auto', fields: {}, inputs: null, ...patch };
};

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
    expect(executorPatch('fal|fal-ai/minimax/speech')).toEqual({ provider: 'fal', model: 'fal-ai/minimax/speech' });
    expect(executorPatch(AUTO_EXECUTOR)).toEqual({ provider: null, model: null });
  });

  it('подсветка строки', () => {
    const auto = st({ op: 'speak' });
    expect(executorValue(CATALOG, auto)).toBe(AUTO_EXECUTOR);
    // Явный поставщик с моделью «Авто» подсвечивает модель, которой пойдёт запуск
    const falAuto = st({ op: 'speak', providerKey: 'fal', modelId: 'auto', provider: FAL, model: FAL.models[0] });
    expect(executorValue(CATALOG, falAuto)).toBe('fal|fal-ai/minimax/speech');
  });
});
