import { describe, it, expect } from 'vitest';
import { AmpConflictDetector } from '../ampConflict';

// Детектор конфликта захватов микрофона: наша амплитуда (getUserMedia) слышит
// голос, движок распознавания — нет. Чистый класс, покрываем юнитами.

describe('AmpConflictDetector', () => {
  it('голос в амплитуде + бесплодный цикл при живом потоке = конфликт', () => {
    const d = new AmpConflictDetector();
    d.setStream(true);
    d.noteVoice();
    expect(d.cycleEnd(true)).toBe(true);
  });

  it('тихий цикл без голоса — не конфликт (обычное молчание человека)', () => {
    const d = new AmpConflictDetector();
    d.setStream(true);
    expect(d.cycleEnd(true)).toBe(false);
  });

  it('движок распознал речь — конфликт невозможен, даже если амплитуда слышала голос', () => {
    const d = new AmpConflictDetector();
    d.setStream(true);
    d.noteVoice();
    expect(d.cycleEnd(false)).toBe(false);
  });

  it('без нашего потока конфликта не бывает (амплитуда в псевдо)', () => {
    const d = new AmpConflictDetector();
    d.noteVoice();
    expect(d.cycleEnd(true)).toBe(false);
  });

  it('слух живёт один цикл: голос прошлого цикла не ложится на молчаливый следующий', () => {
    const d = new AmpConflictDetector();
    d.setStream(true);
    d.noteVoice();
    expect(d.cycleEnd(false)).toBe(false); // плодный цикл съел отметку о голосе
    expect(d.cycleEnd(true)).toBe(false);  // тишина после — не конфликт
  });

  it('закрытие потока обнуляет слух', () => {
    const d = new AmpConflictDetector();
    d.setStream(true);
    d.noteVoice();
    d.setStream(false);
    d.setStream(true);
    expect(d.cycleEnd(true)).toBe(false);
  });

  it('после вердикта-конфликта детектор перезаряжается', () => {
    const d = new AmpConflictDetector();
    d.setStream(true);
    d.noteVoice();
    expect(d.cycleEnd(true)).toBe(true);
    d.noteVoice();
    expect(d.cycleEnd(true)).toBe(true); // конфликт устойчив, пока поток жив
  });
});
