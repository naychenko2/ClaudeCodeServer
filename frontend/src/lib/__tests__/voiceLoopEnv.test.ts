import { describe, it, expect, vi, afterEach } from 'vitest';

// Сигнал и Wake Lock режима разговора живут «поверх» необязательных браузерных API:
// на десктопном Safari нет wakeLock, в тестовом окружении нет WebAudio. Контракт один —
// тихая деградация: функции не бросают и не мешают петле работать без звука/блокировки.
describe('beep и wakeLock без браузерных API', () => {
  afterEach(() => {
    // Обязательно ДО остального: тест с фейковыми таймерами мог упасть на ассерте и не
    // дойти до своего useRealTimers — тогда следующие тесты висли бы на реальных ожиданиях
    vi.useRealTimers();
    vi.resetModules();
    delete (globalThis as Record<string, unknown>).window;
    delete (globalThis as Record<string, unknown>).navigator;
    delete (globalThis as Record<string, unknown>).document;
  });

  it('beep: без window и AudioContext не бросает', async () => {
    const { primeBeep, beep, closeBeep } = await import('../beep');
    expect(() => { primeBeep(); beep(); closeBeep(); }).not.toThrow();
  });

  it('beep: window есть, AudioContext нет — тоже молча', async () => {
    Object.assign(globalThis, { window: {} });
    const { primeBeep, beep, closeBeep } = await import('../beep');
    expect(() => { primeBeep(); beep(); closeBeep(); }).not.toThrow();
  });

  it('фоновая пульсация: без WebAudio не бросает и не плодит таймеры', async () => {
    Object.assign(globalThis, { window: {} });
    const { startThinking, stopThinking, closeBeep } = await import('../beep');
    expect(() => { startThinking(); startThinking(); stopThinking(); }).not.toThrow();
    // Повторный стоп после закрытия контекста — тоже штатная ситуация (размонтирование
    // компонента поверх уже погашенной петли)
    expect(() => { startThinking(); closeBeep(); stopThinking(); }).not.toThrow();
  });

  it('фоновая пульсация: тикает по таймеру и глохнет по stopThinking', async () => {
    vi.useFakeTimers();
    const started: number[] = [];
    // Минимальный AudioContext: считаем запущенные осцилляторы — это и есть «тики»
    const audioCtx = {
      state: 'running',
      currentTime: 0,
      createOscillator: () => ({
        type: '', frequency: { value: 0 }, connect: () => {},
        start: () => started.push(1), stop: () => {},
      }),
      createGain: () => ({
        gain: { setValueAtTime: () => {}, exponentialRampToValueAtTime: () => {} },
        connect: () => {},
      }),
      destination: {},
      resume: async () => {},
      close: async () => {},
    };
    Object.assign(globalThis, { window: { AudioContext: function () { return audioCtx; } } });
    const { primeBeep, startThinking, stopThinking, needAnswer } = await import('../beep');
    primeBeep();
    startThinking();
    expect(started.length, 'первый тик сразу — подтверждение, что вопрос принят').toBe(1);
    vi.advanceTimersByTime(5000);
    expect(started.length, 'дальше пульсация идёт по таймеру').toBeGreaterThan(1);
    const afterStop = started.length;
    stopThinking();
    vi.advanceTimersByTime(5000);
    expect(started.length, 'после stopThinking тиков нет').toBe(afterStop);

    // «Слушаю» — двойной тик сразу и повтор по редкому таймеру, пока микрофон открыт
    const { startListening, stopListening } = await import('../beep');
    const beforeListen = started.length;
    startListening();
    expect(started.length - beforeListen, 'двойной тик при открытии микрофона').toBe(2);
    vi.advanceTimersByTime(6000);
    expect(started.length - beforeListen, 'и повтор через несколько секунд').toBe(4);
    stopListening();
    const afterListen = started.length;
    vi.advanceTimersByTime(12_000);
    expect(started.length, 'после stopListening тиков нет').toBe(afterListen);

    // «Нужно решение» — ровно три пинга одним вызовом, без собственного таймера
    const beforeAnswer = started.length;
    needAnswer();
    expect(started.length - beforeAnswer, 'сигнал «нужно решение» — три ноты').toBe(3);
    vi.advanceTimersByTime(5000);
    expect(started.length - beforeAnswer, 'и он не повторяется сам').toBe(3);
  });

  it('wakeLock: без navigator.wakeLock не бросает', async () => {
    Object.assign(globalThis, { navigator: {}, document: { visibilityState: 'visible', addEventListener: () => {}, removeEventListener: () => {} } });
    const { requestWakeLock, releaseWakeLock } = await import('../wakeLock');
    expect(() => { requestWakeLock(); releaseWakeLock(); }).not.toThrow();
  });

  it('wakeLock: берёт и отпускает блокировку, когда API есть', async () => {
    let released = false;
    const listeners: Record<string, () => void> = {};
    Object.assign(globalThis, {
      navigator: {
        wakeLock: {
          request: async () => ({
            released: false,
            release: async () => { released = true; },
            addEventListener: (t: string, cb: () => void) => { listeners[t] = cb; },
          }),
        },
      },
      document: { visibilityState: 'visible', addEventListener: () => {}, removeEventListener: () => {} },
    });
    const { requestWakeLock, releaseWakeLock } = await import('../wakeLock');
    requestWakeLock();
    await new Promise(r => setTimeout(r, 5));
    releaseWakeLock();
    await new Promise(r => setTimeout(r, 5));
    expect(released).toBe(true);
  });

  it('wakeLock: отказ API (не жест/фон) не роняет вызов', async () => {
    Object.assign(globalThis, {
      navigator: { wakeLock: { request: async () => { throw new Error('NotAllowedError'); } } },
      document: { visibilityState: 'visible', addEventListener: () => {}, removeEventListener: () => {} },
    });
    const { requestWakeLock, releaseWakeLock } = await import('../wakeLock');
    expect(() => requestWakeLock()).not.toThrow();
    await new Promise(r => setTimeout(r, 5));
    expect(() => releaseWakeLock()).not.toThrow();
  });
});
