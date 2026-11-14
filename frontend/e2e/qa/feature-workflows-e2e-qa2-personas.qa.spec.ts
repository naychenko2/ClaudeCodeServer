import { test, expect } from '@playwright/test';
import { login, api, type Api } from './helpers';

// QA-2 (задача 6c3cdc39), блок персон.
// S1 = п.1: мастер создания персоны с наследованием (не API-путь).
// S2 = п.3: фактическая смена прав и модели унаследовавшей персоны на живом ходу;
//      персональная кастомизация не теряется.
// S3 = п.4: средний и слабый слоты по отдельности.
// S4 = п.2: подтверждение при смене специальности для своих секций промпта.
// Порядок: S1 → S2 → S3 (всё на analyst) → S4 (смена специальности в конце).

const NAME = 'qa-e2e-qa2-наследник';
const OWN_NAME = 'qa-e2e-qa2-своя';
const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const SHOTS = '.cc-attachments/qa-wf-e2e';

let client: Api;
let request: import('@playwright/test').APIRequestContext;
let token: string;
let personaId: string | null = null;
let ownPersonaId: string | null = null;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => {
  if (personaId) await client.del(`/api/personas/${personaId}`).catch(() => undefined);
  if (ownPersonaId) await client.del(`/api/personas/${ownPersonaId}`).catch(() => undefined);
  await request?.dispose();
});

const flat = (s: string) => s.replace(/\n/g, ' | ');

async function openPage(context: import('@playwright/test').BrowserContext, path: string) {
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  page.on('pageerror', e => errs.push(String(e).slice(0, 200)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3000);
  return { page, errs };
}

async function nextStep(page: import('@playwright/test').Page) {
  const next = page.getByRole('button', { name: 'Далее' });
  await next.click({ timeout: 10000 });
  await page.waitForTimeout(2000);
}

test('S1: мастер создания персоны со специальностью — наследование, своих слотов и секций нет', async ({ context }) => {
  test.setTimeout(240_000);
  const { page, errs } = await openPage(context, '/#/personas');

  await page.getByRole('button', { name: 'Новая персона' }).first().click({ timeout: 15000 });
  await page.waitForTimeout(2000);
  await page.getByText('С нуля').first().click({ timeout: 10000 });
  await page.waitForTimeout(800);
  await nextStep(page); // шаг 2 «Основа»

  await page.getByPlaceholder('Например, Ассистент').fill(NAME);
  await nextStep(page); // шаг 3 «Характер»
  await nextStep(page); // шаг 4 «Поведение»

  const sel = page.getByLabel('Специальность');
  const opts = await sel.locator('option').allInnerTexts();
  console.log('S1 опций специальности в мастере:', JSON.stringify(opts.slice(0, 8)));
  await sel.selectOption({ value: 'analyst' });
  await page.waitForTimeout(1200);
  const note = await page.locator('body').innerText();
  console.log('S1 подсказка после выбора специальности:', JSON.stringify((note.match(/[^|]*наследуют[^|]*/i)?.[0] ?? '').slice(0, 200)));
  await nextStep(page); // шаг 5 «Умения»
  await nextStep(page); // шаг 6 «Проактивность»
  await nextStep(page); // шаг 7 «Доступ и память»
  await nextStep(page); // шаг 8 «Внешность»
  await page.waitForTimeout(2500); // генерация аватара на шаге «Внешность»
  await nextStep(page); // шаг 9 «Готово»
  await page.waitForTimeout(1500);
  const done = await page.locator('body').innerText();
  console.log('S1 шаг «Готово» есть:', done.includes('Персона создана'));
  expect.soft(done.includes('Персона создана'), 'мастер дошёл до шага «Готово»').toBeTruthy();
  await page.screenshot({ path: `${SHOTS}/qa2-s1-wizard-done.png`, fullPage: true });
  await page.close();

  // Что создал мастер — проверяем по API и в форме.
  const personas = await client.get<any[]>('/api/personas');
  const p = personas.find(x => x.name === NAME);
  expect(!!p, 'мастер создал персону').toBeTruthy();
  personaId = p!.id;
  const cur = await client.get<any>(`/api/personas/${personaId}`);
  console.log('S1 persona:', JSON.stringify({
    id: personaId, specialty: cur?.specialty, inheritsAccess: cur?.inheritsAccess,
    tierStrong: cur?.tierStrong ?? null, tierMedium: cur?.tierMedium ?? null, tierWeak: cur?.tierWeak ?? null,
    promptSections: cur?.promptSections ?? null, disallowedTools: cur?.disallowedTools ?? null,
    access: cur?.access, tools: cur?.tools ?? null,
  }));
  expect.soft(cur?.specialty, 'специальность из мастера — analyst').toBe('analyst');
  expect.soft(cur?.tierStrong ?? null, 'своего слота strong нет').toBeNull();
  expect.soft(cur?.tierMedium ?? null, 'своего слота medium нет').toBeNull();
  expect.soft(cur?.tierWeak ?? null, 'своего слота weak нет').toBeNull();
  const ownSections = (cur?.promptSections ?? []).filter((s: any) => s?.text != null || s?.enabled === false);
  expect.soft(ownSections.length, 'своих секций промпта нет').toBe(0);
  expect.soft(cur?.inheritsAccess, 'права наследуются (inheritsAccess=true)').toBe(true);
  // Наследование прав: выдача персоны = эффективный шаблон специальности (analyst запрещает WebSearch/Read).
  const dis = (cur?.disallowedTools ?? []) as string[];
  expect.soft(dis.includes('WebSearch'), 'запрет специальности дошёл до персоны (WebSearch)').toBeTruthy();
  expect.soft(dis.includes('Read'), 'запрет специальности дошёл до персоны (Read)').toBeTruthy();

  // Форма: модели показаны унаследованными (не своими).
  const { page: f, errs: fe } = await openPage(context, `/#/personas/${personaId}`);
  await f.getByRole('button', { name: 'Редактировать' }).first().click({ timeout: 15000 }).catch(() => undefined);
  await f.waitForTimeout(2500);
  const inh = await f.getByTestId('models-inherited-strong').count();
  const ownHint = await f.getByTestId('models-own-hint').count();
  console.log('S1 формы моделей: унаследованных ячеек:', inh, '| подсказка «своё»:', ownHint);
  expect.soft(inh, 'ячейки моделей показаны унаследованными').toBeGreaterThan(0);
  await f.screenshot({ path: `${SHOTS}/qa2-s1-form-models.png`, fullPage: true });
  await f.close();
  console.log('S1 ошибки консоли:', JSON.stringify([...new Set([...errs, ...fe])]));
});

test('S2: правка специальности — унаследовавшая персона получает новое значение на живом ходу; своё не теряется', async () => {
  test.setTimeout(420_000);
  expect(personaId, 'S1 должна пройти раньше').toBeTruthy();

  // Персона с личной кастомизацией прав (свой запрет), чтобы проверить, что она не теряется.
  ownPersonaId = (await client.post<any>('/api/personas', {
    name: OWN_NAME, specialty: 'analyst', inheritsAccess: false,
    disallowedTools: ['WebFetch'],
  })).id;
  const beforeOwn = await client.get<any>(`/api/personas/${ownPersonaId}`);
  console.log('S2 до правки специальности: своя персона disallowed:', JSON.stringify(beforeOwn?.disallowedTools));

  // Правка глобальной специальности analyst: новый запрет инструмента + модель среднего слота.
  const settings = await client.get<any>('/api/specialties/settings');
  const layer = JSON.parse(JSON.stringify(settings.global));
  const an = layer.specialties.analyst ?? (layer.specialties.analyst = {});
  an.disallowedTools = [...new Set([...(an.disallowedTools ?? []), 'TaskStop'])];
  an.tierMedium = 'qwen3.8-27b';
  await client.put('/api/specialties/settings/global', layer);
  console.log('S2 слой analyst после правки:', JSON.stringify(layer.specialties.analyst));

  // Унаследовавшая персона получила новое значение сразу (без правки самой персоны).
  const after = await client.get<any>(`/api/personas/${personaId}`);
  const dis = (after?.disallowedTools ?? []) as string[];
  console.log('S2 унаследовавшая персона disallowed:', JSON.stringify(dis));
  expect.soft(dis.includes('TaskStop'), 'новый запрет специальности дошёл до унаследовавшей персоны').toBeTruthy();

  // Личная кастомизация другой персоны не потерялась.
  const afterOwn = await client.get<any>(`/api/personas/${ownPersonaId}`);
  const ownDis = (afterOwn?.disallowedTools ?? []) as string[];
  console.log('S2 своя персона после правки специальности:', JSON.stringify(ownDis));
  expect.soft(ownDis.includes('WebFetch'), 'персональная кастомизация (WebFetch) не потерялась').toBeTruthy();
  expect.soft(!ownDis.includes('TaskStop'), 'у своей персоны новый запрет специальности не подставился').toBeTruthy();

  // Живой ход персоны на новом значении модели: задача с уровнем medium исполняется
  // персоной-наследником — резолв идёт через средний слот специальности.
  const t = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-live-medium', assignee: 'claude', personaId, modelTier: 'medium',
    description: 'Ответь ровно одно слово: готово',
  });
  const ex = await request.post(`/api/tasks/${t.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
  console.log('S2 execute:', ex.status());
  let done = false;
  for (let i = 0; i < 40; i++) {
    await new Promise(r => setTimeout(r, 4000));
    const cur = await client.get<any>(`/api/tasks/${t.id}`);
    if (cur?.status === 'done') { done = true; console.log('S2 живой ход завершился за', (i + 1) * 4, 'с'); break; }
  }
  const task = await client.get<any>(`/api/tasks/${t.id}`);
  const sid = task?.linkedSessionId;
  let model: string | null = null;
  if (sid) {
    const sessions = await client.get<any[]>(`/api/projects/${PID}/sessions`);
    model = sessions.find(s => s.id === sid)?.model ?? null;
    console.log('S2 сессия хода:', sid, 'model:', model);
  }
  console.log('S2 живой ход:', done ? 'ЕСТЬ' : 'НЕТ', '| модель хода:', model);
  expect.soft(done, 'живой ход персоны завершился').toBeTruthy();
  expect.soft(model, 'ход персоны шёл на модели из нового среднего слота специальности').toBe('qwen3.8-27b');
  await client.del(`/api/projects/${PID}/tasks/${t.id}`).catch(() => undefined);
});

test('S3: средний и слабый слоты по отдельности — значения разные и применяются', async () => {
  test.setTimeout(300_000);
  expect(personaId, 'S1 должна пройти раньше').toBeTruthy();

  // Что вообще есть на стенде: список моделей для выбора разных значений слотов.
  const models = await client.get<any>('/api/models');
  const list: any[] = models?.models ?? [];
  console.log('S3 каталог моделей:', JSON.stringify(list).slice(0, 600));
  const ids: string[] = list.flatMap((g: any) => Array.isArray(g?.models) ? g.models.map((m: any) => m?.id ?? m) : (g?.id ? [g.id] : []));
  const uniq = [...new Set(ids.filter(Boolean))] as string[];
  const localId = uniq.find(id => /qwen/i.test(id)) ?? 'qwen3.8-27b';
  const otherId = uniq.find(id => id !== localId) ?? null;
  console.log('S3 выбрано: local=', localId, '| другой=', otherId);

  // Средний и слабый слоты специальности — разные значения.
  const settings = await client.get<any>('/api/specialties/settings');
  const layer = JSON.parse(JSON.stringify(settings.global));
  const an = layer.specialties.analyst ?? (layer.specialties.analyst = {});
  an.tierMedium = localId;
  an.tierWeak = otherId ?? localId;
  await client.put('/api/specialties/settings/global', layer);
  const saved = await client.get<any>('/api/specialties/settings');
  const anSaved = saved.global.specialties.analyst;
  console.log('S3 сохранённые слоты:', JSON.stringify({ medium: anSaved?.tierMedium, weak: anSaved?.tierWeak }));
  expect.soft(anSaved?.tierMedium, 'средний слот сохранён').toBe(localId);
  expect.soft(anSaved?.tierWeak, 'слабый слот сохранён').toBe(otherId ?? localId);

  // Применение по отдельности: задачи с уровнем medium и с уровнем weak резолвятся
  // в свою ячейку. Модель хода читаем по сессии исполнителя.
  async function runAt(tier: 'medium' | 'weak'): Promise<string | null> {
    const t = await client.post<any>(`/api/projects/${PID}/tasks`, {
      title: `qa-e2e-qa2-live-${tier}`, assignee: 'claude', personaId, modelTier: tier,
      description: 'Ответь ровно одно слово: готово',
    });
    const ex = await request.post(`/api/tasks/${t.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
    console.log(`S3 execute ${tier}:`, ex.status());
    for (let i = 0; i < 25; i++) {
      await new Promise(r => setTimeout(r, 4000));
      const cur = await client.get<any>(`/api/tasks/${t.id}`);
      if (cur?.status === 'done' || cur?.status === 'todo' || cur?.executorStopReason) break;
    }
    const task = await client.get<any>(`/api/tasks/${t.id}`);
    let model: string | null = null;
    if (task?.linkedSessionId) {
      const sessions = await client.get<any[]>(`/api/projects/${PID}/sessions`);
      model = sessions.find(s => s.id === task.linkedSessionId)?.model ?? null;
    }
    console.log(`S3 ${tier}: статус=${task?.status} stop=${task?.executorStopReason} model=${model}`);
    const out = model;
    await client.del(`/api/projects/${PID}/tasks/${t.id}`).catch(() => undefined);
    return out;
  }

  const mMedium = await runAt('medium');
  const mWeak = await runAt('weak');
  console.log('S3 резолв: medium→', mMedium, '| weak→', mWeak);
  expect.soft(mMedium, 'ход уровня medium идёт на значении среднего слота').toBe(localId);
  if (otherId) {
    expect.soft(mWeak, 'ход уровня weak отличается от medium').not.toBe(mMedium);
  } else {
    console.log('S3 на стенде одна рабочая модель — различить значения чем нечем');
  }
});

test('S4: смена специальности персоной со своими секциями — диалог подтверждения', async ({ context }) => {
  test.setTimeout(300_000);
  expect(personaId, 'S1 должна пройти раньше').toBeTruthy();

  // Заведем своей секции промпта через API (это состояние рождается и в форме: «Своё»).
  const secs = await client.get<any[]>(`/api/personas/${personaId}/prompt-sections`);
  const target = (secs ?? []).find((s: any) => s?.enabled !== false);
  console.log('S4 секции персоны:', JSON.stringify((secs ?? []).map((s: any) => ({ id: s?.id, enabled: s?.enabled, source: s?.source }))).slice(0, 400));
  expect(!!target, 'есть рабочая секция каталога').toBeTruthy();
  await client.put(`/api/personas/${personaId}`, {
    promptSections: [{ id: target.id, enabled: true, text: 'Свой текст QA-2: особенная манера ответа.' }],
  });
  const own = await client.get<any>(`/api/personas/${personaId}`);
  console.log('S4 своих секций после записи:', JSON.stringify(own?.promptSections));
  expect.soft((own?.promptSections ?? []).length, 'своя секция записи есть').toBeGreaterThan(0);

  // Форма: смена специальности → диалог «Сбросить свои секции промпта?»
  const { page, errs } = await openPage(context, `/#/personas/${personaId}`);
  await page.getByRole('button', { name: 'Редактировать' }).first().click({ timeout: 15000 });
  await page.waitForTimeout(2500);
  await page.getByText('Умения', { exact: true }).first().click().catch(() => undefined);
  await page.waitForTimeout(1500);
  const sel = page.getByLabel('Специальность');
  console.log('S4 селект специальности:', await sel.count());
  await sel.selectOption({ value: 'planner' });
  await page.waitForTimeout(2000);
  const body = flat(await page.locator('body').innerText());
  const hasDialog = body.includes('Сбросить свои секции промпта');
  console.log('S4 диалог подтверждения:', hasDialog, '|', JSON.stringify(body.match(/[^|]*свои секции[^|]*/gi)?.slice(0, 2) ?? []));
  expect.soft(hasDialog, 'диалог «Сбросить свои секции промпта?» показан').toBeTruthy();
  await page.screenshot({ path: `${SHOTS}/qa2-s4-confirm-dialog.png`, fullPage: true });

  // Без подтверждения: «Оставить как есть» — специальность и секции не меняются.
  await page.getByRole('button', { name: 'Оставить как есть' }).click({ timeout: 10000 });
  await page.waitForTimeout(1500);
  const selVal = await sel.inputValue().catch(() => null);
  console.log('S4 специальность после «Оставить как есть»:', selVal);
  expect.soft(selVal, 'без подтверждения специальность не сменилась').toBe('analyst');
  await page.getByRole('button', { name: 'Отменить' }).first().click().catch(() => undefined);
  await page.waitForTimeout(1500);
  const mid = await client.get<any>(`/api/personas/${personaId}`);
  expect.soft(mid?.specialty, 'без подтверждения специальность на бэке прежняя').toBe('analyst');
  expect.soft((mid?.promptSections ?? []).length, 'без подтверждения свои секции целы').toBeGreaterThan(0);

  // С подтверждением: «Сменить специальность» + «Сохранить» — специальность сменилась,
  // свои секции сброшены.
  await page.getByRole('button', { name: 'Редактировать' }).first().click({ timeout: 15000 });
  await page.waitForTimeout(2000);
  await page.getByText('Умения', { exact: true }).first().click().catch(() => undefined);
  await page.waitForTimeout(1200);
  await page.getByLabel('Специальность').selectOption({ value: 'planner' });
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: 'Сменить специальность' }).click({ timeout: 10000 });
  await page.waitForTimeout(1500);
  await page.getByTestId('persona-form-footer').getByRole('button', { name: 'Сохранить' }).click({ timeout: 10000 });
  await page.waitForTimeout(3000);
  const after = await client.get<any>(`/api/personas/${personaId}`);
  console.log('S4 после подтверждения:', JSON.stringify({ specialty: after?.specialty, promptSections: after?.promptSections ?? null }));
  expect.soft(after?.specialty, 'с подтверждением специальность сменилась').toBe('planner');
  const left = (after?.promptSections ?? []).filter((s: any) => s?.text != null);
  expect.soft(left.length, 'с подтверждением свои секции сброшены').toBe(0);
  console.log('S4 ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
});
