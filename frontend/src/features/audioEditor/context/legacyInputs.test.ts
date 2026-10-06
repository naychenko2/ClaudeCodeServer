// Переезд старых входов звука в контекст (2з-3): модель голоса из `voices/<slug>/` → референс, остальное остаётся.
import { beforeEach, describe, expect, it, vi } from 'vitest';

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); }, clear: () => store.clear(), key: (i: number) => [...store.keys()][i] ?? null,
  get length() { return store.size; },
} as Storage;
vi.stubGlobal('window', Object.assign(new EventTarget(), { innerWidth: 1440, innerHeight: 900 }));

import { chatContextApi } from '../../../lib/chatContext/api';
import { __applyChatContext, __resetChatContextStore } from '../../../lib/chatContext/store';
import { DEFAULT_INPUTS, inputsKey, readInputs, writeInputs } from '../panel/inputs';
import {
  __resetLegacyInputs, dropLegacyClipPaths, legacyClipPaths, migrateLegacyVoice, voiceSlugOfPath,
} from './legacyInputs';

const key = inputsKey('p1', 's1', 't1');
const dto = { revision: 2, primary: null, refs: [] };
const settle = () => new Promise(r => setTimeout(r, 0));

beforeEach(() => {
  vi.restoreAllMocks();
  store.clear();
  __resetLegacyInputs();
  __resetChatContextStore();
  __applyChatContext('s1', dto);
});

describe('voiceSlugOfPath', () => {
  it('голос библиотеки — slug; путь вне voices/ — null', () => {
    expect(voiceSlugOfPath('voices/andrey/voice.pth')).toBe('andrey');
    expect(voiceSlugOfPath(' ./voices/anya/voice.pth ')).toBe('anya');
    expect(voiceSlugOfPath('models/x.pth')).toBeNull();
    expect(voiceSlugOfPath('')).toBeNull();
  });
});

describe('migrateLegacyVoice', () => {
  it('модель голоса библиотеки уходит референсом «голос», из входов исчезает вместе с индексом', async () => {
    writeInputs(key, { ...DEFAULT_INPUTS, voiceModelPath: 'voices/andrey/voice.pth', voiceIndexPath: 'voices/andrey/voice.index', language: 'ru' });
    const add = vi.spyOn(chatContextApi, 'attachRef').mockResolvedValue({ ...dto, revision: 3 });
    migrateLegacyVoice('p1', 's1', 't1', []);
    await settle();
    expect(add).toHaveBeenCalledWith('s1', { kind: 'audio-voice', ref: { slug: 'andrey' }, role: 'voice' }, 2);
    expect(readInputs(key).voiceModelPath).toBe('');
    expect(readInputs(key).voiceIndexPath).toBe('');
  });

  it('отказ сервера — входы остаются, повторной попытки в этой вкладке нет', async () => {
    writeInputs(key, { ...DEFAULT_INPUTS, voiceModelPath: 'voices/andrey/voice.pth' });
    const add = vi.spyOn(chatContextApi, 'attachRef').mockRejectedValue(new Error('400'));
    migrateLegacyVoice('p1', 's1', 't1', []);
    await settle();
    migrateLegacyVoice('p1', 's1', 't1', []);
    expect(add).toHaveBeenCalledTimes(1);
    expect(readInputs(key).voiceModelPath).toBe('voices/andrey/voice.pth');
  });

  it('голос уже в контексте — запись не нужна, входы чистятся', () => {
    writeInputs(key, { ...DEFAULT_INPUTS, voiceModelPath: 'voices/andrey/voice.pth' });
    const add = vi.spyOn(chatContextApi, 'attachRef');
    migrateLegacyVoice('p1', 's1', 't1', [{ kind: 'audio-voice', ref: { slug: 'andrey' } }]);
    expect(add).not.toHaveBeenCalled();
    expect(readInputs(key).voiceModelPath).toBe('');
  });

  it('путь вне библиотеки контекст принять не может — не трогаем', async () => {
    writeInputs(key, { ...DEFAULT_INPUTS, voiceModelPath: 'models/x.pth' });
    const add = vi.spyOn(chatContextApi, 'attachRef');
    migrateLegacyVoice('p1', 's1', 't1', []);
    await settle();
    expect(add).not.toHaveBeenCalled();
    expect(readInputs(key).voiceModelPath).toBe('models/x.pth');
  });
});

describe('записи обучения', () => {
  it('форма «Обучить голос» забирает clipPaths старых входов области и очищает их', () => {
    writeInputs(key, { ...DEFAULT_INPUTS, clipPaths: ['records/a.wav', ' ', 'records/b.wav'] });
    writeInputs(inputsKey('other', 's1', 't1'), { ...DEFAULT_INPUTS, clipPaths: ['x.wav'] });
    expect(legacyClipPaths('p1')).toEqual(['records/a.wav', 'records/b.wav']);
    dropLegacyClipPaths('p1');
    expect(legacyClipPaths('p1')).toEqual([]);
    expect(legacyClipPaths('other')).toEqual(['x.wav']);
  });
});
