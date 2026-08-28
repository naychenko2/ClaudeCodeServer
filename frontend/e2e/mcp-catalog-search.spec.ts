import { test, expect, type APIRequestContext, type Page } from '@playwright/test';

// Каталог MCP-серверов: поиск и подтверждение пробы (задача f318725b).
// Два дефекта приёмки:
//   D6: поиск фильтровал загруженную первую страницу вместо запроса в реестр —
//       «filesystem» давал «ничего не нашлось», хотя записи есть на 2–4 странице
//   D7: проба каталожной stdio-записи у local-владельца получала 400 с
//       requiresConfirmation и показывала общую плашку «Проверка не удалась»
//       вместо диалога с полной строкой запуска

declare const process: { env: Record<string, string | undefined> };
const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

async function ensureCatalogFlag(request: APIRequestContext, token: string) {
  await request.put('/api/feature-flags/mcp-catalog', {
    headers: { Authorization: `Bearer ${token}` },
    data: { enabled: true },
  });
}

async function openMcpModal(page: Page) {
  await page.goto('/');
  await page.waitForLoadState('networkidle');
  const loginInput = page.locator('input[placeholder="Имя пользователя"]');
  if (await loginInput.count() > 0) {
    await loginInput.fill(USER);
    await page.locator('input[placeholder="Пароль"]').fill(PASS);
    await page.getByRole('button', { name: /войти|подключиться/i }).click();
    await page.waitForTimeout(3000);
  }
  const avatar = page.locator('[aria-label^="Меню пользователя"]');
  await avatar.waitFor({ state: 'visible', timeout: 15000 });
  await avatar.click();
  const mcpItem = page.getByRole('button', { name: /MCP-серверы/ });
  await mcpItem.waitFor({ state: 'visible', timeout: 10000 });
  await mcpItem.click();
}

async function openCatalogTab(page: Page) {
  const catalogTab = page.getByRole('button', { name: /^Каталог$/ });
  await catalogTab.waitFor({ state: 'visible', timeout: 10000 });
  await catalogTab.click();
  // Витрина каталога — первая страница реестра
  await page.locator('button.card-act').first().waitFor({ state: 'visible', timeout: 20000 });
}

test('D6: поиск уходит в реестр с q и подгружает следующие страницы', async ({ page, request }) => {
  const token = await login(request);
  await ensureCatalogFlag(request, token);

  // Все запросы поиска: без правки фронт звал эндпоинт РОВНО ОДИН раз с пустым q
  const searchUrls: string[] = [];
  page.on('request', r => {
    const u = r.url();
    if (u.includes('/api/mcp/catalog/search')) searchUrls.push(u);
  });

  await openMcpModal(page);
  await openCatalogTab(page);

  const search = page.locator('input[placeholder*="Поиск" i]').first();
  await expect(search).toBeVisible();
  await search.fill('filesystem');
  // Дебаунс 350 мс + ответ реестра
  await page.waitForTimeout(2500);

  // Запрос с q=filesystem обязан уйти на сервер
  expect(searchUrls.some(u => /[?&]q=filesystem(&|$)/.test(u)),
    `запрос с q=filesystem не ушёл; фактические запросы: ${JSON.stringify(searchUrls)}`).toBeTruthy();

  // Выдача не пуста: на первой странице каталога нет ни одной записи filesystem,
  // поэтому локальный фильтр здесь показывал бы «ничего не нашлось»
  await expect(page.getByText(/ничего не нашлось/)).toHaveCount(0);
  const cards = page.locator('button.card-act');
  const firstPage = await cards.count();
  expect(firstPage, 'по запросу «filesystem» должны найтись записи').toBeGreaterThan(0);

  // Найденное относится к запросу — проверяем по имени/описанию первой карточки
  const firstTitle = await cards.first().innerText();
  expect(firstTitle.toLowerCase()).toContain('filesystem');

  // «Показать ещё» — следующая страница по nextCursor. Считаем на запросе, где у
  // реестра есть новые ИМЕНА дальше первой страницы: реестр пагинирует по версиям
  // (двадцать релизов одного сервера = двадцать записей), и по иному запросу
  // следующая страница законно приносит ноль новых карточек
  await search.fill('postgres');
  await page.waitForTimeout(2500);
  const beforeMore = await cards.count();
  expect(beforeMore, 'по запросу «postgres» должны найтись записи').toBeGreaterThan(0);

  const more = page.getByRole('button', { name: /^Показать ещё$/ });
  await expect(more, 'у выдачи с nextCursor должна быть кнопка «Показать ещё»').toBeVisible();
  await more.click();
  await page.waitForTimeout(6000);

  // Догрузка идёт с курсором, а не повтором того же запроса
  expect(searchUrls.some(u => u.includes('cursor=')),
    `запрос со cursor= не ушёл; фактические запросы: ${JSON.stringify(searchUrls)}`).toBeTruthy();
  // Молча обрываться нельзя: либо карточек стало больше, либо есть внятная подпись,
  // почему список не вырос (у реестра дальше только другие версии тех же серверов)
  const afterMore = await cards.count();
  const note = page.getByText(/Дальше в реестре идут другие версии|Это всё, что нашлось в реестре/);
  const grew = afterMore > beforeMore;
  expect(grew || await note.count() > 0,
    `после «Показать ещё» ни новых карточек (${beforeMore} → ${afterMore}), ни подписи`).toBeTruthy();

  await page.screenshot({ path: 'test-results/catalog-search-server.png', fullPage: false });
});

test('D7: проба каталожного stdio-сервера спрашивает подтверждение запуска', async ({ page, request }) => {
  const token = await login(request);
  await ensureCatalogFlag(request, token);
  const auth = { Authorization: `Bearer ${token}` };

  // Владелец должен быть local — иначе порога подтверждения нет по замыслу
  const me = await (await request.get('/api/auth/me', { headers: auth })).json() as
    { executionEnvironment?: string };
  if (me.executionEnvironment === 'container') {
    test.skip(true, 'владелец в песочнице — подтверждение запуска не требуется');
    return;
  }

  // Каталожную stdio-запись заводим через API: путь «импорт из формы» покрыт
  // соседней спекой (mcp-catalog-qa), здесь проверяется ровно диалог пробы
  const key = 'e2e-probe-confirm';
  const created = await request.post('/api/mcp/servers', {
    headers: auth,
    data: {
      key, label: 'E2E проба каталога', transport: 'stdio',
      command: 'npx', args: ['-y', '@modelcontextprotocol/server-filesystem@2025.8.21', '.'],
      catalogRef: { name: 'io.modelcontextprotocol/e2e-probe', version: '1.0.0' },
    },
  });
  expect(created.ok(), `запись должна создаться: ${await created.text()}`).toBeTruthy();
  const createdId = (await created.json()).id as string;

  try {
    await openMcpModal(page);
    // Вкладка «Серверы» открыта по умолчанию — ищем кнопку «Проверить» нашей записи
    const card = page.locator('div', { hasText: 'E2E проба каталога' });
    await card.first().waitFor({ state: 'visible', timeout: 15000 });
    const probeBtn = page.getByRole('button', { name: /^Проверить$/ }).first();
    await probeBtn.waitFor({ state: 'visible', timeout: 10000 });
    await probeBtn.click();

    // Диалог вместо общей плашки «Проверка не удалась»
    await expect(page.getByText('Запустить этот сервер на вашем компьютере?')).toBeVisible({ timeout: 10000 });
    await expect(page.getByText(/Проверка не удалась/)).toHaveCount(0);
    // Полная строка запуска видна человеку до согласия. Modal рендерится порталом
    // в конец body, поэтому последнее вхождение команды — то, что в диалоге
    // (первое — подпись карточки сервера)
    await expect(page.getByText(/npx -y @modelcontextprotocol\/server-filesystem/).last()).toBeVisible();
    await page.screenshot({ path: 'test-results/catalog-probe-confirm.png', fullPage: false });

    // Отмена ничего не запускает и не оставляет ошибки
    await page.getByRole('button', { name: /^Отмена$/ }).click();
    await expect(page.getByText('Запустить этот сервер на вашем компьютере?')).toHaveCount(0);
    await expect(page.getByText(/Проверка не удалась/)).toHaveCount(0);
  } finally {
    await request.delete(`/api/mcp/servers/${createdId}`, { headers: auth });
  }
});
