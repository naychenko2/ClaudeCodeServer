import { describe, expect, it } from 'vitest';
import { createVoicePayload } from '../voicePayload';

describe('createVoicePayload', () => {
  it('выбранный голос едет как есть', () => {
    const voice = { voice: 'masha', role: 'friendly', speed: 1.15 };
    expect(createVoicePayload(voice, true)).toEqual(voice);
  });

  it('пикер не трогали — поле не шлём, сервер подберёт голос сам', () => {
    expect(createVoicePayload(null, false)).toBeUndefined();
  });

  it('явный выбор голоса инстанса — пустой объект, подбора не будет', () => {
    expect(createVoicePayload(null, true)).toEqual({});
  });
});
