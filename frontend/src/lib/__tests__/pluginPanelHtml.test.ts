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
});
