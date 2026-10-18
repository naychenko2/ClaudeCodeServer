// Стор «← назад» (featureBackNav): откуда пришли в ключевой чат, в памяти вкладки.
// Чистая логика (без DOM): запись/сброс/чтение по текущему чату.
import { describe, expect, it, beforeEach } from 'vitest';
import { recordBackNav, clearBackNav, getBackNav, backNavFrom, __test } from './featureBackNav';

beforeEach(() => __test.reset());

describe('featureBackNav', () => {
  it('recordBackNav: записывает from→to; backNavFrom(to) = from, backNavFrom(другой) = null', () => {
    recordBackNav('chat-exec', 'chat-key');
    expect(getBackNav()).toEqual({ from: 'chat-exec', to: 'chat-key' });
    expect(backNavFrom('chat-key')).toBe('chat-exec');
    expect(backNavFrom('other')).toBeNull();
    expect(backNavFrom(null)).toBeNull();
  });

  it('recordBackNav(from, from) — не переход: указатель не ставится', () => {
    recordBackNav('chat-a', 'chat-a');
    expect(getBackNav()).toBeNull();
    expect(backNavFrom('chat-a')).toBeNull();
  });

  it('recordBackNav с пустым from/to — не записывается', () => {
    recordBackNav('', 'chat-key');
    expect(getBackNav()).toBeNull();
    recordBackNav('chat-exec', '');
    expect(getBackNav()).toBeNull();
  });

  it('clearBackNav: сбрасывает; повторный clear — идемпотентен', () => {
    recordBackNav('chat-exec', 'chat-key');
    clearBackNav();
    expect(getBackNav()).toBeNull();
    expect(backNavFrom('chat-key')).toBeNull();
    clearBackNav(); // не падает
    expect(getBackNav()).toBeNull();
  });

  it('recordBackNav поверх прежнего — заменяет указатель (один «← назад»)', () => {
    recordBackNav('chat-a', 'chat-key');
    recordBackNav('chat-b', 'chat-key');
    expect(getBackNav()).toEqual({ from: 'chat-b', to: 'chat-key' });
    expect(backNavFrom('chat-key')).toBe('chat-b');
  });

  it('backNavFrom: только когда to === текущий чат (иначе null, даже если from совпадает)', () => {
    recordBackNav('chat-exec', 'chat-key');
    // from = 'chat-exec', но мы в 'chat-exec' (это from, не to) → null.
    expect(backNavFrom('chat-exec')).toBeNull();
  });
});
