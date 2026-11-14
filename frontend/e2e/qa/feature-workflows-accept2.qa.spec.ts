import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import { login } from './helpers';

// Приёмка фичи «Процессы фичи», часть 2: редактор шаблонов процессов и форма персоны
// (каркас, группа «Права», диалоги наследования, «Модели», вкладки «Промпт» и «Умения»).

const SHOTS = '.cc-attachments/qa-wf-accept';
const IDS = JSON.parse(readFileSync('/home/an/Sources/ClaudeCodeServer-feature-workflows/.cc-attachments/qa-wf-accept/ids.json', 'utf8')) as {
  project: string; persona: string; personaNoSpecialty: string;
};

let token: string;
const errors: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  token = await login(req);
  await req.dispose();
  mkdirSync(SHOTS, { recursive: true });
});

async function open(context: BrowserContext, path: string, w = 1280): Promise<Page> {
  const page = await context.newPage();
  await page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
  page.on('console', m => { if (m.type() === 'error') errors.push(`[error] ${path} ${m.text()}`.slice(0, 300)); });
  page.on('pageerror', e => errors.push(`[pageerror] ${path} ${e}`.slice(0, 300)));
  await context.addInitScript((tk: string) => { localStorage.setItem('cc_token', tk); }, token);
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2500);
  return page;
}

const overflow = (page: Page) => page.evaluate(() => ({
  sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth,
}));

async function openEditor(page: Page) {
  await page.goto('#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);
  const switcher = page.getByRole('button', { name: 'Процессы фичи' });
  if (await switcher.count() > 0) await switcher.click();
  await page.waitForTimeout(1500);
}

test('Т1: редактор шаблонов — список, «+ Новый», черновик «не сохранён», 8 этапов', async ({ context }) => {
  const page = await open(context, '#/specialties');
  await openEditor(page);
  const body = await page.locator('body').innerText();
  const lower = body.toLowerCase();
  console.log('Т1 разделы списка:', JSON.stringify([
    lower.includes('глобальные · правит админ'),
    lower.includes('правит владелец'),
    lower.includes('процессы'),
  ]));
  expect.soft(lower.includes('глобальные · правит админ'), 'секция глобальных шаблонов есть').toBeTruthy();
  expect.soft(lower.includes('8 этапов'), 'в списке есть шаблоны с 8 этапами').toBeTruthy();

  const newBtn = page.getByTestId('new-template');
  expect.soft(await newBtn.count(), 'кнопка «+ Новый» есть').toBeGreaterThan(0);
  await newBtn.click();
  await page.waitForTimeout(1200);
  const text = await page.locator('body').innerText();
  console.log('Т1 черновик: «не сохранён» =', text.includes('не сохранён'), '| «Новый шаблон» =', text.includes('Новый шаблон'));
  expect.soft(text.includes('не сохранён'), 'черновик помечен «не сохранён»').toBeTruthy();

  // этапы нового шаблона
  const rows = page.locator('[data-testid^="wf-stage-"], [data-testid^="stage-row-"]');
  console.log('Т1 строк этапов:', await rows.count());
  const bodyAfter = await page.locator('body').innerText();
  console.log('Т1 фрагмент вокруг этапов:', JSON.stringify(bodyAfter.slice(0, 600)));
  const o = await overflow(page);
  console.log('Т1 переполнение:', JSON.stringify(o));
  expect.soft(o.sw, 'нет горизонтального переполнения').toBeLessThanOrEqual(o.cw + 1);
  await page.screenshot({ path: `${SHOTS}/t1-editor-1280.png`, fullPage: true });
  await page.close();
});

test('Т2: настройка этапа — уровень, ячейки моделей, порог молчания, MCP', async ({ context }) => {
  const page = await open(context, '#/specialties');
  await openEditor(page);
  const newBtn = page.getByTestId('new-template');
  if (await newBtn.count() > 0) { await newBtn.click(); await page.waitForTimeout(1200); }
  const row = page.getByRole('button', { name: /^Этап «/ });
  console.log('Т2 строк этапов в ленте:', await row.count());
  expect.soft(await row.count(), 'в ленте есть строки этапов').toBeGreaterThan(0);
  if (await row.count() > 0) {
    await row.first().click({ timeout: 15000 });
    await page.waitForTimeout(1500);
  }
  const dialog = page.getByLabel(/Настройка этапа/);
  console.log('Т2 шторка настройки:', await dialog.count());
  expect.soft(await dialog.count(), 'шторка «Настройка этапа» открылась').toBeGreaterThan(0);
  const text = await page.locator('body').innerText();
  const low = text.toLowerCase();
  for (const w of ['уровень по умолчанию', 'strong', 'medium', 'weak', 'порог молчания', 'mcp', 'что делать', 'после этапов', 'линия', 'исполнитель']) {
    console.log(`Т2 «${w}» в настройке этапа:`, low.includes(w));
    expect.soft(low.includes(w), `в настройке этапа есть «${w}»`).toBeTruthy();
  }
  console.log('Т2 текст шторки (начало):', JSON.stringify((await dialog.first().innerText({ timeout: 5000 }).catch(() => '')).slice(0, 400)));
  await page.screenshot({ path: `${SHOTS}/t2-stage-settings-1280.png`, fullPage: true });
  await page.close();
});

test('Т6: граф редактора — «⇉ параллельно», клик по узлу открывает настройку', async ({ context }) => {
  const page = await open(context, '#/specialties');
  await openEditor(page);
  // открыть встроенный «Параллельный цикл» — в нём есть параллельные ветки
  const parallel = page.getByText('Параллельный цикл', { exact: true });
  console.log('Т6 в списке «Параллельный цикл»:', await parallel.count());
  if (await parallel.count() > 0) { await parallel.first().click(); await page.waitForTimeout(1500); }
  const frames = await page.locator('[data-parallel-frame="true"]').count();
  const parallelLabel = await page.getByText('⇉ параллельно').count();
  console.log('Т6 рамок параллельных:', frames, '| подпись «⇉ параллельно»:', parallelLabel);
  expect.soft(frames, 'есть рамка параллельной колонки').toBeGreaterThan(0);
  expect.soft(parallelLabel, 'есть подпись «⇉ параллельно»').toBeGreaterThan(0);
  const nodes = page.locator('[data-node]');
  console.log('Т6 узлов в графе редактора:', await nodes.count());
  expect.soft(await nodes.count(), 'в графе есть узлы').toBeGreaterThan(0);
  if (await nodes.count() > 0) {
    const pressedBefore = await nodes.first().getAttribute('aria-pressed');
    await nodes.first().click();
    await page.waitForTimeout(1200);
    const dialog = page.getByLabel(/Настройка этапа/);
    console.log('Т6 клик по узлу → шторка настройки:', await dialog.count(), '| aria-pressed до клика:', pressedBefore);
    expect.soft(await dialog.count(), 'клик по узлу открывает настройку этапа').toBeGreaterThan(0);
  }
  await page.screenshot({ path: `${SHOTS}/t6-editor-graph-1280.png`, fullPage: true });
  await page.close();
});

test('Т3: форма персоны — каркас, «Права», число отличий, «Модели», вкладка «Промпт»', async ({ context }) => {
  const page = await open(context, `#/personas/${IDS.persona}`);
  const edit = page.getByTitle('Редактировать');
  console.log('Т3 кнопка «Редактировать»:', await edit.count());
  if (await edit.count() > 0) { await edit.first().click(); await page.waitForTimeout(1500); }

  console.log('Т3 access-group:', await page.getByTestId('access-group').count());
  console.log('Т3 models-group:', await page.getByTestId('models-group').count());
  console.log('Т3 футер формы:', await page.getByTestId('persona-form-footer').count());
  expect.soft(await page.getByTestId('access-group').count(), 'группа «Права» есть').toBeGreaterThan(0);
  expect.soft(await page.getByTestId('models-group').count(), 'группа «Модели» есть').toBeGreaterThan(0);

  const copied = page.getByTestId('access-copied');
  const copiedText = await copied.innerText().catch(() => '');
  console.log('Т3 строка копии:', JSON.stringify(copiedText));
  const m = copiedText.match(/отличается в (\d+)/);
  const stated = m ? Number(m[1]) : null;
  const marks = await page.getByTestId('access-group').locator('text=≠').count();
  console.log('Т3 заявлено отличий:', stated, '| видимых меток «≠»:', marks);
  expect.soft(stated, 'строка «отличается в N местах» есть').not.toBeNull();
  expect.soft(marks, 'число меток «≠» совпадает с N').toBe(stated);

  // вкладки формы — это PillSwitch, а не role=tab
  const tabs = await page.getByRole('tab').allInnerTexts();
  console.log('Т3 role=tab в форме:', JSON.stringify(tabs));
  const promptTab = page.getByText('Промпт', { exact: true });
  console.log('Т3 переключатель «Промпт»:', await promptTab.count());
  if (await promptTab.count() > 0) {
    await promptTab.first().click();
    await page.waitForTimeout(1500);
    console.log('Т3 prompt-chain:', await page.getByTestId('prompt-chain').count());
    const sections = await page.locator('[data-testid^="prompt-section-"]').count();
    console.log('Т3 секций промпта:', sections);
    const hint = await page.locator('text=Длиннее 1024 символов').count();
    console.log('Т3 подсказка о лимите 1024:', hint);
    const promptText = await page.getByTestId('prompt-chain').innerText({ timeout: 5000 }).catch(() => '');
    console.log('Т3 цепочка слоёв (начало):', JSON.stringify(promptText.slice(0, 300)));
    await page.screenshot({ path: `${SHOTS}/t3-prompt-1280.png`, fullPage: true });
  }
  const o = await overflow(page);
  console.log('Т3 переполнение:', JSON.stringify(o));
  expect.soft(o.sw, 'нет горизонтального переполнения в форме').toBeLessThanOrEqual(o.cw + 1);
  await page.close();
});

test('Т4: диалоги — возврат прав как у специальности и смена специальности', async ({ context }) => {
  const page = await open(context, `#/personas/${IDS.persona}`);
  const edit = page.getByTitle('Редактировать');
  if (await edit.count() > 0) { await edit.first().click(); await page.waitForTimeout(1500); }
  const group = page.getByTestId('access-group');
  // переключатель «Как у специальности»
  const inherit = group.getByText('Как у специальности', { exact: true });
  console.log('Т4 переключатель «Как у специальности»:', await inherit.count());
  if (await inherit.count() > 0) {
    await inherit.first().click();
    await page.waitForTimeout(1000);
    const body = await page.locator('body').innerText();
    const dialog = body.includes('Вернуть права как у специальности?');
    console.log('Т4 диалог возврата прав:', dialog);
    console.log('Т4 хвост экрана:', JSON.stringify(body.slice(-400)));
    expect.soft(dialog, 'диалог «Вернуть права как у специальности?» показан').toBeTruthy();
    await page.screenshot({ path: `${SHOTS}/t4-inherit-dialog-1280.png`, fullPage: true });
  }
  await page.close();
});

test('Т5: мобильные экраны 360 — редактор шаблонов и форма персоны', async ({ context }) => {
  const page = await open(context, '#/specialties', 360);
  await openEditor(page);
  const o1 = await overflow(page);
  console.log('Т5 переполнение редактора 360:', JSON.stringify(o1));
  expect.soft(o1.sw, 'редактор без переполнения на 360').toBeLessThanOrEqual(o1.cw + 1);
  await page.screenshot({ path: `${SHOTS}/t5-editor-360.png`, fullPage: true });
  await page.close();

  const p2 = await open(context, `#/personas/${IDS.persona}`, 360);
  const edit = p2.getByTitle('Редактировать');
  if (await edit.count() > 0) { await edit.first().click(); await p2.waitForTimeout(1500); }
  const o2 = await overflow(p2);
  console.log('Т5 переполнение формы 360:', JSON.stringify(o2));
  expect.soft(o2.sw, 'форма без переполнения на 360').toBeLessThanOrEqual(o2.cw + 1);
  const footer = p2.getByTestId('persona-form-footer');
  if (await footer.count() > 0) {
    const sizes = await footer.locator('button').evaluateAll(els => els.map(e => {
      const r = (e as HTMLElement).getBoundingClientRect();
      return { t: (e as HTMLElement).innerText.trim().slice(0, 20), h: Math.round(r.height), w: Math.round(r.width) };
    }));
    console.log('Т5 кнопки футера:', JSON.stringify(sizes));
    for (const b of sizes) expect.soft(b.h, `кнопка «${b.t}» ≥40 px по высоте`).toBeGreaterThanOrEqual(40);
  }
  await p2.screenshot({ path: `${SHOTS}/t5-persona-360.png`, fullPage: true });
  await p2.close();
});

test('Т7: смена специальности — диалог и сегмент «Умения»', async ({ context }) => {
  const page = await open(context, `#/personas/${IDS.persona}`);
  const edit = page.getByTitle('Редактировать');
  if (await edit.count() > 0) { await edit.first().click(); await page.waitForTimeout(1500); }
  const specialtySelect = page.locator('select').filter({
    has: page.locator('option', { hasText: 'Исполнитель (фронтенд)' }),
  });
  console.log('Т7 селект специальности:', await specialtySelect.count());
  if (await specialtySelect.count() > 0) {
    await specialtySelect.first().selectOption({ label: 'Исполнитель (фронтенд)' });
    await page.waitForTimeout(1500);
    const body = await page.locator('body').innerText();
    const dlg = body.includes('Оставить свои') || body.includes('перейти на новые') || body.includes('Права и модели наследуются');
    console.log('Т7 диалог смены специальности:', dlg);
    console.log('Т7 хвост экрана:', JSON.stringify(body.slice(-400)));
    expect.soft(dlg, 'диалог смены специальности показан').toBeTruthy();
    await page.screenshot({ path: `${SHOTS}/t7-specialty-dialog-1280.png`, fullPage: true });
  }
  await page.close();
});

test('Т8: сегмент «Умения» в визитке персоны', async ({ context }) => {
  const p2 = await open(context, `#/personas/${IDS.persona}`);
  const skills = p2.getByRole('button', { name: 'Умения' });
  console.log('Т8 сегмент «Умения» (кнопок):', await skills.count());
  expect.soft(await skills.count(), 'сегмент «Умения» есть').toBeGreaterThan(0);
  if (await skills.count() > 0) {
    await skills.first().click({ timeout: 10000 }).catch(e => console.log('Т8 клик не удался:', String(e).slice(0, 120)));
    await p2.waitForTimeout(1500);
    const body = await p2.locator('body').innerText();
    console.log('Т8 «Умения»: длина текста', body.length, '| фрагмент:', JSON.stringify(body.slice(0, 300)));
    await p2.screenshot({ path: `${SHOTS}/t8-skills-1280.png`, fullPage: true });
  }
  await p2.close();
});

test('Т9: персона без специальности + приглушение секций (opacity) в «Промпте»', async ({ context }) => {
  // 1) персона без специальности: каркас «Без специальности»
  const p1 = await open(context, `#/personas/${IDS.personaNoSpecialty}`);
  const edit = p1.getByTitle('Редактировать');
  if (await edit.count() > 0) { await edit.first().click(); await p1.waitForTimeout(1500); }
  const body = await p1.locator('body').innerText();
  console.log('Т9 «Наследовать не от кого» в форме:', body.includes('Наследовать не от кого'));
  const promptTab = p1.getByText('Промпт', { exact: true });
  if (await promptTab.count() > 0) {
    await promptTab.first().click();
    await p1.waitForTimeout(1200);
    console.log('Т9 prompt-no-specialty:', await p1.getByTestId('prompt-no-specialty').count());
    console.log('Т9 prompt-from-specialty:', await p1.getByTestId('prompt-from-specialty').count());
  }
  await p1.screenshot({ path: `${SHOTS}/t9-no-specialty-1280.png`, fullPage: true });
  await p1.close();

  // 2) приглушение секций, пришедших от специальности
  const p2 = await open(context, `#/personas/${IDS.persona}`);
  const edit2 = p2.getByTitle('Редактировать');
  if (await edit2.count() > 0) { await edit2.first().click(); await p2.waitForTimeout(1500); }
  const tab = p2.getByText('Промпт', { exact: true });
  if (await tab.count() > 0) {
    await tab.first().click();
    await p2.waitForTimeout(1500);
    const opacity = await p2.locator('[data-testid^="prompt-section-"]').evaluateAll(els =>
      els.map(e => ({ id: e.getAttribute('data-testid'), op: getComputedStyle(e).opacity })));
    console.log('Т9 opacity секций:', JSON.stringify(opacity));
  }
  await p2.close();
});

test.afterAll(() => {
  console.log('ФИНАЛ ошибки консоли:', JSON.stringify(errors.slice(0, 20)));
});
