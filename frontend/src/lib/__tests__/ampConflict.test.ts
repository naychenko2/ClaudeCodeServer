import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import {
  AmpConflictDetector, EARLY_CONFLICT_MS,
  isAmpUnsafeDevice, markAmpUnsafeDevice, clearAmpUnsafeDevice,
} from '../ampConflict';

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

// Память устройства о конфликте: вердикт обязан пережить перезагрузку вкладки.
// Пока он жил модульной переменной, каждое новое открытие страницы платило за
// урок первым циклом слушания целиком (замер на планшете: ~7 с глухоты).
//
// Окружение тестов — node, своего localStorage там нет: подставляем минимальный
// in-memory стаб, иначе проверялась бы только ветка «хранилище недоступно»
function stubStorage(): void {
  const data = new Map<string, string>();
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: {
      getItem: (k: string) => data.get(k) ?? null,
      setItem: (k: string, v: string) => { data.set(k, v); },
      removeItem: (k: string) => { data.delete(k); },
    },
  });
}

function dropStorage(): void {
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    get() { throw new Error('доступ запрещён'); },
  });
}

describe('память устройства о конфликте захватов', () => {
  beforeEach(() => { stubStorage(); clearAmpUnsafeDevice(); });
  afterEach(() => { Reflect.deleteProperty(globalThis, 'localStorage'); });

  it('по умолчанию устройство считается пригодным для честной амплитуды', () => {
    expect(isAmpUnsafeDevice()).toBe(false);
  });

  it('отметка переживает перечитывание — хранится вне памяти модуля', () => {
    markAmpUnsafeDevice();
    expect(isAmpUnsafeDevice()).toBe(true);
    expect(localStorage.getItem('ampUnsafeDevice')).toBe('1');
  });

  it('сброс возвращает устройство в исходное состояние', () => {
    markAmpUnsafeDevice();
    clearAmpUnsafeDevice();
    expect(isAmpUnsafeDevice()).toBe(false);
  });

  it('недоступный localStorage не роняет чтение и запись', () => {
    dropStorage();
    expect(() => markAmpUnsafeDevice()).not.toThrow();
    expect(isAmpUnsafeDevice()).toBe(false);
  });
});

// Ранний вердикт: ждать конца цикла Web Speech нельзя — он тянется 5-6 секунд, и
// всё сказанное за них пропадает. Расхождение видно уже на второй секунде
describe('ранний вердикт конфликта', () => {
  const t0 = 1_000_000;

  function speaking(): AmpConflictDetector {
    const d = new AmpConflictDetector();
    d.setStream(true);
    d.noteVoice(t0);
    return d;
  }

  it('голос звучит дольше порога, движок молчит — конфликт', () => {
    const d = speaking();
    expect(d.earlyConflict(t0 + EARLY_CONFLICT_MS)).toBe(true);
  });

  it('до порога молчим: движок отзывается не мгновенно', () => {
    const d = speaking();
    expect(d.earlyConflict(t0 + EARLY_CONFLICT_MS - 1)).toBe(false);
  });

  it('движок услышал звук — подозрение снято даже спустя время', () => {
    const d = speaking();
    d.noteEngineHeard();
    expect(d.earlyConflict(t0 + 10 * EARLY_CONFLICT_MS)).toBe(false);
  });

  it('без нашего потока конфликта быть не может', () => {
    const d = new AmpConflictDetector();
    d.noteVoice(t0);
    expect(d.earlyConflict(t0 + 10 * EARLY_CONFLICT_MS)).toBe(false);
  });

  it('тишина не копит подозрение: отсчёт идёт от первого громкого кадра', () => {
    const d = new AmpConflictDetector();
    d.setStream(true);
    expect(d.earlyConflict(t0 + 10 * EARLY_CONFLICT_MS)).toBe(false);
    d.noteVoice(t0 + 10 * EARLY_CONFLICT_MS);
    expect(d.earlyConflict(t0 + 10 * EARLY_CONFLICT_MS)).toBe(false);
  });

  it('конец цикла перезаряжает отсчёт — прошлый голос на новый цикл не давит', () => {
    const d = speaking();
    d.cycleEnd(true);
    expect(d.earlyConflict(t0 + 10 * EARLY_CONFLICT_MS)).toBe(false);
  });

  it('слух движка живёт ровно один цикл', () => {
    const d = speaking();
    d.noteEngineHeard();
    d.cycleEnd(false);
    d.noteVoice(t0);
    expect(d.earlyConflict(t0 + EARLY_CONFLICT_MS)).toBe(true);
  });
});
