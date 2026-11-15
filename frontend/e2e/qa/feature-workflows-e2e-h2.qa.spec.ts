import { test, expect } from '@playwright/test';
import { login, api, type Api } from './helpers';

// С10v2: наследование в интерфейсе персоны — унаследованные права и модели показаны
// как чужие, «Своё» делает их своими, смена специальности спрашивает про свои секции.

const NAME = 'qa-e2e-наследник2';
let client: Api;
let token: string;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

test('С10v2: наследование в форме персоны', async ({ context }) => {
  test.setTimeout(120_000);
  for (const old of (await client.get<any[]>('/api/personas')).filter(x => x.name === NAME)) await client.del(`/api/personas/${old.id}`);
  // Наследование включает флаг inheritsAccess (его шлёт форма); без него права копируются при создании
  const p = await client.post<any>('/api/personas', { name: NAME, specialty: 'analyst', inheritsAccess: true });
  const id = p.id ?? p.persona?.id;
  const page = await context.newPage();
  try {
    const cur = await client.get<any>(`/api/personas/${id}`);
    expect(cur.inheritsAccess, 'персона создана наследующей права').toBe(true);

    page.setViewportSize({ width: 1280, height: 900 });
    const errs: string[] = [];
    page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await page.goto(`/#/personas/${id}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Редактировать' }).first().click({ timeout: 15000 });

    // Форма правки: унаследованные права показаны как чужие, «Своё» предлагает их присвоить
    const specSel = page.getByLabel('Специальность');
    await expect(specSel, 'форма правки открылась, видно поле «Специальность»').toBeVisible({ timeout: 15000 });
    await expect(page.getByText(/пока права наследуются/i).first(), 'подсказка про наследование').toBeVisible({ timeout: 10000 });
    await expect(page.getByText('Своё', { exact: true }).first(), 'кнопка «Своё»').toBeVisible();
    await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/С10v2-inherit.png', fullPage: true });

    // «Умения» — вкладка просмотра карточки, в форме правки её нет (см. PersonaToolbar): не ждём её здесь
    const opts = await specSel.locator('option').allInnerTexts();
    console.log('С10v2 опций специальности:', JSON.stringify(opts.slice(0, 6)));
    const pick = opts.find(o => /Планировщик/i.test(o));
    expect(pick, 'в списке есть «Планировщик»').toBeTruthy();
    await specSel.selectOption({ label: pick! });
    // Смена специальности у наследующей персоны: диалог «Права и модели наследуются…»
    await expect(page.getByText(/Права и модели наследуются/).first(), 'диалог смены специальности').toBeVisible({ timeout: 10000 });
    await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/С10v2-change.png', fullPage: true });
    console.log('С10v2 errors:', JSON.stringify([...new Set(errs)]));
  } finally {
    await page.close();
    await client.del(`/api/personas/${id}`);
  }
});
