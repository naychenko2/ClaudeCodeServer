// panelHtml (ADR-031 §3.1): URL панели строится из entry манифеста (contributes.panels[].entry),
// а не из хардкода panel/index.html. Маршрут бэка — {**path} (catch-all), поэтому слэши entry
// остаются литеральными. Мутация «вернуть хардкод panel/index.html» → URL снова panel/index.html →
// тест с entry=panel/main.html красный.
//
// Окружение node: fetch — глобальный, шимим vi.stubGlobal; readStoredToken в node даёт null
// (typeof localStorage === 'undefined'), токен в заголовках не нужен.

import { afterEach, describe, expect, it, vi } from 'vitest';

const { api } = await import('../api');

// Фейк fetch: возвращает ok-ответ с текстом и запоминает URL первого вызова
function stubFetch() {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: true,
    status: 200,
    statusText: 'OK',
    text: () => Promise.resolve('<html></html>'),
    json: () => Promise.resolve({}),
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

describe('api.plugins.panelHtml: entry из манифеста в URL', () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  it('entry=panel/main.html → GET .../assets/panel/main.html (не хардкод)', async () => {
    const fetchMock = stubFetch();
    await api.plugins.panelHtml('local-1/main', '1.0.0', 'panel/main.html');
    const url = vi.mocked(fetchMock).mock.calls[0]?.[0] as string;
    expect(url).toBe('/api/plugins/local-1~main/1.0.0/assets/panel/main.html');
  });

  it('entry=panel/index.html (стандартный) → .../assets/panel/index.html', async () => {
    const fetchMock = stubFetch();
    await api.plugins.panelHtml('local-1/idx', '2.0.0', 'panel/index.html');
    const url = vi.mocked(fetchMock).mock.calls[0]?.[0] as string;
    expect(url).toBe('/api/plugins/local-1~idx/2.0.0/assets/panel/index.html');
  });

  it('entry=panel/sub/main.html (вложенная) → .../assets/panel/sub/main.html', async () => {
    const fetchMock = stubFetch();
    await api.plugins.panelHtml('local-1/sub', '1.0.0', 'panel/sub/main.html');
    const url = vi.mocked(fetchMock).mock.calls[0]?.[0] as string;
    expect(url).toBe('/api/plugins/local-1~sub/1.0.0/assets/panel/sub/main.html');
  });
});

// === MAJOR 2: entry из недоверенного манифеста валидируется ДО запроса ===
// Форму `panel/...` (без «..»/«?»/«#»/«%»/«\»/схемы/управляющих) принимаем, сегменты
// кодируем encodeURIComponent ПОСЛЕ проверки. Невалидный entry → запрос НЕ идёт (иначе
// «..»/«?» уведут Bearer-запрос на чужой маршрут) и понятная ошибка. Мутация «убрать
// проверку» (всегда делать запрос) красит эти тесты.
describe('api.plugins.panelHtml: валидация entry (MAJOR 2)', () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  // entry → запрос не идёт, ошибка «Панель плагина указана неверно»
  async function expectInvalid(entry: unknown): Promise<void> {
    const fetchMock = stubFetch();
    let threw: unknown = null;
    try {
      await api.plugins.panelHtml('local-1/x', '1.0.0', entry as string);
    } catch (e) {
      threw = e;
    }
    expect(threw, `entry ${JSON.stringify(entry)} не отбито`).toBeInstanceOf(Error);
    expect((threw as Error).message).toBe('Панель плагина указана неверно');
    expect(fetchMock, 'запрос ушёл при невалидном entry').not.toHaveBeenCalled();
  }

  it('«../../x» (точечные сегменты) → запроса нет', () => expectInvalid('../../x'));
  it('«panel/../x» (точечный сегмент внутри) → запроса нет', () => expectInvalid('panel/../x'));
  it('«/panel/x» (ведущий слэш) → запроса нет', () => expectInvalid('/panel/x'));
  it('«https://x» (схема) → запроса нет', () => expectInvalid('https://x'));
  it('«panel/a?b» (вопрос) → запроса нет', () => expectInvalid('panel/a?b'));
  it('«panel/a#b» (якорь) → запроса нет', () => expectInvalid('panel/a#b'));
  it('«panel\\x» (обратный слэш) → запроса нет', () => expectInvalid('panel\\x'));
  it('«panel/%2e%2e/x» (процент) → запроса нет', () => expectInvalid('panel/%2e%2e/x'));
  it('пустая строка → запроса нет', () => expectInvalid(''));
  it('не-строка (null) → запроса нет', () => expectInvalid(null));
  it('не-строка (число) → запроса нет', () => expectInvalid(42));
});
