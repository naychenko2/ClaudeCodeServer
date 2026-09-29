import { test, expect, type APIRequestContext, type Page } from '@playwright/test';

// Высота поля композера при смене режима «Картинка» ↔ «Чат». У режима «Картинка» minHeight
// 78, и авторазмер пустого поля пишет inline-высоту 78px. Смена режима с одинаковыми
// буферами (оба пустые — промпт отправлен или стёрт) не меняет value, и «Чат» оставался
// растянутым на три строки. Проверяем отправку промпта с уходом в «Чат», ручное
// переключение и программное: выбор картинки снят — режим пропадает, поле само
// возвращается в «Чат». Черновик заводится ручкой нитей — как агентский image_new.
// Стенд — на временной data (личный чат: нужен override домашней папки admin).
//
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5099 npx playwright test e2e/composer-mode-autosize.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const LONG = Array.from({ length: 12 }, (_, i) => `Строка промпта номер ${i + 1}: акварель, мягкий свет, рыжий кот на подоконнике`).join('\n');
// Одна строка поля «Чат» — 34px; растянутое на три строки — 78px
const ONE_LINE_MAX = 40;
const MODEL = 'fal-ai/flux/dev';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

const fieldHeight = (page: Page) =>
  page.locator('textarea.cc-composer-input').evaluate(el => el.getBoundingClientRect().height);

// ✕ у выбранной картинки: в свёрнутой полосе — кнопка, в развёрнутой — крестик чипа
async function releaseImage(page: Page) {
  const mini = page.getByRole('button', { name: 'Снять выбор картинки' });
  if (await mini.count()) return mini.first().click();
  await page.locator('[title*="✕ — снять выбор"] > :last-child').first().click();
}

test('поле возвращается к одной строке при смене режима с пустыми буферами', async ({ page, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const headers = { Authorization: `Bearer ${token}` };
  await request.put('/api/feature-flags/image-editor', { headers, data: { enabled: true } });
  const chat = await request.post('/api/chats', { headers, data: { mode: 'auto', name: `E2E composer autosize ${Date.now()}` } });
  expect(chat.ok(), 'чат должен создаться').toBeTruthy();
  const sid = (await chat.json()).id as string;
  const threads = `/api/image-editor/chats/${sid}/threads`;
  const revision = async () => ((await (await request.get(threads, { headers })).json()) as { revision: number }).revision;
  const draft = async () => {
    const r = await request.post(threads, { headers, data: { draftFolder: '', revision: await revision() } });
    expect(r.ok(), `черновик должен завестись: ${r.status()} ${await r.text()}`).toBeTruthy();
  };

  // Поставщик — мок: на тестовой data рисовать нечем; задача живёт вечно в «running»
  const jobs: string[] = [];
  await page.route('**/image-editor/**/catalog', r => r.fulfill({ json: {
    default: { provider: 'fal', model: 'auto' },
    providers: [{ key: 'fal', label: 'fal.ai', priceUnit: 'usd', models: [
      { id: 'auto', label: 'Авто' },
      { id: MODEL, label: 'FLUX dev', caps: { ops: ['generate'], mask: 'none', maxReferences: 4, maxCount: 4, faceByReferences: false }, priceHint: { amount: 0.03, unit: 'usd', per: 'image' } },
    ] }],
    limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null,
  } }));
  await page.route('**/image-editor/**/quote', r => r.fulfill({ json: {
    quoteId: 'q-1', provider: 'fal', model: MODEL, estimate: { amount: 0.06, unit: 'usd', approx: true, source: 'catalog' },
    expiresAt: new Date(Date.now() + 600_000).toISOString(), expectedSeconds: 20,
  } }));
  await page.route(/\/image-editor\/.*\/jobs$/, async r => {
    if (r.request().method() !== 'POST') return r.fallback();
    jobs.push(r.request().url());
    await r.fulfill({ status: 202, json: { jobId: 'job-autosize' } });
  });

  try {
    await page.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await page.goto(`/#/chats/${sid}`);
    const field = page.locator('textarea.cc-composer-input');
    await expect(field).toBeVisible();

    // Обычный разговор с длинным текстом: поле растёт и сжимается обратно
    await field.fill(LONG);
    await expect.poll(() => fieldHeight(page)).toBeGreaterThan(150);
    await field.fill('');
    await expect.poll(() => fieldHeight(page)).toBeLessThan(ONE_LINE_MAX);

    // Агент заводит черновик посреди разговора: переключатель есть, режим остаётся «Чат».
    // Перезагрузка — страница подхватывает нити с сервера, не завися от доставки
    // image_thread_changed; текст поля при этом не нужен, он проверен выше
    await draft();
    await page.reload();
    const imageBtn = page.getByRole('button', { name: 'Режим «Картинка»' });
    await expect(imageBtn).toBeVisible();
    expect(await fieldHeight(page)).toBeLessThan(ONE_LINE_MAX);

    // 0. Жалоба Андрея: длинный промпт → «Сгенерировать» (поле очищается, затравка по той
    // же нити не возвращает текст) → «Чат»
    await imageBtn.click();
    await field.fill(LONG);
    await expect.poll(() => fieldHeight(page)).toBeGreaterThan(150);
    await page.getByRole('button', { name: /Сгенерировать/ }).click();
    await expect.poll(() => jobs.length, { message: 'запуск должен уйти' }).toBe(1);
    await expect(field).toHaveValue('');
    await page.getByRole('button', { name: 'Режим «Чат»' }).click();
    await expect.poll(() => fieldHeight(page), { message: 'уход в «Чат» после генерации' }).toBeLessThan(ONE_LINE_MAX);

    // 1. Ручное: длинный промпт → стёрт → «Чат»
    await imageBtn.click();
    await field.fill(LONG);
    await expect.poll(() => fieldHeight(page)).toBeGreaterThan(150);
    await field.fill('');
    await expect.poll(() => fieldHeight(page)).toBeLessThan(100);
    await page.getByRole('button', { name: 'Режим «Чат»' }).click();
    await expect.poll(() => fieldHeight(page), { message: 'ручной уход в «Чат» после пустого промпта' }).toBeLessThan(ONE_LINE_MAX);

    // 2. Программное: выбор картинки снят (✕ на чипе) — режим пропадает, и поле само
    // уходит в «Чат» без клика по переключателю
    await imageBtn.click();
    await field.fill(LONG);
    await field.fill('');
    await expect.poll(() => fieldHeight(page)).toBeGreaterThan(ONE_LINE_MAX);
    await releaseImage(page);
    await expect(imageBtn).toHaveCount(0);
    await expect.poll(() => fieldHeight(page), { message: 'самовозврат в «Чат» после пустого промпта' }).toBeLessThan(ONE_LINE_MAX);

    // 3. Прежний сценарий: длинный промпт прямо в «Чат» — поле тоже сжимается
    await draft();
    await page.reload();
    await expect(imageBtn).toBeVisible();
    await imageBtn.click();
    await field.fill(LONG);
    await expect.poll(() => fieldHeight(page)).toBeGreaterThan(150);
    await page.getByRole('button', { name: 'Режим «Чат»' }).click();
    await expect.poll(() => fieldHeight(page), { message: 'уход в «Чат» с длинным промптом' }).toBeLessThan(ONE_LINE_MAX);
  } finally {
    await request.delete(`/api/chats/${sid}`, { headers });
    await request.dispose();
  }
});
