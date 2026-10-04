import { describe, expect, it } from 'vitest';

import type { AudioCatalog, AudioPrefs, AudioThread } from '../api';
import { priceLabel, resolveLaunch } from './launch';

const CAPS = { languages: ['ru'], voiceKinds: [], producesFiles: ['main'], license: { label: 'Apache-2.0', kind: 'permissive' as const } };
const CATALOG: AudioCatalog = {
  autoModelId: 'auto', maxCount: 4,
  providers: [
    { key: 'fal', label: 'fal', priceUnit: 'usd', available: false, reason: 'нет ключа', models: [
      { id: 'mm', label: 'MiniMax Speech', caps: { ...CAPS, ops: ['speak'], priceUnit: 'usd' }, priceHint: { amount: 0.1, unit: 'usd', per: '1000 симв.' } },
    ] },
    { key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null, models: [
      { id: 'qwen', label: 'Qwen3-TTS 1.7B', caps: { ...CAPS, ops: ['speak'], priceUnit: 'free' } },
      { id: 'ace', label: 'ACE-Step 1.5 XL', caps: { ...CAPS, ops: ['song', 'extract'], priceUnit: 'free', heavyOps: ['extract'] } },
    ] },
  ],
};
const PREFS: AudioPrefs = { voice: null, music: null, process: null };
const thread = (over: Partial<AudioThread> = {}): AudioThread => ({
  id: 't1', file: 'audio/podcast-intro.mp3', lineage: [], draftFolder: null, createdAt: '',
  versions: [
    { id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, files: [], license: null, createdAt: '' },
    { id: 'v3', number: 3, jobId: 'j', variant: 1, baseVersionId: 'origin', files: [], license: null, createdAt: '' },
  ],
  currentVersionId: 'v3', launches: [], settings: null, ...over,
});

describe('разрешение запуска звука', () => {
  it('настройки нити старше префов режима; явный недоступный поставщик не подменяется', () => {
    const prefs: AudioPrefs = { ...PREFS, voice: { operation: 'speak', provider: 'local', model: 'qwen', count: 3, fields: { speaker: 'Аня' } } };
    const t = thread({ settings: { mode: 'voice', operation: 'speak', provider: 'fal', model: 'mm', fields: null, count: 2 } });
    const L = resolveLaunch(t, prefs, CATALOG, 'music');
    expect([L.provider?.key, L.model?.id, L.voice, L.count]).toEqual(['fal', 'mm', 'Аня', 2]);
    expect(L.price).toBe('≈ $0.1 за 1000 симв.');
  });

  it('без настроек — режим ярлыка, первая операция, первый доступный поставщик и «Авто»', () => {
    const L = resolveLaunch(null, PREFS, CATALOG, 'voice');
    expect([L.mode, L.op, L.provider?.key, L.model?.id, L.count, L.price]).toEqual(['voice', 'speak', 'local', 'qwen', 1, 'бесплатно']);
  });

  it('«Авто» идёт порядком сервера: с local первым сводка показывает local, явный выбор не подменяется', () => {
    const fal = { ...CATALOG.providers[0], available: true, reason: null };
    const catalog: AudioCatalog = { ...CATALOG, providers: [fal, CATALOG.providers[1]] };
    expect(resolveLaunch(null, PREFS, catalog, 'voice').provider?.key).toBe('fal');
    const preferLocal: AudioCatalog = { ...catalog, autoProviders: ['local', 'fal'] };
    const L = resolveLaunch(null, PREFS, preferLocal, 'voice');
    expect(L.provider?.key).toBe('local');
    expect(L.model?.id).toBe('qwen');
    const own = thread({ settings: { mode: 'voice', operation: 'speak', provider: 'fal', model: null, fields: null } });
    expect(resolveLaunch(own, PREFS, preferLocal, 'voice').provider?.key).toBe('fal');
  });

  it('режим задаёт нить, а не ярлык; без настроек нити — режим ярлыка и его префы', () => {
    const prefs: AudioPrefs = { ...PREFS, music: { operation: 'song', provider: null, model: null, count: null, fields: null } };
    const t = thread({ settings: { mode: 'voice', operation: null, provider: null, model: null, fields: null } });
    expect(resolveLaunch(t, prefs, CATALOG, 'music').op).toBe('speak');
    expect(resolveLaunch(thread(), prefs, CATALOG, 'music').op).toBe('song');
  });

  it('priceLabel: «бесплатно» у локальных, ориентир каталога у облака', () => {
    expect(priceLabel(CATALOG.providers[1], CATALOG.providers[1].models[0])).toBe('бесплатно');
    expect(priceLabel(CATALOG.providers[0], CATALOG.providers[0].models[0])).toBe('≈ $0.1 за 1000 симв.');
  });
});
