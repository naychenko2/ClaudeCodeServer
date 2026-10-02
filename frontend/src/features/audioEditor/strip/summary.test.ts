import { beforeEach, describe, expect, it, vi } from 'vitest';

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
// Ярлык зовёт revealWorkspacePanel — событие окна; в node окна нет
const dispatched: unknown[] = [];
(globalThis as unknown as { window: unknown }).window = {
  dispatchEvent: (e: unknown) => { dispatched.push(e); return true; },
  addEventListener: () => {}, removeEventListener: () => {},
};
(globalThis as unknown as { CustomEvent: unknown }).CustomEvent = class { constructor(public type: string, public init: { detail: unknown }) {} };

import { __resetComposerStrips, getActiveStrip } from '../../../lib/composerStrips';
import type { AudioCatalog, AudioPrefs, AudioThread } from '../api';
import { soundShortcuts } from '../manifest';
import { __applyThreads, __resetAudioStore, __setScopeData, getShortcutMode, handleEvent, SOUND_STRIP } from '../thread/threadStore';
import { soundStripStatus, stripModel } from './SoundStrip';
import { queueBadge, resolveLaunch, soundSummary, soundSummaryMobile } from './summary';

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

beforeEach(() => {
  localStorage.clear();
  dispatched.length = 0;
  __resetAudioStore();
  __resetComposerStrips();
  vi.restoreAllMocks();
});

describe('сводка полосы «Звук»', () => {
  it('правка без ИИ — без поставщика, модели и вариантов: «Обработка · Склеить · без ИИ»', () => {
    const L = resolveLaunch(thread({ settings: { mode: 'process', operation: 'concat', provider: null, model: null, fields: null } }), PREFS, CATALOG, 'voice');
    expect(soundSummary({ focus: null, launch: L }, true)).toBe('Обработка · Склеить · без ИИ');
    expect(soundSummaryMobile(L)).toBe('Обработка · без ИИ');
  });

  it('без настроек — режим ярлыка, первая операция, первый доступный поставщик и «Авто»', () => {
    const L = resolveLaunch(null, PREFS, CATALOG, 'voice');
    expect(soundSummary({ focus: null, launch: L }, true)).toBe('Голос · Озвучить · Локально · Qwen3-TTS 1.7B · 1 вар. · бесплатно');
    expect(soundSummary({ focus: null, launch: L })).toBe('Звук не выбран · Голос · Озвучить · Локально · Qwen3-TTS 1.7B · 1 вар. · бесплатно');
  });

  it('настройки нити старше префов режима; явный недоступный поставщик не подменяется', () => {
    const prefs: AudioPrefs = { ...PREFS, voice: { operation: 'speak', provider: 'local', model: 'qwen', count: 3, fields: { speaker: 'Аня' } } };
    const t = thread({ settings: { mode: 'voice', operation: 'speak', provider: 'fal', model: 'mm', fields: null, count: 2 } });
    const L = resolveLaunch(t, prefs, CATALOG, 'music');
    expect(soundSummary({ focus: 'x', launch: L }, true)).toBe('Голос · Озвучить · fal · MiniMax Speech · Аня · 2 вар. · ≈ $0.1 за 1000 симв.');
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

  it('бейдж очереди: позиция и старт, ход варианта, тяжёлая локальная без задачи', () => {
    const L = resolveLaunch(null, PREFS, CATALOG, 'voice');
    const j = { jobId: 'j', sessionId: 's', threadId: 't1', stage: 'queued' as const, queuePosition: 2, etaSeconds: 180, variant: 1, count: 2 };
    expect(queueBadge([j], false, L)).toBe('GPU: 2-я в очереди · старт ≈ через 3 мин');
    expect(queueBadge([{ ...j, stage: 'running', etaSeconds: 12, variant: 2 }], false, L)).toBe('идёт: Озвучить · вариант 2 из 2 · ещё ≈ 12 с');
    expect(queueBadge([], true, L)).toBe('идёт генерация');
    expect(queueBadge([], false, L)).toBeNull();
    const heavy = resolveLaunch(null, { ...PREFS, music: { operation: 'extract', provider: null, model: null, count: null, fields: null } }, CATALOG, 'music');
    expect(queueBadge([], false, heavy)).toBe('тяжёлая · одна за раз');
  });
});

describe('полоса «Звук» по стору', () => {
  it('чип и сводка следуют за сменой нити событием', () => {
    __setScopeData('p1', CATALOG, PREFS);
    __applyThreads('s1', 'p1', { focus: 't1', revision: 1, threads: [thread()] });
    expect(stripModel('p1', 's1', thread()).focus).toBe('podcast-intro.mp3 · версия 3');
    expect(soundStripStatus('p1', 's1')).toBe('Работаем с: podcast-intro.mp3 · версия 3 · Голос · Озвучить · Локально · Qwen3-TTS 1.7B · 1 вар. · бесплатно');

    const draft = thread({ id: 't2', file: null, draftFolder: '', versions: [], currentVersionId: null,
      settings: { mode: 'music', operation: 'song', provider: null, model: null, fields: null } });
    handleEvent({ type: 'audio_thread_changed', scopeKey: 'p1', sessionId: 's1', revision: 2, state: { focus: 't2', revision: 2, threads: [thread(), draft] } });
    expect(soundStripStatus('p1', 's1')).toBe('Работаем с: Новый звук · Музыка · Песня · Локально · ACE-Step 1.5 XL · 1 вар. · бесплатно');

    handleEvent({ type: 'audio_thread_changed', scopeKey: 'p1', sessionId: 's1', revision: 3, state: { focus: null, revision: 3, threads: [thread(), draft] } });
    expect(soundStripStatus('p1', 's1').startsWith('Звук не выбран · ')).toBe(true);
  });

  it('ярлык «Музыка» ставит режим чата, просит полосу «Звук» и открывает панель на «Настройках»', () => {
    __setScopeData('p1', CATALOG, PREFS);
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [] });
    const music = soundShortcuts({ sessionId: 's1' }).find(s => s.key === 'sound-music')!;
    music.onSelect();
    expect(getShortcutMode('s1')).toBe('music');
    expect(getActiveStrip('s1', ['git', SOUND_STRIP])).toBe(SOUND_STRIP);
    expect(dispatched).toEqual([expect.objectContaining({ type: 'cc-reveal-panel', init: { detail: { key: 'sound', tab: 'settings', sessionId: 's1' } } })]);
    expect(soundStripStatus('p1', 's1')).toBe('Звук не выбран · Музыка · Песня · Локально · ACE-Step 1.5 XL · 1 вар. · бесплатно');
  });
});
