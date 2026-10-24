import { describe, expect, it } from 'vitest';
import type { TtsVoiceInfo } from '../../../types';
import {
  filterVoices, greetingSample, isLiveTtsVoice, voiceName, voiceRoleLabel, voiceSupportsSpeed, voiceTrait,
} from '../voiceLabels';

const CATALOG: TtsVoiceInfo[] = [
  { voice: 'alena', label: 'Алёна · спокойная, ровная', gender: 'female', roles: ['good'] },
  { voice: 'marina', label: 'Марина · мягкая, может шептать', gender: 'female', roles: ['friendly', 'whisper'] },
  { voice: 'kirill', label: 'Кирилл · чёткий, может быть строгим', gender: 'male', roles: ['good', 'strict'] },
  { voice: 'madi_ru', label: 'Мади · ровный, чуть отстранённый', gender: 'male', roles: [] },
  { voice: 'sergey', label: 'Сергей · живой, поддерживающий', gender: 'male', roles: ['support'], model: 'livetts' },
];

describe('строка голоса и поиск', () => {
  it('подпись делится на имя и характер', () => {
    expect(voiceName(CATALOG[0])).toBe('Алёна');
    expect(voiceTrait(CATALOG[0])).toBe('спокойная, ровная');
    expect(voiceName({ voice: 'x', label: '' })).toBe('x');
    expect(voiceTrait({ label: 'Без характера' })).toBe('');
  });

  it('ищет по имени и характеру, без разницы между «ё» и «е»', () => {
    expect(filterVoices(CATALOG, 'алена', 'all').map(v => v.voice)).toEqual(['alena']);
    expect(filterVoices(CATALOG, 'шептать', 'all').map(v => v.voice)).toEqual(['marina']);
    expect(filterVoices(CATALOG, 'ЧЁТКИЙ', 'all').map(v => v.voice)).toEqual(['kirill']);
    expect(filterVoices(CATALOG, 'madi', 'all').map(v => v.voice)).toEqual(['madi_ru']);
    expect(filterVoices(CATALOG, 'бас', 'all')).toEqual([]);
  });

  it('фильтр пола складывается с поиском', () => {
    expect(filterVoices(CATALOG, '', 'male').map(v => v.voice)).toEqual(['kirill', 'madi_ru', 'sergey']);
    expect(filterVoices(CATALOG, 'ровн', 'female').map(v => v.voice)).toEqual(['alena']);
    expect(filterVoices(CATALOG, '  ', 'all')).toHaveLength(CATALOG.length);
  });
});

describe('фраза для прослушки из приветствия', () => {
  it('целые предложения, пока не наберётся ~40 символов', () => {
    expect(greetingSample('Привет! Я Сергей, руковожу командой проекта. Давай разберёмся, что горит.'))
      .toBe('Привет! Я Сергей, руковожу командой проекта.');
  });

  it('длинное предложение режется на 120 символах многоточием', () => {
    const long = 'Слово '.repeat(40).trim() + '.';
    const sample = greetingSample(long)!;
    expect(sample.length).toBeLessThanOrEqual(120);
    expect(sample.endsWith('…')).toBe(true);
  });

  it('нет приветствия — нет своей фразы', () => {
    expect(greetingSample('')).toBeNull();
    expect(greetingSample('   ')).toBeNull();
    expect(greetingSample(undefined)).toBeNull();
  });
});

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
