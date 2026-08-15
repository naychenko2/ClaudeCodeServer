import { describe, it, expect, afterEach } from 'vitest';
import { observeWidth } from './useContainerWidth';

// Окружение тестов — node, настоящего ResizeObserver там нет: подменяем своим,
// чтобы проверить и подписку, и отцепку
class FakeResizeObserver {
  static instances: FakeResizeObserver[] = [];
  observed: unknown[] = [];
  disconnected = false;
  constructor(private cb: () => void) { FakeResizeObserver.instances.push(this); }
  observe(el: unknown) { this.observed.push(el); }
  unobserve() {}
  disconnect() { this.disconnected = true; }
  fire() { this.cb(); }
}

function fakeNode(width: number) {
  return { clientWidth: width } as unknown as HTMLElement;
}

function useFakeObserver() {
  FakeResizeObserver.instances = [];
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver = FakeResizeObserver;
}

afterEach(() => { delete (globalThis as { ResizeObserver?: unknown }).ResizeObserver; });

describe('observeWidth', () => {
  it('меряет узел сразу при подключении, в том числе позднем', () => {
    useFakeObserver();
    const widths: number[] = [];
    // Узел появился уже после монтирования панели (мобильная ветка сменилась десктопной)
    observeWidth(fakeNode(900), w => widths.push(w));
    expect(widths).toEqual([900]);
    expect(FakeResizeObserver.instances[0].observed).toHaveLength(1);
  });

  it('перемер узла на ресайзе', () => {
    useFakeObserver();
    const node = fakeNode(900);
    const widths: number[] = [];
    observeWidth(node, w => widths.push(w));
    (node as unknown as { clientWidth: number }).clientWidth = 600;
    FakeResizeObserver.instances[0].fire();
    expect(widths).toEqual([900, 600]);
  });

  it('переезд на другой узел: старый наблюдатель отцеплен, новый узел измерен', () => {
    useFakeObserver();
    const widths: number[] = [];
    const stop = observeWidth(fakeNode(900), w => widths.push(w));
    stop();
    observeWidth(fakeNode(500), w => widths.push(w));
    expect(widths).toEqual([900, 500]);
    expect(FakeResizeObserver.instances[0].disconnected).toBe(true);
    expect(FakeResizeObserver.instances[1].disconnected).toBe(false);
  });

  it('без ResizeObserver замер всё равно происходит, отцепка безопасна', () => {
    delete (globalThis as { ResizeObserver?: unknown }).ResizeObserver;
    const widths: number[] = [];
    const stop = observeWidth(fakeNode(720), w => widths.push(w));
    expect(widths).toEqual([720]);
    expect(() => stop()).not.toThrow();
  });
});
