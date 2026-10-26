// Серия glif в аналитике расхода: источник с подписью и своим цветом (отличным
// от fal), относится к «генерациям медиа» — рендерится счётчиком, без токенов.
import { describe, it, expect } from 'vitest';
import { C } from 'aihome_shell/kit';
import {
  GEN_SOURCES, SPEND_SOURCES, SPEND_TIERS, isGenSource, sourceColor, sourceLabel, sourceTextColor,
  tierColor,
} from '../spendModel';

describe('SPEND_SOURCES: серия glif', () => {
  it('glif присутствует с подписью и цветом серии', () => {
    expect(SPEND_SOURCES.glif).toEqual({ label: 'glif', color: C.warning });
  });

  it('цвет glif отличен от fal — серии различимы на графике', () => {
    expect(SPEND_SOURCES.glif.color).not.toBe(SPEND_SOURCES.fal.color);
  });

  it('хелперы: label/color по ключу, «текстовый» цвет — text-пара токена', () => {
    expect(sourceLabel('glif')).toBe('glif');
    expect(sourceColor('glif')).toBe(C.warning);
    expect(sourceTextColor('glif')).toBe(C.warningText);
    expect(sourceTextColor('fal')).toBe(C.planText);
    // Прочие источники — без text-пары: текстовый цвет совпадает с цветом серии
    expect(sourceTextColor('chat-turn')).toBe(sourceColor('chat-turn'));
  });
});

describe('isGenSource: источники без токенов', () => {
  it('fal, glif и озвучка — счётчиком вызовов; токенные источники — нет', () => {
    // Озвучка тоже считается вызовами, а не токенами: тарификация SpeechKit идёт за запрос
    expect(GEN_SOURCES).toEqual(['fal', 'glif', 'tts']);
    expect(isGenSource('glif')).toBe(true);
    expect(isGenSource('fal')).toBe(true);
    expect(isGenSource('tts')).toBe(true);
    expect(isGenSource('chat-turn')).toBe(false);
    expect(isGenSource('one-shot')).toBe(false);
    expect(isGenSource('free')).toBe(false);
  });
});

describe('SPEND_TIERS: ценовые классы моделей', () => {
  it('у каждого класса своя подпись и свой токен цвета', () => {
    expect(SPEND_TIERS.free).toEqual({ label: 'Бесплатные', color: C.success });
    expect(SPEND_TIERS.cheap).toEqual({ label: 'Дёшево', color: C.info });
    expect(SPEND_TIERS.expensive).toEqual({ label: 'Дорого', color: C.warning });
  });

  it('хелперы: цвет по ключу', () => {
    expect(tierColor('free')).toBe(C.success);
    expect(tierColor('cheap')).toBe(C.info);
    expect(tierColor('expensive')).toBe(C.warning);
  });

  it('неизвестный ключ и null не роняют — нейтральный цвет палитры', () => {
    expect(tierColor('vip')).toBe(C.textMuted);
    expect(tierColor(null)).toBe(C.textMuted);
    expect(tierColor(undefined)).toBe(C.textMuted);
  });
});
