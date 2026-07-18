import { describe, it, expect } from 'vitest';
import { describeSpeechError, isSilentSpeechError, MIC_FALLBACK_TEXT } from '../voiceInput';

// Замер на планшетной ширине (тост 280px, кламп 3 строки): влезает ~120 символов.
// Текст на 160 символов терял последнюю строку — и подсказка про меню была не видна.
const TOAST_LIMIT = 120;

describe('MIC_FALLBACK_TEXT', () => {
  it('влезает в трёхстрочный кламп тоста', () => {
    expect(MIC_FALLBACK_TEXT.length).toBeLessThanOrEqual(TOAST_LIMIT);
  });

  it('не теряет подсказку, где вернуть распознавание', () => {
    expect(MIC_FALLBACK_TEXT).toContain('меню аватара');
  });
});

describe('describeSpeechError', () => {
  it('расшифровывает известные коды и всегда добавляет сам код', () => {
    const network = describeSpeechError('network');
    expect(network).toContain('Google');
    expect(network).toContain('(network)');
  });

  it('незнакомый код не теряется — уходит в тост как есть', () => {
    expect(describeSpeechError('какая-то-новая-ошибка')).toContain('какая-то-новая-ошибка');
  });

  // Тост клампится тремя строками — длинные тексты просто не увидят
  it('расшифровки остаются короткими', () => {
    const codes = ['network', 'not-allowed', 'service-not-allowed', 'audio-capture',
      'language-not-supported', 'bad-grammar', 'unknown'];
    for (const code of codes) {
      expect(describeSpeechError(code).length).toBeLessThanOrEqual(TOAST_LIMIT);
    }
  });
});

describe('isSilentSpeechError', () => {
  it('молчит про штатные исходы: отмену и тишину', () => {
    expect(isSilentSpeechError('aborted')).toBe(true);
    expect(isSilentSpeechError('no-speech')).toBe(true);
  });

  it('о настоящих сбоях сообщает', () => {
    expect(isSilentSpeechError('network')).toBe(false);
    expect(isSilentSpeechError('not-allowed')).toBe(false);
  });
});
