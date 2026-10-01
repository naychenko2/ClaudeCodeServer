import { describe, expect, it } from 'vitest';
import { swapSourceKeepingPosition, type MediaLike } from './abSwitch';

// Поддельный <audio>: как настоящий, смена src сбрасывает позицию и ставит на паузу,
// а метаданные приходят позже — по fireMeta()
class FakeMedia implements MediaLike {
  src = '';
  currentTime = 0;
  paused = true;
  duration = NaN;
  plays = 0;
  private listeners: Array<() => void> = [];
  constructor(private lengths: Record<string, number>) {}
  play() { this.paused = false; this.plays++; }
  pause() { this.paused = true; }
  load() { this.currentTime = 0; this.paused = true; this.duration = NaN; }
  addEventListener(_t: 'loadedmetadata', fn: () => void) { this.listeners.push(fn); }
  removeEventListener(_t: 'loadedmetadata', fn: () => void) { this.listeners = this.listeners.filter(f => f !== fn); }
  fireMeta() {
    this.duration = this.lengths[this.src];
    const ls = this.listeners;
    this.listeners = [];
    ls.forEach(f => f());
  }
}

describe('A/B без сброса позиции', () => {
  it('во время воспроизведения позиция сохраняется и звук продолжается', () => {
    const el = new FakeMedia({ a: 16, b: 16 });
    el.src = 'a';
    el.currentTime = 7.4;
    el.paused = false;
    swapSourceKeepingPosition(el, 'b');
    expect(el.currentTime).toBe(0); // до метаданных браузер уже сбросил позицию
    el.fireMeta();
    expect(el.src).toBe('b');
    expect(el.currentTime).toBe(7.4);
    expect(el.paused).toBe(false);
  });

  it('на паузе позиция сохраняется, а воспроизведение само не начинается', () => {
    const el = new FakeMedia({ a: 16, b: 16 });
    el.src = 'a';
    el.currentTime = 3;
    swapSourceKeepingPosition(el, 'b');
    el.fireMeta();
    expect(el.currentTime).toBe(3);
    expect(el.paused).toBe(true);
    expect(el.plays).toBe(0);
  });

  it('версия короче позиции — встаём в её конец', () => {
    const el = new FakeMedia({ a: 20, b: 12 });
    el.src = 'a';
    el.currentTime = 15;
    swapSourceKeepingPosition(el, 'b');
    el.fireMeta();
    expect(el.currentTime).toBe(12);
  });

  it('отмена до метаданных не трогает позицию нового файла', () => {
    const el = new FakeMedia({ a: 16, b: 16 });
    el.src = 'a';
    el.currentTime = 9;
    const cancel = swapSourceKeepingPosition(el, 'b');
    cancel();
    el.fireMeta();
    expect(el.currentTime).toBe(0);
  });
});
