import { test, expect, type APIRequestContext } from '@playwright/test';

// Регрессия: карточки механик в раскрывашке «Обсудить с командой» перехватывались
// textarea.cc-composer-input (панель без своего stacking context красилась ниже поля
// ввода — см. TeamDrawer.tsx). Кликаем БЕЗ force, чтобы Playwright делал обычный
// hit-test — если оверлей вернётся, клик проретраится 30с и упадёт по таймауту.

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

test.describe('раскрывашка «Обсудить с командой»', () => {
  let token: string;
  let chatId: string;
  // Второй чат — под кейс автопоказа (имя уникально, чтобы найти строку в списке чатов)
  let autoShowChatId: string;
  const autoShowChatName = `E2E team-drawer autodiscuss ${Date.now()}`;

  test.beforeAll(async ({ playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    token = await login(request);
    const r = await request.post('/api/chats', {
      data: { mode: 'auto', name: 'E2E team-drawer' },
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(r.ok(), 'создание чата должно пройти').toBeTruthy();
    chatId = (await r.json()).id as string;

    const r2 = await request.post('/api/chats', {
      data: { mode: 'auto', name: autoShowChatName },
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(r2.ok(), 'создание второго чата должно пройти').toBeTruthy();
    autoShowChatId = (await r2.json()).id as string;
    await request.dispose();
  });

  test.afterAll(async ({ playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    await request.delete(`/api/chats/${chatId}`, { headers: { Authorization: `Bearer ${token}` } });
    await request.delete(`/api/chats/${autoShowChatId}`, { headers: { Authorization: `Bearer ${token}` } });
    await request.dispose();
  });

  test('карточка механики выбирается обычным кликом мыши', async ({ page, context }) => {
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);

    await page.goto(`/#/chats/${chatId}`);
    const textarea = page.locator('textarea.cc-composer-input');
    await expect(textarea).toBeVisible();

    await page.locator('button[title="Обсудить с командой"]').click();
    const card = page.getByRole('button', { name: /Командная реализация/ });
    await expect(card).toBeVisible();

    // Обычный клик (без force) — падает с «intercepts pointer events», если панель
    // перехватывается соседним элементом композера
    await card.click();

    // Выбор применился: плейсхолдер поля сменился на плейсхолдер этой механики
    await expect(textarea).toHaveAttribute('placeholder', 'Что реализовать командой?…');
  });

  test('нижняя карточка секции «Сделать» выбирается обычным кликом', async ({ page, context }) => {
    // Регрессия (третий заход, Вера/QA 2026-08-03): предыдущий кейс кликал по «Командная
    // реализация» — средней карточке «Сделать», она проходила и до фикса. Баг был именно
    // в нижней карточке секции: область карточек имела свой жёсткий maxHeight (330px/38vh),
    // меньший, чем реально оставался бюджет высоты у корня раскрывашки (см. TeamDrawer.tsx) —
    // при переносе сетки групп на 2+ строки (обычная ширина композера ~900px: CHAT_MAX_W=950
    // не даёт использовать всю ширину окна даже на широком мониторе) нижняя карточка
    // обрезалась выше своего реального места, и клик по исходной (не проскроленной) позиции
    // проваливался мимо неё. При широком дефолтном вьюпорте Playwright (1280×720) сетка групп
    // умещается в одну строку и баг не проявляется — вьюпорт сужен явно, чтобы тест реально
    // ловил регресс.
    //
    // page.mouse ВМЕСТО locator.click(): Playwright перед обычным click() сам скроллит цель
    // в видимую область (scrollIntoViewIfNeeded) — это маскирует баг, тест был бы зелёным
    // независимо от фикса. Координаты берём ДО какого-либо скролла — так же, как курсор
    // реального пользователя, который целится в карточку по тому, что видит на экране.
    await page.setViewportSize({ width: 900, height: 820 });
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);

    await page.goto(`/#/chats/${chatId}`);
    const textarea = page.locator('textarea.cc-composer-input');
    await expect(textarea).toBeVisible();

    await page.locator('button[title="Обсудить с командой"]').click();
    const card = page.getByRole('button', { name: /Командный спринт/ });
    await expect(card).toBeVisible();

    const box = await card.boundingBox();
    expect(box, 'у карточки должен быть boundingBox').not.toBeNull();
    const x = box!.x + box!.width / 2;
    const y = box!.y + box!.height / 2;
    // Хит-тест ровно как у QA в баг-репорте: elementFromPoint в центре карточки должен
    // вернуть саму кнопку, а не что-то из соседней раскладки (композер, зону настроек и т.п.)
    const hitsCard = await page.evaluate(([x, y]) => {
      const el = document.elementFromPoint(x, y);
      const btn = el?.closest('button');
      return !!btn && (btn.textContent || '').includes('Командный спринт');
    }, [x, y] as const);
    expect(hitsCard, 'elementFromPoint в центре карточки должен возвращать саму кнопку').toBe(true);

    await page.mouse.move(x, y);
    await page.mouse.down();
    await page.mouse.up();

    await expect(textarea).toHaveAttribute('placeholder', 'Что сделать спринтом?…');
  });

  test('карточка механики выбирается обычным кликом при автопоказе панели', async ({ page, context }) => {
    // Регрессия (проверка на проде 2026-08-02, Вера): при автопоказе — переход в чат,
    // где sessionStorage 'cc_auto_discuss' совпадает с id (см. TeamCommandCenter
    // «Созвать команду» → Composer.tsx) — панель открывалась сама, и клик по карточке
    // перехватывался невидимым слоем. Кликаем БЕЗ force как в тесте выше.
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);

    await page.goto(`/#/chats/${chatId}`);
    const textarea = page.locator('textarea.cc-composer-input');
    await expect(textarea).toBeVisible();

    // Имитируем сигнал «Созвать команду»: флаг ставится ДО перехода в целевой чат
    await page.evaluate((id) => sessionStorage.setItem('cc_auto_discuss', id), autoShowChatId);

    // Переход в целевой чат обычным кликом по строке списка (SPA-навигация, без
    // reload) — так же, как после «Создать чат» в TeamCommandCenter
    await page.getByText(autoShowChatName, { exact: false }).first().click();

    const card = page.getByRole('button', { name: /Командная реализация/ });
    await expect(card).toBeVisible();

    // Панель открылась автоматически — обычный клик не должен перехватываться
    await card.click();

    const targetTextarea = page.locator('textarea.cc-composer-input');
    await expect(targetTextarea).toHaveAttribute('placeholder', 'Что реализовать командой?…');
  });
});
