import { describe, it, expect, afterEach } from 'vitest';
import { isAmpUnsafePlatform, isParallelCaptureUnsafe } from '../useMicLevel';

// Гейт платформ для второго захвата микрофона. Пессимизм здесь сознательный:
// ложный позитив стоит псевдо-сияния, ложный негатив — потерянной речи.
// Замер на Android-планшете: первая фраза разговора пропадала целиком.
const original = Object.getOwnPropertyDescriptor(globalThis, 'navigator');

function withUa(ua: string, gate: () => boolean): boolean {
  Object.defineProperty(globalThis, 'navigator', {
    configurable: true, value: { userAgent: ua },
  });
  return gate();
}

// UA с боевого дампа: Chrome урезает его до «Android 10; K» и на телефоне, и на
// планшете, токен Mobile при этом остаётся
const ANDROID = 'Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Mobile Safari/537.36';
// Планшет без токена Android — проверяет ветку Tablet отдельно
const TABLET = 'Mozilla/5.0 (Linux; Tablet; rv:120.0) Gecko/120.0 Firefox/120.0';
const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1';
const IPAD = 'Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/604.1';
const SAFARI_MAC = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15';
const CHROME_WIN = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36';
const CHROME_LINUX = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36';
const CHROME_MAC = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36';

afterEach(() => {
  if (original) Object.defineProperty(globalThis, 'navigator', original);
  else Reflect.deleteProperty(globalThis, 'navigator');
});

// Гейт последовательного захвата: им закрыт и VAD-канал барж-ина, который живёт в
// фазе озвучки при ЗАКРЫТОМ распознавании. Android сюда попадать не должен —
// иначе перебивание голосом выключится там, где конфликта нет
describe('isAmpUnsafePlatform (второй захват ломает движок насовсем)', () => {
  it.each([['iPhone', IPHONE], ['iPad', IPAD], ['Safari macOS', SAFARI_MAC]])(
    '%s — WebKit, захват запрещён', (_name, ua) => {
      expect(withUa(ua, isAmpUnsafePlatform)).toBe(true);
    });

  it('Android остаётся с барж-ином: там ломается только ПАРАЛЛЕЛЬНЫЙ захват', () => {
    expect(withUa(ANDROID, isAmpUnsafePlatform)).toBe(false);
  });

  it.each([['Chrome Windows', CHROME_WIN], ['Chrome Linux', CHROME_LINUX], ['Chrome macOS', CHROME_MAC]])(
    '%s — десктоп, захват разрешён', (_name, ua) => {
      expect(withUa(ua, isAmpUnsafePlatform)).toBe(false);
    });

  it('без navigator считаем платформу опасной', () => {
    Reflect.deleteProperty(globalThis, 'navigator');
    expect(isAmpUnsafePlatform()).toBe(true);
  });
});

// Гейт параллельного захвата: только честная амплитуда держит свой поток
// ОДНОВРЕМЕННО с открытым Web Speech
describe('isParallelCaptureUnsafe (амплитуда под открытым распознаванием)', () => {
  it.each([
    ['Android (UA урезан до «Android 10; K»)', ANDROID],
    ['планшет с токеном Tablet', TABLET],
    ['iPhone', IPHONE],
    ['iPad', IPAD],
    ['Safari macOS', SAFARI_MAC],
  ])('%s — честную амплитуду не пробуем', (_name, ua) => {
    expect(withUa(ua, isParallelCaptureUnsafe)).toBe(true);
  });

  it.each([
    ['Chrome на Windows', CHROME_WIN],
    ['Chrome на Linux', CHROME_LINUX],
    ['Chrome на macOS', CHROME_MAC],
  ])('%s — десктоп, параллельный захват безболезнен', (_name, ua) => {
    expect(withUa(ua, isParallelCaptureUnsafe)).toBe(false);
  });

  it('без navigator считаем платформу опасной', () => {
    Reflect.deleteProperty(globalThis, 'navigator');
    expect(isParallelCaptureUnsafe()).toBe(true);
  });
});
