import { describe, expect, it } from 'vitest';
import { isLiveTtsVoice, voiceRoleLabel, voiceSupportsSpeed } from '../voiceLabels';

describe('voiceLabels', () => {
  it('подписывает амплуа general и LiveTTS по-русски', () => {
    expect(voiceRoleLabel('friendly')).toBe('дружелюбное');
    expect(voiceRoleLabel('casual')).toBe('непринуждённое');
    expect(voiceRoleLabel('support')).toBe('поддерживающее');
    expect(voiceRoleLabel('sales')).toBe('продающее');
    expect(voiceRoleLabel('formal')).toBe('официальное');
    expect(voiceRoleLabel('narrator')).toBe('дикторское');
  });

  it('неизвестное амплуа показывает ключом, а не пустотой', () => {
    expect(voiceRoleLabel('angry')).toBe('angry');
  });

  it('LiveTTS — по модели из списка голосов', () => {
    expect(isLiveTtsVoice({ model: 'livetts' })).toBe(true);
    expect(isLiveTtsVoice({ model: 'general' })).toBe(false);
    expect(isLiveTtsVoice({})).toBe(false);
    expect(isLiveTtsVoice(null)).toBe(false);
  });

  it('темп есть, пока сервер явно не сказал обратное (старый сервер поля не шлёт)', () => {
    expect(voiceSupportsSpeed({ speedSupported: false })).toBe(false);
    expect(voiceSupportsSpeed({ speedSupported: true })).toBe(true);
    expect(voiceSupportsSpeed({})).toBe(true);
    expect(voiceSupportsSpeed(undefined)).toBe(true);
  });
});
