// Единственная точка подмены геттера globalThis.localStorage в тестах фичи:
// дескриптор возвращается в finally, а утечка бросающего геттера сразу красит тест.
export function withThrowingLocalStorage<T>(getter: () => never, fn: () => T): T {
  const orig = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
  Object.defineProperty(globalThis, 'localStorage', { configurable: true, get: getter });
  const restore = () => {
    if (orig) Object.defineProperty(globalThis, 'localStorage', orig);
    else delete (globalThis as { localStorage?: unknown }).localStorage;
    if (orig && 'get' in orig && orig.get) return; // собственный геттер окружения — не проверяем
    try { void globalThis.localStorage; } catch (e) {
      throw new Error('после withThrowingLocalStorage остался бросающий геттер localStorage: ' + String(e));
    }
  };
  let result: T;
  try {
    result = fn();
  } catch (e) {
    restore();
    throw e;
  }
  if (result instanceof Promise) {
    return result.finally(restore) as T;
  }
  restore();
  return result;
}
