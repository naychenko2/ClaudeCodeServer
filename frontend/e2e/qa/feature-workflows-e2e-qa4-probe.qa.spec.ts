import { test } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { login, api, type Api } from './helpers';

// Пробник QA-4 (задача 55314d7f): знак переноса на живом узле. Открывает фичу с
// однословным именем, скроллит ленту к узлу, сохраняет скриншот заголовка и мерит чернила.
// Одноразовый diagnostic: утверждений о продукте не делает.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const WORD = 'Производственная';
const LONG = 'Очень длинное название этапа процесса';

let client: Api;
let request: import('@playwright/test').APIRequestContext;
let token: string;
const tplIds: string[] = [];
const epicIds: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  for (const id of tplIds) await client.del(`/api/workflow-templates/${id}`).catch(() => undefined);
  for (const id of epicIds) await client.del(`/api/projects/${PID}/tasks/${id}`).catch(() => undefined);
  await request?.dispose();
});

test('P1: скриншоты заголовков узлов с длинными именами @360', async ({ browser }) => {
  test.setTimeout(120_000);
  const tpl = await client.post<any>('/api/workflow-templates', {
    name: 'qa-e2e-qa4-probe', stages: [{ role: 'qa', presence: 'on', title: LONG }, { role: 'review', presence: 'on', title: WORD }],
  });
  const tplId = tpl?.id ?? tpl?.template?.id ?? null;
  if (tplId) tplIds.push(tplId);
  const epic = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa4-probe-feature', role: 'feature', assignee: 'me', description: 'Пробник знака переноса.',
  });
  epicIds.push(epic.id);
  await client.post(`/api/tasks/${epic.id}/workflow/confirm`, { templateId: tplId, scope: 'global' });

  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  page.setViewportSize({ width: 360, height: 800 });
  await ctx.addInitScript(([tk]: string[]) => { localStorage.setItem('cc_token', tk); localStorage.setItem('theme-mode', 'light'); }, [token]);
  await page.goto(`/#/project/${PID}/feature/${epic.id}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);

  for (const [tag, text] of [['long', LONG], ['word', WORD]] as const) {
    const loc = page.locator('[data-testid^="pgraph-title-"]').filter({ hasText: text }).first();
    await loc.scrollIntoViewIfNeeded({ timeout: 15000 }).catch(() => undefined);
    await page.waitForTimeout(800);
    const geo = await loc.evaluate((el: Element) => {
      const e = el as HTMLElement;
      const box = e.getBoundingClientRect();
      const tn = [...e.childNodes].find((n) => n.nodeType === 3) as Text | undefined;
      if (!tn) return null;
      const rg = document.createRange();
      rg.selectNodeContents(tn);
      const rects = [...rg.getClientRects()].filter((r) => r.width > 0 && r.height > 0);
      if (!rects.length) return null;
      const top = rects[0].top;
      const line = rects.filter((r) => Math.abs(r.top - top) < 2);
      return { box: { x: box.x, y: box.y, w: box.width, h: box.height }, lineLeft: Math.min(...line.map((r) => r.left)), lineRight: Math.max(...line.map((r) => r.right)), lineTop: top, text: tn.data.slice(0, 20) };
    }).catch(() => null);
    console.log(`P1 ${tag} geo:`, JSON.stringify(geo));
    if (!geo) continue;
    const buf = await loc.screenshot().catch(() => null);
    if (buf) {
      const path = `${SHOTS}/qa4-probe-title-${tag}.png`;
      writeFileSync(path, buf);
      console.log(`P1 ${tag} saved`, path, buf.length);
    }
  }
  await page.close();
  await ctx.close();
});
