import { test } from '@playwright/test';
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

const flat = (s: string) => s.replace(/\n/g, ' | ');

test('С10v2: наследование в форме персоны', async ({ context }) => {
  test.setTimeout(280_000);
  const personas = await client.get<any[]>('/api/personas');
  let p = personas.find(x => x.name === NAME);
  if (!p) p = await client.post<any>('/api/personas', { name: NAME, specialty: 'analyst' });
  const id = p.id ?? p.persona?.id;
  const cur = await client.get<any>(`/api/personas/${id}`);
  console.log('С10v2 persona:', id, 'InheritsAccess:', cur?.inheritsAccess, 'свои слоты:', JSON.stringify({ t: cur?.tierStrong, a: cur?.access }));

  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/personas/${id}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3500);
  await page.getByRole('button', { name: 'Редактировать' }).first().click({ timeout: 15000 }).catch(e => console.log('С10v2 редактировать:', String(e).slice(0, 100)));
  await page.waitForTimeout(2500);

  const body1 = await page.locator('body').innerText();
  console.log('С10v2 маркеры наследования: «пока права наследуются»:', /пока права наследуются/i.test(flat(body1)), '| «Своё»:', /Своё/.test(flat(body1)), '| «как у специальности»:', /как у специальности/i.test(flat(body1)));
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/С10v2-inherit.png', fullPage: true });

  // Сменить специальность в форме — ждём подтверждение про свои секции
  await page.getByText('Умения', { exact: true }).first().click().catch(() => undefined);
  await page.waitForTimeout(1500);
  const specSel = page.locator('select').first();
  const opts = await specSel.locator('option').allInnerTexts().catch(() => []);
  console.log('С10v2 опций специальности:', JSON.stringify(opts.slice(0, 6)));
  const pick = opts.find(o => /Планировщик/i.test(o));
  if (pick) {
    await specSel.selectOption({ label: pick });
    await page.waitForTimeout(2000);
    const b2 = await page.locator('body').innerText();
    const dlg = /наследуются|свои умения|свои секции/i.test(flat(b2));
    console.log('С10v2 диалог смены специальности:', dlg, flat(b2).match(/[^|]*(наследуются|свои секции)[^|]*/gi)?.slice(0, 3));
    await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/С10v2-change.png', fullPage: true });
  }
  console.log('С10v2 errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
  await client.del(`/api/personas/${id}`);
});
