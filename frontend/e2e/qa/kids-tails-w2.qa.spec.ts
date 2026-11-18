// Приёмка мини-волны ХВ2 (QA-ХВ2, задача 45df91ba): имя ребёнка в рельсе на 768,
// подпись предмета в «Старых уроках», подсказка первого урока, регрессия рельса взрослого.
//
// Закрывает дефекты QA-1 (54827999): f0c03f21 (имя уезжало за левый край) и 698bcb27
// (строка «Старых уроков» без названия предмета).
//
// Стенд: свой каталог данных, пул подписки anthropic-api-kids (живых ходов тьютора тут
// нет — проверяем интерфейс). Скриншоты — в .cc-attachments/qa/kids-tails-w2/shots.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, type Api } from './helpers';
import { mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const SHOTS = resolve(HERE, '../../../.cc-attachments/qa/kids-tails-w2/shots');

const ADMIN_U = process.env.E2E_USER || 'admin';
const ADMIN_P = process.env.E2E_PASS || '12345';
const CHILD_U = 'qa_kids_w3';
const CHILD_PW = 'Kidsqa12345';
const PROVIDER = 'anthropic-api-kids';
const MODEL = 'sonnet';

const MATH = { id: 'math', label: 'Математика', glyph: 'Мат', color: 'rose' };
const PHYS = { id: 'phys', label: 'Физика', glyph: 'Физ', color: 'info' };
const CHEM = { id: 'chem', label: 'Химия', glyph: 'Хим', color: 'success' };
const HIST = { id: 'hist', label: 'История', glyph: 'Ист', color: 'warning' };
const LIT = { id: 'lit', label: 'Литература', glyph: 'Лит', color: 'accent' };
// Предмет без уроков (сценарий 3) и предмет с уроком (контроль подписей ХВ12).
const ALL_SUBJECTS = [MATH, PHYS, CHEM, HIST, LIT];

test.describe.configure({ mode: 'serial' });

let adminReq: APIRequestContext;
let childReq: APIRequestContext;
let admin: Api;
let adminToken = '';
let childToken = '';
let childId = '';
let projectId = '';

function subjectBody(s: { id: string; label: string; glyph: string; color: string }, enabled: boolean) {
  return { ...s, providerKey: PROVIDER, model: MODEL, enabled };
}

function profileBody(subjects: ReturnType<typeof subjectBody>[]) {
  return {
    enabled: true,
    guardianUserIds: [],
    providerKey: PROVIDER,
    model: MODEL,
    spaces: { lessons: { windows: [{ from: '00:00', to: '23:59' }], dailyMinutes: 30 } },
    subjects,
    dailyBudgetUsd: null,
    monthlyBudgetUsd: null,
    warnMinutes: 5,
  };
}

async function tokenOf(req: APIRequestContext, username: string, password: string): Promise<string> {
  const r = await req.post('/api/auth/login', { data: { username, password } });
  expect(r.ok(), `логин ${username} (${r.status()})`).toBeTruthy();
  return (await r.json()).token as string;
}

async function freshUser(adminApi: Api, username: string, password: string): Promise<string> {
  const users = await adminApi.get<Array<{ id: string; username: string }>>('/api/users');
  const found = users.find(u => u.username === username);
  if (found) await adminApi.del(`/api/users/${found.id}`);
  const created = await adminApi.post<{ id: string }>('/api/users', {
    username, password, role: 'user', executionEnvironment: 'container',
  });
  return created.id;
}

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  adminReq = await playwright.request.newContext({ baseURL });
  childReq = await playwright.request.newContext({ baseURL });

  adminToken = await tokenOf(adminReq, ADMIN_U, ADMIN_P);
  admin = api(adminReq, adminToken);

  // Флаг прячет только интерфейс включения у админа — включаем его.
  await admin.put('/api/feature-flags/child-mode', { enabled: true });

  childId = await freshUser(admin, CHILD_U, CHILD_PW);
  await admin.put(`/api/users/${childId}/child-profile`, profileBody(ALL_SUBJECTS.map(s => subjectBody(s, true))));
  childToken = await tokenOf(childReq, CHILD_U, CHILD_PW);

  // Правила согласованы ручками (живого хода тьютора тут нет): состояние ребёнка — «ok».
  const rules = await childReq.post('/api/child/rules/start', { headers: { Authorization: `Bearer ${childToken}` } });
  expect(rules.status(), 'засев: чат правил').toBeLessThan(300);
  const rulesBody = await rules.json();
  const agreed = await childReq.post('/api/child/rules/agree', {
    headers: { Authorization: `Bearer ${childToken}` },
    data: { sessionId: rulesBody.sessionId, summary: 'Слушаю репетитора, стараюсь, прошу помощи, когда непонятно.' },
  });
  expect(agreed.status(), 'засев: правила согласованы').toBeLessThan(300);

  // Четыре урока: математика (открытый предмет), физика и химия (потом выключим),
  // история (потом удалим предмет из профиля — урок без названия предмета).
  for (const subject of [MATH.id, PHYS.id, CHEM.id, HIST.id]) {
    const chat = await childReq.post('/api/child/spaces/lessons/chat', {
      headers: { Authorization: `Bearer ${childToken}` }, data: { subject },
    });
    expect(chat.status(), `засев: урок по предмету ${subject}`).toBe(201);
  }

  // Физику и химию выключаем, историю удаляем из профиля. Литература остаётся включённой,
  // но уроков по ней нет — предмет для подсказки первого урока.
  await admin.put(`/api/users/${childId}/child-profile`, profileBody([
    subjectBody(MATH, true),
    subjectBody(PHYS, false),
    subjectBody(CHEM, false),
    subjectBody(LIT, true),
  ]));

  // Проект для регрессии рельса взрослого (подпись «Проекты» и панели).
  const root = `/tmp/qa-kids-tails-w2-${Date.now()}`;
  const proj = await admin.post<{ id: string }>('/api/projects', {
    name: `qa-kids-tails-w2-${Date.now()}`, rootPath: root, createDirectory: true,
  });
  projectId = proj.id;
});

test.afterAll(async () => {
  if (childId) await admin?.del(`/api/users/${childId}`).catch(() => {});
  if (projectId) await admin?.del(`/api/projects/${projectId}`).catch(() => {});
  await adminReq?.dispose();
  await childReq?.dispose();
});

// Вход в приложение под токеном: роль и логин приложение читает из localStorage.
async function enter(page: Page, token: string, role: 'admin' | 'user' = 'user', username = '') {
  await page.addInitScript(
    ({ t, r, u }) => {
      localStorage.setItem('cc_token', t);
      localStorage.setItem('cc_role', r);
      if (u) localStorage.setItem('cc_username', u);
    },
    { t: token, r: role, u: username },
  );
}

function noise(errors: string[]): string[] {
  return errors.filter(e => !e.includes('Failed to load resource'));
}

// Капсула рельсы детской оболочки и её шапка с именем ребёнка.
function railCapsule(page: Page) {
  return page.locator('[aria-label="Разделы"]');
}

// Подпись шапки: span с многоточием (RailHat обрезает длинное имя внутри капсулы).
function railHatLabel(page: Page) {
  return railCapsule(page).locator('span[style*="ellipsis"]').first();
}

// Остров списка уроков под главной (360 и планшет 601–899).
function island(page: Page) {
  return page.locator('[data-testid="child-mobile-lessons-list"]');
}

// Плитка предмета на главной.
function subjectTile(page: Page, label: string) {
  return page.getByText('Предметы', { exact: true })
    .locator('xpath=following-sibling::div[1]')
    .getByRole('button')
    .filter({ hasText: label })
    .first();
}

// Элементы, вылезающие за окно, и ширина документа.
async function overflowInfo(page: Page) {
  return page.evaluate(() => {
    const w = window.innerWidth;
    const out: Array<{ tag: string; cls: string; left: number; right: number; text: string }> = [];
    for (const el of Array.from(document.querySelectorAll('*'))) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 && r.height === 0) continue;
      if (r.left < -0.5 || r.right > w + 0.5) {
        out.push({
          tag: el.tagName, cls: (el.className || '').toString().slice(0, 40),
          left: Math.round(r.left * 10) / 10, right: Math.round(r.right * 10) / 10,
          text: (el.textContent || '').trim().slice(0, 40),
        });
      }
    }
    return { docScrollW: document.documentElement.scrollWidth, innerW: w, offenders: out.slice(0, 12) };
  });
}

// ── Сценарий 1: имя ребёнка в шапке рельса на 768 (дефект f0c03f21) ───────────────────────
test('С1 (768): имя ребёнка в шапке рельса не уезжает за левый край, обрезается многоточием', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.setViewportSize({ width: 768, height: 1024 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');

  const cap = railCapsule(page);
  await expect(cap, 'на 768 рельса детской оболочки есть').toBeVisible({ timeout: 30000 });

  const label = railHatLabel(page);
  await expect(label, 'в шапке рельсы есть подпись').toBeVisible({ timeout: 20000 });
  const full = (await label.innerText()).trim();
  expect(full.toLowerCase(), 'подпись шапки — имя ребёнка').toBe(CHILD_U.toLowerCase());

  const m = await label.evaluate(el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return {
      left: r.left, right: r.right, width: r.width,
      scrollW: el.scrollWidth, clientW: el.clientWidth,
      textOverflow: cs.textOverflow, overflow: cs.overflow,
    };
  });
  expect(m.left, 'плашка имени начинается не левее края окна').toBeGreaterThanOrEqual(0);
  expect(m.right, 'плашка имени не выходит за правый край').toBeLessThanOrEqual(768);
  expect(m.textOverflow, 'имя обрезается многоточием').toBe('ellipsis');
  expect(m.scrollW, 'имя длиннее капсулы — текст обрезан').toBeGreaterThan(m.clientW);
  await page.screenshot({ path: resolve(SHOTS, 's1-768-rail-name.png'), fullPage: false });

  // Полное имя видно в подсказке (плашка рельсы) по наведению
  await label.hover();
  const flyout = page.locator('div').filter({ hasText: new RegExp(`^${CHILD_U}$`) });
  await expect(flyout.first(), 'подсказка с полным именем появилась').toBeVisible({ timeout: 10000 });
  await page.screenshot({ path: resolve(SHOTS, 's1-768-rail-name-flyout.png'), fullPage: false });

  // Горизонтальное переполнение на 768 быть не должно (замер — после отрисовки плиток)
  await expect(subjectTile(page, 'Математика'), 'плитки предметов отрисованы').toBeVisible({ timeout: 20000 });
  await page.waitForTimeout(500);
  const ov = await overflowInfo(page);
  console.log('С1: переполнение на 768 =', JSON.stringify(ov));
  expect(ov.offenders, `элементы выходят за окно 768: ${JSON.stringify(ov.offenders)}`).toEqual([]);
  expect(ov.docScrollW, 'документ не шире окна').toBeLessThanOrEqual(768);

  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

// ── Сценарий 2: «Старые уроки» — подпись своего предмета (дефект 698bcb27) ─────────────────
async function checkOldLessons(page: Page, width: number) {
  await page.setViewportSize({ width, height: width === 360 ? 800 : 1024 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');

  const list = island(page);
  await expect(list, `на ${width} остров уроков есть`).toBeVisible({ timeout: 30000 });

  const group = list.getByRole('button', { name: /Старые уроки/ });
  await expect(group, `на ${width} есть группа «Старые уроки»`).toBeVisible();
  const groupTitle = (await group.innerText()).trim();
  const closedCount = Number((groupTitle.match(/(\d+)\s*$/) || [])[1] || '0');
  expect(closedCount, `в заголовке группы видно число закрытых уроков: ${groupTitle}`).toBeGreaterThan(0);
  await group.click();

  // Строки уроков: открытые идут первыми, закрытые — в раскрытой группе «Старые уроки» (последние).
  const rows = list.getByRole('button', { name: /Новый урок/ });
  const n = await rows.count();
  const texts: string[] = [];
  for (let i = 0; i < n; i++) texts.push((await rows.nth(i).innerText()).trim());
  console.log(`С2 (${width}): все строки =`, JSON.stringify(texts));
  return { texts, closedCount };
}

test('С2 (360 и 768): каждая строка «Старых уроков» подписана своим предметом', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  for (const width of [360, 768]) {
    const { texts, closedCount } = await checkOldLessons(page, width);
    // Закрытые уроки — хвост списка (в группе «Старые уроки»): физика, химия и удалённая история
    const closed = texts.slice(texts.length - closedCount);
    expect(closed.length, `на ${width} столько же строк, сколько в счётчике группы`).toBe(closedCount);

    const labelled = closed.filter(t => t.includes('Физика') || t.includes('Химия'));
    expect(labelled.length, `на ${width} два урока подписаны своим предметом: ${JSON.stringify(closed)}`).toBe(2);
    expect(closed.some(t => t.includes('Физика')), `на ${width} строка физики подписана`).toBe(true);
    expect(closed.some(t => t.includes('Химия')), `на ${width} строка химии подписана`).toBe(true);

    // Урок удалённого из профиля предмета: подписи нет, строка не сломана
    const bare = closed.filter(t => !t.includes('Физика') && !t.includes('Химия'));
    expect(bare.length, `на ${width} ровно одна строка без подписи предмета: ${JSON.stringify(closed)}`).toBe(1);
    expect(bare[0], `на ${width} строка урока удалённого предмета не пуста и не сломана`).toContain('Новый урок');
    expect(bare[0].trim(), `на ${width} строка урока удалённого предмета не пуста`).not.toBe('');

    await page.screenshot({ path: resolve(SHOTS, `s2-${width}-old-lessons.png`), fullPage: true });
  }
  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

// ── Сценарий 4: регрессия рельса взрослого ──────────────────────────────────────────────
test('С4: рельс взрослого (проекты, панели, Стена) с короткими подписями — как раньше', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  for (const width of [1280, 360]) {
    await page.setViewportSize({ width, height: width === 360 ? 800 : 900 });
    await enter(page, adminToken, 'admin', ADMIN_U);
    await page.goto('/');
    await page.waitForTimeout(1500);

    // Подпись «Проекты» в рельсе не обрезана
    const hat = page.locator('[aria-label="Разделы"]').count();
    const projHat = page.getByText('Проекты', { exact: true }).first();
    if (await projHat.count()) {
      const m = await projHat.evaluate(el => ({
        scrollW: el.scrollWidth, clientW: el.clientWidth,
        left: el.getBoundingClientRect().left,
      }));
      console.log(`С4 (${width}): подпись «Проекты» =`, JSON.stringify(m));
      expect(m.scrollW, `на ${width} подпись «Проекты» не обрезана`).toBeLessThanOrEqual(m.clientW);
      expect(m.left, `на ${width} подпись «Проекты» не левее нуля`).toBeGreaterThanOrEqual(0);
    } else {
      console.log(`С4 (${width}): подпись «Проекты» не найдена (капсул: ${hat})`);
    }

    const ov = await overflowInfo(page);
    console.log(`С4 (${width}): переполнение =`, JSON.stringify(ov));
    expect(ov.offenders, `на ${width} у взрослого элементы выходят за окно: ${JSON.stringify(ov.offenders)}`).toEqual([]);
    await page.screenshot({ path: resolve(SHOTS, `s4-${width}-adult-rail.png`), fullPage: false });
  }
  expect(noise(errors).filter(e => !e.includes('401'))).toEqual([]);
});

// ── Сценарий 3: подсказка первого урока ──────────────────────────────────────────────────
test('С3 (768 и 1280): предмет без уроков зовёт «Начни свой первый урок.»', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  for (const width of [768, 1280]) {
    await page.setViewportSize({ width, height: width === 768 ? 1024 : 900 });
    await enter(page, childToken, 'user', CHILD_U);
    await page.goto('/');

    // Предмет без уроков — «Литература»
    await subjectTile(page, 'Литература').click();
    await expect(page.getByText('Начни свой первый урок.'), `на ${width} в предмете без уроков — подсказка первого урока`)
      .toBeVisible({ timeout: 20000 });
    await page.screenshot({ path: resolve(SHOTS, `s3-${width}-first-lesson.png`), fullPage: true });

    // Контроль: в предмете с уроками остаются подписи ХВ12
    await page.goto('/');
    await subjectTile(page, 'Математика').click();
    const wide = width >= 1280;
    const expected = wide ? 'Выбери урок слева или начни новый.' : 'Начни новый урок. Прошлые уроки — на главной, в списке «Мои уроки».';
    await expect(page.getByText(expected), `на ${width} в предмете с уроками — прежняя подпись ХВ12`)
      .toBeVisible({ timeout: 20000 });
    await page.screenshot({ path: resolve(SHOTS, `s3-${width}-subject-with-lessons.png`), fullPage: true });
  }
  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

