import { describe, it, expect } from 'vitest';
import { createBargeDetector, BARGE_DEFAULTS, type BargeAction } from '../bargeDetect';

// Двухступенчатый детектор перебивания. Кадры подаём вручную — по 32 мс, как отдаёт
// модель Silero v5 на 16 кГц. Считаем именно кадрами: пороги в миллисекундах внутри
// округляются вверх, и «примерно столько же» в тесте маскировало бы ошибку на границе.
const LOUD = 0.2;   // речь рядом с микрофоном
const QUIET = 0.01; // телевизор за стеной
const SPEECH = 0.95;
const SILENCE = 0.1;

const F = BARGE_DEFAULTS.frameMs;
const TO_DUCK = Math.ceil(BARGE_DEFAULTS.duckMs / F);      // 10-й кадр речи — приглушение
const TO_CUT = Math.ceil(BARGE_DEFAULTS.cutMs / F);        // 25-й — обрыв
const TO_RELEASE = Math.ceil(BARGE_DEFAULTS.releaseMs / F); // 13-й кадр тишины — отбой

function feed(d: ReturnType<typeof createBargeDetector>, count: number, prob: number, rms: number): BargeAction[] {
  const out: BargeAction[] = [];
  for (let i = 0; i < count; i++) {
    const a = d.push(prob, rms);
    if (a !== 'none') out.push(a);
  }
  return out;
}

describe('createBargeDetector', () => {
  it('громкая речь: сперва приглушение, потом обрыв', () => {
    const d = createBargeDetector();
    expect(feed(d, TO_DUCK - 1, SPEECH, LOUD)).toEqual([]);
    expect(d.push(SPEECH, LOUD)).toBe('duck');
    expect(feed(d, TO_CUT - TO_DUCK - 1, SPEECH, LOUD)).toEqual([]);
    expect(d.push(SPEECH, LOUD)).toBe('cut');
  });

  it('короткая реплика: приглушили и вернули громкость, ответ не потерян', () => {
    const d = createBargeDetector();
    expect(feed(d, TO_DUCK, SPEECH, LOUD)).toEqual(['duck']);
    // Заговоривший рядом замолчал раньше второй ступени
    expect(feed(d, TO_RELEASE, SILENCE, QUIET)).toEqual(['release']);
  });

  it('тихий источник не перебивает вовсе: гейт громкости', () => {
    const d = createBargeDetector();
    // Речь по всем признакам, но тише абсолютного минимума — телевизор за стеной
    expect(feed(d, TO_CUT * 2, SPEECH, QUIET)).toEqual([]);
  });

  it('в шумной комнате порог растёт вместе с фоном', () => {
    const d = createBargeDetector();
    // Долгий громкий фон без речи (гул, музыка) поднимает планку
    feed(d, 200, SILENCE, 0.1);
    expect(d.background()).toBeGreaterThan(0.05);
    // Речь вровень с фоном гейт не проходит
    expect(feed(d, TO_CUT, SPEECH, 0.12)).toEqual([]);
    // ...а заметно более громкая — проходит
    expect(feed(d, TO_DUCK, SPEECH, 0.6)).toEqual(['duck']);
  });

  it('рваная речь с долгими паузами не суммируется до обрыва', () => {
    const d = createBargeDetector();
    // Реплики по 6 кадров (~190 мс) с паузами: ни одна не дотягивает даже до
    // приглушения, и накопленное каждый раз обнуляется
    for (let i = 0; i < 3; i++) {
      expect(feed(d, TO_DUCK - 4, SPEECH, LOUD)).toEqual([]);
      expect(feed(d, TO_RELEASE, SILENCE, QUIET)).toEqual([]);
    }
  });

  it('пауза короче окна возврата приглушение не снимает', () => {
    const d = createBargeDetector();
    expect(feed(d, TO_DUCK, SPEECH, LOUD)).toEqual(['duck']);
    // Человек перевёл дыхание — это ещё не «ложная тревога»
    expect(feed(d, TO_RELEASE - 1, SILENCE, QUIET)).toEqual([]);
    // ...и договорил: копившееся до паузы не пропало, вторая ступень наступает
    expect(feed(d, TO_CUT - TO_DUCK, SPEECH, LOUD)).toEqual(['cut']);
  });

  it('reset возвращает громкость только если она была приглушена', () => {
    const d = createBargeDetector();
    expect(d.reset()).toBe('none');
    feed(d, TO_DUCK, SPEECH, LOUD);
    expect(d.reset()).toBe('release');
    expect(d.reset()).toBe('none');
  });
});
