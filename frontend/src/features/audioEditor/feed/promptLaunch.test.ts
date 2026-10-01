import { describe, expect, it } from 'vitest';
import type { AudioCatalog, AudioPrefs, AudioThread } from '../api';
import { findModel, promptLaunch, promptRunLabel } from './promptLaunch';

const CAPS = { languages: ['ru'], voiceKinds: [], producesFiles: ['main'], license: { label: 'MIT', kind: 'permissive' as const } };
const CATALOG: AudioCatalog = {
  autoModelId: 'auto', maxCount: 4,
  providers: [
    { key: 'fal', label: 'fal', priceUnit: 'usd', available: true, reason: null, models: [
      { id: 'fal-ai/minimax/speech-2.8-hd', label: 'MiniMax Speech 2.8 HD', caps: { ...CAPS, ops: ['speak'], priceUnit: 'usd' }, priceHint: { amount: 0.1, unit: 'usd', per: '1000 симв.' } },
    ] },
    { key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null, models: [
      { id: 'qwen3-tts-1.7b', label: 'Qwen3-TTS 1.7B', caps: { ...CAPS, ops: ['speak'], priceUnit: 'free' } },
      { id: 'ace', label: 'ACE-Step 1.5 XL', caps: { ...CAPS, ops: ['song'], priceUnit: 'free' } },
    ] },
  ],
};
const PREFS: AudioPrefs = { voice: null, music: null, process: null };
// Нить, на которой в полосе выбрана «Обработка · Склеить» — кадр f1440l-feed-4 ревью
const glued: AudioThread = {
  id: 't1', file: 'intro.wav', lineage: [], draftFolder: null, createdAt: '',
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, files: [], license: null, createdAt: '' }],
  currentVersionId: 'origin', launches: [],
  settings: { mode: 'process', operation: 'concat', provider: null, model: null, fields: null },
};

describe('«Сгенерировать» в карточке «Текст для звука»', () => {
  it('режим и модель карточки побеждают полосу: «Голос · qwen3-tts» не запускает склейку', () => {
    const p = promptLaunch(glued, PREFS, CATALOG, 'voice', { mode: 'voice', model: 'qwen3-tts' });
    expect(p.launch.mode).toBe('voice');
    expect(p.launch.op).toBe('speak');
    expect(p.launch.model?.id).toBe('qwen3-tts-1.7b');
    expect(p.override).toEqual({ mode: 'voice', operation: 'speak', provider: 'local', model: 'qwen3-tts-1.7b' });
    // Цена на кнопке — у того, что пойдёт, и кнопка называет запуск
    expect(promptRunLabel(p)).toBe('Озвучить · Qwen3-TTS 1.7B · бесплатно');
  });

  it('модель, названная агентом, меняет цену даже при том же режиме', () => {
    const voice = { ...glued, settings: { mode: 'voice' as const, operation: 'speak' as const, provider: 'local', model: 'qwen3-tts-1.7b', fields: null } };
    const p = promptLaunch(voice, PREFS, CATALOG, 'voice', { mode: null, model: 'MiniMax Speech 2.8 HD' });
    expect(p.launch.provider?.key).toBe('fal');
    expect(p.launch.price).toBe('≈ $0.1 за 1000 симв.');
    expect(p.override?.model).toBe('fal-ai/minimax/speech-2.8-hd');
  });

  it('без указаний агента — как в полосе, без явного в запросе', () => {
    const voice = { ...glued, settings: { mode: 'voice' as const, operation: 'speak' as const, provider: 'local', model: 'qwen3-tts-1.7b', fields: null } };
    const p = promptLaunch(voice, PREFS, CATALOG, 'voice', { mode: null, model: null });
    expect(p.differs).toBe(false);
    expect(p.override).toBeNull();
    expect(promptRunLabel(p)).toBe('Сгенерировать · бесплатно');
  });

  it('модель ищется по слову, но только среди умеющих режим', () => {
    expect(findModel(CATALOG, 'ace-step', 'voice')).toBeNull();
    expect(findModel(CATALOG, 'ace-step', 'music')?.model.id).toBe('ace');
    expect(findModel(CATALOG, 'нет такой', 'voice')).toBeNull();
  });
});
