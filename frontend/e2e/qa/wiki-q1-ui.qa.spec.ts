import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { login } from './helpers';

// QA · очередь 1 вики: интерфейс на стенде из worktree (:5562, копия data/).
// Дерево вики в «Заметках», страница вики с блоком «Связанные», плоские ссылки [[…]],
// мобильная раскладка. Скриншоты — в .cc-attachments/wiki-test-queue1/shots/.

const SHOTS = '/home/an/Sources/ClaudeCodeServer-wt/wiki-queue1/.cc-attachments/wiki-test-queue1/shots';
const EXPECTED_SUBGROUPS = ['Решение', 'Правило', 'Термин', 'Состояние', 'Хроника', 'Прочее', 'Мои заметки'];
const T = { timeout: 15000 };

let request: APIRequestContext;
let token: string;

test.describe.configure({ mode: 'serial' });
test.setTimeout(240_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});

test.afterAll(async () => { await request?.dispose(); });

async function openNotes(page: Page, context: any) {
  page.setDefaultTimeout(20000);
  await context.addInitScript((tk: string) => localStorage.setItem('cc_token', tk), token);
  await page.goto('/#/notes');
  await page.waitForTimeout(5000);
  await expect(page.locator('[data-testid="wiki-unified-tree"]')).toBeVisible({ timeout: 30000 });
}

test('дерево вики: подгруппы, счётчики, свёртка помнится, фильтр и поиск сквозь подгруппы', async ({ page, context }) => {
  await openNotes(page, context);
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 160)); });
  const bad: string[] = [];
  page.on('response', r => { if (r.status() >= 400) bad.push(`${r.status()} ${r.url()}`); });

  const subs = page.locator('[data-testid="unified-subgroup"]');
  const labels = await subs.allInnerTexts();
  const distinct = [...new Set(labels.map(t => t.replace(/\s*\d+\s*$/, '').trim()))];
  test.info().annotations.push({ type: 'note', description: `подгрупп: ${JSON.stringify(labels.slice(0, 40))}` });
  test.info().annotations.push({ type: 'note', description: `разные подгруппы: ${JSON.stringify(distinct)}` });
  const unexpected = distinct.filter(d => !EXPECTED_SUBGROUPS.includes(d));
  expect(unexpected, 'в дереве нет посторонних подгрупп').toEqual([]);
  const missing = EXPECTED_SUBGROUPS.filter(d => !distinct.includes(d));
  test.info().annotations.push({ type: 'note', description: `скрытые (пустые) подгруппы: ${JSON.stringify(missing)}` });

  const withCount = labels.filter(t => /\d/.test(t)).length;
  test.info().annotations.push({ type: 'note', description: `подгрупп со счётчиком: ${withCount} из ${labels.length}` });
  expect(withCount, 'у подгрупп есть счётчики').toBe(labels.length);

  const zero = labels.filter(t => /\b0$/.test(t.trim()));
  test.info().annotations.push({ type: 'note', description: `подгрупп со счётчиком 0: ${JSON.stringify(zero)}` });
  expect(zero, 'пустые подгруппы не рисуются').toEqual([]);

  // «Мои заметки» в «Другие заметки» и по разделам
  const otherGroup = page.locator('[data-testid="unified-group"]', { hasText: 'Другие заметки' });
  const otherCount = await otherGroup.count();
  const otherSub = otherGroup.locator('[data-testid="unified-subgroup"]', { hasText: 'Мои заметки' });
  const otherSubText = (await otherSub.count()) ? (await otherSub.first().innerText()).trim() : null;
  const perSection = await page.locator('[data-testid="unified-subgroup"][data-type="note"]').allInnerTexts();
  test.info().annotations.push({ type: 'note', description: `узлов «Другие заметки»: ${otherCount}; «Мои заметки» в нём: ${otherSubText}; «Мои заметки» по разделам: ${JSON.stringify(perSection)}` });

  // свёртка запоминается (перезагрузка)
  const before = await subs.first().getAttribute('aria-expanded');
  await subs.first().click(T);
  await page.waitForTimeout(500);
  const after = await subs.first().getAttribute('aria-expanded');
  await page.reload();
  await page.waitForTimeout(8000);
  const treeAfter = await page.locator('[data-testid="wiki-unified-tree"]').count();
  const subsAfter = await page.locator('[data-testid="unified-subgroup"]').count();
  const bodyAfter = (await page.locator('body').innerText()).slice(0, 300);
  test.info().annotations.push({ type: 'note', description: `после перезагрузки: дерево=${treeAfter}, подгрупп=${subsAfter}; текст: ${bodyAfter}` });
  if (!subsAfter) {
    // перезагрузка увела на другую панель — открываем «Заметки» из навигации
    const nav = page.locator('button', { hasText: 'Заметки' }).first();
    if (await nav.count()) { await nav.click(T); await page.waitForTimeout(7000); }
  }
  const subsReload = await page.locator('[data-testid="unified-subgroup"]').count();
  test.info().annotations.push({ type: 'note', description: `подгрупп после перезагрузки (с панелью «Заметки»): ${subsReload}` });
  const afterReload = subsReload ? await page.locator('[data-testid="unified-subgroup"]').first().getAttribute('aria-expanded') : null;
  test.info().annotations.push({ type: 'note', description: `свёртка: было ${before} → стало ${after} → после перезагрузки ${afterReload}` });
  expect(before, 'подгруппа была раскрыта').toBe('true');
  expect(after, 'подгруппа свернулась').toBe('false');
  expect(afterReload, 'свёртка пережила перезагрузку').toBe('false');
  if (afterReload === null) {
    // дерево не отрисовалось после перезагрузки — проверяем в новой вкладке той же сессии
    const page2 = await context.newPage();
    await page2.goto('/#/notes');
    await page2.waitForTimeout(9000);
    const subs2 = await page2.locator('[data-testid="unified-subgroup"]').count();
    test.info().annotations.push({ type: 'note', description: `в новой вкладке подгрупп: ${subs2}; текст: ${(await page2.locator('body').innerText()).slice(0, 300)}` });
    if (subs2) {
      const exp2 = await page2.locator('[data-testid="unified-subgroup"]').first().getAttribute('aria-expanded');
      test.info().annotations.push({ type: 'note', description: `в новой вкладке первая подгруппа: aria-expanded=${exp2}` });
    }
    await page2.close();
  }
  await page.locator('[data-testid="unified-subgroup"]').first().click(T);
  await page.waitForTimeout(300);

  // фильтр по автору
  const filter = page.locator('[data-testid="unified-filter"]');
  const chips = await filter.locator('button').allInnerTexts();
  test.info().annotations.push({ type: 'note', description: `фильтр «Кто написал»: ${JSON.stringify(chips)}` });
  const aiChip = filter.locator('button', { hasText: 'ИИ' }).first();
  expect(await aiChip.count(), 'в фильтре есть чип «ИИ»').toBeGreaterThan(0);
  await aiChip.click(T);
  await page.waitForTimeout(1500);
  const origins = await page.locator('[data-testid="unified-row"]').evaluateAll(
    els => els.map(e => e.getAttribute('data-origin')));
  test.info().annotations.push({ type: 'note', description: `после фильтра «ИИ» строк: ${origins.length}, авторство: ${JSON.stringify([...new Set(origins)])}` });
  expect(origins.length, 'строки после фильтра есть').toBeGreaterThan(0);
  expect([...new Set(origins)].filter(o => o !== 'ai'), 'после фильтра «ИИ» остались только ИИ-строки').toEqual([]);
  const allChip = filter.locator('button', { hasText: 'Все' }).first();
  if (await allChip.count()) { await allChip.click(T); await page.waitForTimeout(800); }

  // поиск сквозь подгруппы
  const search = page.getByPlaceholder('Поиск…').first();
  test.info().annotations.push({ type: 'note', description: `полей «Поиск…»: ${await page.getByPlaceholder('Поиск…').count()}` });
  if (await search.count()) {
    await search.fill('Strava', T);
    await page.waitForTimeout(2500);
    const rows = await page.locator('[data-testid="unified-row"]').allInnerTexts();
    const openSubs = await page.locator('[data-testid="unified-subgroup"][aria-expanded="true"]').allInnerTexts();
    test.info().annotations.push({ type: 'note', description: `поиск «Страва»: строк ${rows.length}, раскрытых подгрупп ${openSubs.length} ${JSON.stringify(openSubs.slice(0, 10))}` });
    test.info().annotations.push({ type: 'note', description: `найденные строки: ${JSON.stringify(rows.slice(0, 10))}` });
    await search.fill('', T);
    await page.waitForTimeout(800);
  }

  await page.screenshot({ path: `${SHOTS}/wiki-tree-1280.png` });
  test.info().annotations.push({ type: 'note', description: `ошибки консоли: ${errs.join(' | ')}` });
  test.info().annotations.push({ type: 'note', description: `ответы 4xx/5xx: ${[...new Set(bad)].join(' | ')}` });
});
