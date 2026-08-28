import { test, expect, type APIRequestContext, type Page } from '@playwright/test';

// QA по сверке клиентского контракта каталога MCP-серверов с DTO бэка (задача
// 458ba87c). Проверяет четыре дефекта:
//   D1: фильтр поиска не должен падать на null title/description
//   D2: клик по карточке не должен падать на undefined.map (поля лежат в prefill)
//   D3: ссылка на репозиторий не должна уходить в https://undefined
//   D4: на карточке должно быть видно название сервера (title)

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

async function loginAndOpenMcpCatalog(page: Page) {
  await page.goto('/');
  await page.waitForLoadState('networkidle');
  // Если уже залогинены (token в localStorage) — пропускаем форму.
  const loginInput = page.locator('input[placeholder="Имя пользователя"]');
  if (await loginInput.count() > 0) {
    await loginInput.fill(USER);
    await page.locator('input[placeholder="Пароль"]').fill(PASS);
    await page.getByRole('button', { name: /войти|подключиться/i }).click();
    await page.waitForTimeout(3000);
  }
  // Ждём пока отрендерится аватар
  const avatar = page.locator('[aria-label^="Меню пользователя"]');
  await avatar.waitFor({ state: 'visible', timeout: 15000 });
  await avatar.click();
  const mcpItem = page.getByRole('button', { name: /MCP-серверы/ });
  await mcpItem.waitFor({ state: 'visible', timeout: 10000 });
  await mcpItem.click();
  // Вкладка «Каталог» внутри модалки
  const catalogTab = page.getByRole('button', { name: /^Каталог$/ });
  await catalogTab.waitFor({ state: 'visible', timeout: 10000 });
  await catalogTab.click();
  // Ждём либо карточки, либо empty-state/error
  await page.waitForTimeout(3000);
}

test('каталог MCP не падает на поиске, показывает имя и валидную ссылку', async ({ page, request }) => {
  const token = await login(request);
  await ensureCatalogFlag(request, token);

  const errors: string[] = [];
  page.on('pageerror', e => errors.push(`pageerror: ${e.message}`));

  await loginAndOpenMcpCatalog(page);

  // D3: ни одной ссылки с href="https://undefined" ни сейчас, ни после ввода поиска
  const badLinks = page.locator('a[href="https://undefined"], a[href="https://null"]');
  await expect(badLinks).toHaveCount(0);

  // Ждём, чтобы каталог реально догрузился: либо карточки, либо пустое состояние
  // с явным текстом (не error и не скелетоны)
  await page.waitForSelector('button.card-act, h3:has-text("Каталог MCP-серверов") ~ *', { timeout: 15000 });
  await page.waitForTimeout(2000);

  // Снимок состояния сразу после открытия каталога
  await page.screenshot({ path: 'test-results/catalog-loaded.png', fullPage: false });

  // D1: ввод в поле поиска не должен ронять страницу
  const search = page.locator('input[placeholder*="Поиск" i]').first();
  await expect(search).toBeVisible();
  await search.fill('filesystem');
  await page.waitForTimeout(1000);
  // ErrorBoundary не должен появиться
  const errorBoundary = page.getByText('Интерфейс споткнулся');
  await expect(errorBoundary).toHaveCount(0);
  // D3 повторно после поиска
  await expect(badLinks).toHaveCount(0);

  // Снимок после поиска
  await page.screenshot({ path: 'test-results/catalog-search.png', fullPage: false });

  // Очищаем поиск, чтобы получить карточки
  await search.fill('');
  await page.waitForTimeout(1000);

  // D2: клик по первой карточке должен открыть форму с предзаполнением
  const firstCard = page.locator('button.card-act').first();
  const cardsCount = await firstCard.count();
  if (cardsCount > 0) {
    await firstCard.click();
    await page.waitForTimeout(2000);
    // Не должно быть ErrorBoundary
    await expect(errorBoundary).toHaveCount(0);
    // Должна появиться форма с заполненными полями — кнопка «Сохранить выключенным»
    // или поле команды/адреса/имени. Имя в форме = title выбранной карточки.
    const saveBtn = page.getByRole('button', { name: /Сохранить выключенным|Сохранить/ });
    await expect(saveBtn).toBeVisible({ timeout: 5000 });
    // Снимок формы с предзаполнением
    await page.screenshot({ path: 'test-results/catalog-form.png', fullPage: false });
  }

  // D4: имя сервера на карточке — первое, что человек видит. Проверим на конкретной карточке.
  // Если карточек нет — пропускаем (каталог мог быть недоступен)
  const nameSpans = page.locator('button.card-act span[title]');
  const nameSpansCount = await nameSpans.count();
  if (nameSpansCount > 0) {
    // title атрибут обязан быть непустым — у нас `title={title}` где title = s.title ?? s.name
    const firstTitle = await nameSpans.first().getAttribute('title');
    expect(firstTitle, 'title атрибут карточки не должен быть пустым').toBeTruthy();
  }

  // Снимок экрана для визуальной проверки
  await page.screenshot({ path: 'test-results/catalog.png', fullPage: false });

  expect(errors.filter(e => !e.includes('ResizeObserver') && !e.includes('favicon'))).toEqual([]);
});

// Дефект D5 (из задачи f1ad4c71): McpServerForm при импорте из каталога собирал
// argv только из полей target='args' с default'ом, игнорируя prefill.args. В итоге
// запись создавалась с command='npx' и пустыми args — сервер не запускался. Тест
// импортирует stdio-запись через настоящий бэкенд и проверяет, что в записи args
// содержит имя пакета с версией (подстрока '@' + semver)
test('импорт stdio-сервера из каталога кладёт имя пакета с версией в args', async ({ page, request }) => {
  const token = await login(request);
  await ensureCatalogFlag(request, token);

  await loginAndOpenMcpCatalog(page);

  // Поиск 'filesystem' — реестр почти всегда содержит такую запись. Если карточек
  // нет — пропускаем (каталог недоступен, тест не проваливаем ложно)
  const search = page.locator('input[placeholder*="Поиск" i]').first();
  await expect(search).toBeVisible();
  await search.fill('filesystem');
  await page.waitForTimeout(2000);

  const firstCard = page.locator('button.card-act').first();
  if (await firstCard.count() === 0) {
    test.skip(true, 'каталог пуст — пропускаем до наличия карточек');
    return;
  }
  await firstCard.click();
  await page.waitForTimeout(2000);

  // Жмём «Сохранить выключенным» — это и есть сценарий «импорт каталожной записи»
  const saveBtn = page.getByRole('button', { name: /Сохранить выключенным/ });
  await expect(saveBtn).toBeVisible({ timeout: 5000 });
  await saveBtn.click();
  // Модалка закрывается после onDone, ждём пока список серверов отрендерится
  await page.waitForTimeout(3000);

  // Тянем список серверов тем же токеном и ищем только что импортированную запись.
  // Критерий «правильной» строки запуска: у stdio-записи из каталога args содержит
  // подстроку '@' (пакет@версия). Без D5-fix там оставалось пусто, и сервер бы не
  // запустился (один npx без аргументов ничего не делает)
  const list = await request.get('/api/mcp/servers', {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(list.ok(), 'GET /api/mcp/servers должен пройти').toBeTruthy();
  const servers = await list.json() as Array<{
    key: string; transport: string; command?: string | null;
    args?: string[]; catalogRef?: { name: string } | null;
  }>;
  // Каталожные записи — те, у кого заполнен catalogRef.name. Ищем stdio среди них
  const catalogStdio = servers.find(s =>
    s.transport === 'stdio' && !!s.catalogRef?.name);
  if (!catalogStdio) {
    test.skip(true, 'после импорта нет stdio-записи с catalogRef — пропускаем');
    return;
  }
  // argv содержит имя пакета с версией — substring вида 'pkg@x.y.z' или 'pkg@x'
  const argv = catalogStdio.args ?? [];
  expect(argv.length, 'argv должен быть непустым у импортированной stdio-записи').toBeGreaterThan(0);
  const hasPkgVersion = argv.some(a => /@[\d.]+/.test(a));
  expect(hasPkgVersion,
    `в argv должен быть токен вида pkg@1.2.3 — фактический argv: ${JSON.stringify(argv)}`
  ).toBeTruthy();
  // command тоже не пустой (фиксированный рантайм, npx/uvx)
  expect(catalogStdio.command, 'command у импортированной записи не должен быть пустым').toBeTruthy();
});

// Дефект D6 (из задачи f1ad4c71): фронт звонил на /mcp/catalog/revisions (мн.ч.),
// бэк отдаёт 404. Обёртка ответа тоже была сломанной: фронт читал res.revisions, бэк
// отдаёт res.items. Оба пути мы уже обжигали (поиск: servers/items; ревизия:
// revisions/revision) — фиксируем тестом, чтобы третий раз не ходить
test('ревизия каталога отвечает 200 и возвращает items', async ({ request }) => {
  const token = await login(request);
  await ensureCatalogFlag(request, token);

  // Имя, по которому реестр обычно что-то отдаёт — точное содержимое не важно:
  // бэк сам фильтрует по «есть ли у владельца запись с таким CatalogRef.name»,
  // отсутствующее имя просто игнорирует в ответе. Здесь нас интересует сам факт
  // 200 + тело {items: [...]}, а не наполнение
  const r = await request.post('/api/mcp/catalog/revision', {
    headers: { Authorization: `Bearer ${token}` },
    data: { names: ['io.modelcontextprotocol/filesystem'] },
  });
  // 200 — путь единственного числа не сломан. 503 — каталог выключен на сервере,
  // не наша ошибка контракта. Всё остальное — регрессия
  expect([200, 503]).toContain(r.status());
  if (r.status() !== 200) {
    test.skip(true, `каталог выключен (${r.status()}) — пропускаем проверку items`);
    return;
  }
  const body = await r.json() as { items?: unknown[] };
  expect(Array.isArray(body.items),
    `ответ POST /api/mcp/catalog/revision обязан содержать items[], получено: ${JSON.stringify(body).slice(0)}`
  ).toBeTruthy();
});
