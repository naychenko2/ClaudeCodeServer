// Повторная проверка дефекта c28b42bf (задача fe0d4085, повтор после QA-ХВ2): сетка плиток
// предметов на 768 при трёх включённых предметах. Фикс — 1f133d6a7 (repeat(3, minmax(0,1fr))).
//
// Сценарии строго по карточке дефекта: ребёнок с тремя включёнными предметами, вход на 768;
// плюс контроль, что на 360 и 1280 вид прежний (ветки 1fr и те же размеры треков).
//
// Стенд: свой каталог данных (data-qa), пул подписки anthropic-api-kids — живых ходов тьютора
// тут нет, проверяем интерфейс. Скриншоты — .cc-attachments/qa/kids-tails-w2-recheck/shots.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, type Api } from './helpers';
import { mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const SHOTS = resolve(HERE, '../../../.cc-attachments/qa/kids-tails-w2-recheck/shots');

const ADMIN_U = process.env.E2E_USER || 'admin';
const ADMIN_P = process.env.E2E_PASS || '12345';
const CHILD_U = 'qa_kids_w3';
const CHILD_PW = 'Kidsqa12345';
const PROVIDER = 'anthropic-api-kids';
const MODEL = 'sonnet';

// Три включённых предмета — ровно как в шагах воспроизведения дефекта.
const MATH = { id: 'math', label: 'Математика', glyph: 'Мат', color: 'rose' };
const PHYS = { id: 'phys', label: 'Физика', glyph: 'Физ', color: 'info' };
const CHEM = { id: 'chem', label: 'Химия', glyph: 'Хим', color: 'success' };
const ALL_SUBJECTS = [MATH, PHYS, CHEM];

// Длинное имя репетитора: на нём проверяется обрезка многоточием (имя даёт бэкенд
// «Репетитор: {метка}», правится админской ручкой PUT child-tutors).
const LONG_TUTOR = 'Репетитор: Химия — органическая, неорганическая и физическая химия';

test.describe.configure({ mode: 'serial' });

let adminReq: APIRequestContext;
let childReq: APIRequestContext;
let admin: Api;
let childToken = '';
let childId = '';
let chemTutorId = '';

function subjectBody(s: { id: string; label: string; glyph: string; color: string }) {
  return { ...s, providerKey: PROVIDER, model: MODEL, enabled: true };
}

function profileBody() {
  return {
    enabled: true,
    guardianUserIds: [],
    providerKey: PROVIDER,
    model: MODEL,
    spaces: { lessons: { windows: [{ from: '00:00', to: '23:59' }], dailyMinutes: 30 } },
    subjects: ALL_SUBJECTS.map(subjectBody),
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

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  adminReq = await playwright.request.newContext({ baseURL });
  childReq = await playwright.request.newContext({ baseURL });

  const adminToken = await tokenOf(adminReq, ADMIN_U, ADMIN_P);
  admin = api(adminReq, adminToken);

  await admin.put('/api/feature-flags/child-mode', { enabled: true });

  // Фикстура с тем же именем, что у приёмки ХВ2: существующая удаляется, дальше — новая.
  const users = await admin.get<Array<{ id: string; username: string }>>('/api/users');
  const found = users.find(u => u.username === CHILD_U);
  if (found) await admin.del(`/api/users/${found.id}`);
  childId = (await admin.post<{ id: string }>('/api/users', {
    username: CHILD_U, password: CHILD_PW, role: 'user', executionEnvironment: 'container',
  })).id;

  // Профиль с тремя включёнными предметами: бэкенд провижит репетитора на каждый (UsersController.cs:312).
  await admin.put(`/api/users/${childId}/child-profile`, profileBody());
  childToken = await tokenOf(childReq, CHILD_U, CHILD_PW);

  // Правила согласованы ручками: иначе ребёнок видит экран «Сначала договоримся о правилах».
  const rules = await childReq.post('/api/child/rules/start', { headers: { Authorization: `Bearer ${childToken}` } });
  expect(rules.status(), 'засев: чат правил').toBeLessThan(300);
  const agreed = await childReq.post('/api/child/rules/agree', {
    headers: { Authorization: `Bearer ${childToken}` },
    data: { sessionId: (await rules.json()).sessionId, summary: 'Слушаю репетитора, стараюсь, прошу помощи, когда непонятно.' },
  });
  expect(agreed.status(), 'засев: правила согласованы').toBeLessThan(300);

  // Имя репетитора химии делаем длинным: на нём виден путь обрезки многоточием.
  const childApi = api(childReq, childToken);
  const { subjects } = await childApi.get<{ subjects: Array<{ id: string; tutor?: { personaId: string; name: string } | null }> }>('/api/child/subjects');
  const chemTutor = subjects.find(s => s.id === CHEM.id)?.tutor;
  expect(chemTutor, 'засев: у предмета есть репетитор').toBeTruthy();
  chemTutorId = chemTutor!.personaId;
  await setTutorName(LONG_TUTOR);
});

// Имя репетитора — админская правка той же персоны (partial-патч, other-слоты не трогаются).
async function setTutorName(name: string) {
  await admin.put(`/api/users/${childId}/child-tutors/${chemTutorId}`, { name });
}

test.afterAll(async () => {
  // Фикстура остаётся на стенде намеренно: по ней же снимается ui_probe живого стенда
  // (числа горизонтального переполнения). Следующий прогон этого спека снимает её и ставит новую.
  await adminReq?.dispose();
  await childReq?.dispose();
});

async function enter(page: Page) {
  await page.addInitScript(
    ({ t, r, u }) => {
      localStorage.setItem('cc_token', t);
      localStorage.setItem('cc_role', r);
      localStorage.setItem('cc_username', u);
    },
    { t: childToken, r: 'user', u: CHILD_U },
  );
}

function subjectGrid(page: Page) {
  return page.getByText('Предметы', { exact: true }).locator('xpath=following-sibling::div[1]');
}

function tile(page: Page, label: string) {
  return subjectGrid(page).getByRole('button').filter({ hasText: label }).first();
}

function tutorNameSpan(page: Page, label: string) {
  return tile(page, label).locator('span[style*="ellipsis"]').first();
}

// Плитки: их координаты и их собственное содержимое; сетка: scrollWidth против clientWidth;
// документ: ширина и всё, что вылезло за окно.
async function layoutInfo(page: Page, width: number) {
  const grid = subjectGrid(page);
  const gridBox = await grid.evaluate(el => ({
    scrollW: el.scrollWidth, clientW: el.clientWidth,
    left: el.getBoundingClientRect().left, right: el.getBoundingClientRect().right,
  }));
  const tiles: Array<{ label: string; left: number; right: number; top: number; inWindow: boolean }> = [];
  for (const s of ALL_SUBJECTS) {
    const t = tile(page, s.label);
    await expect(t, `на ${width} плитка «${s.label}» есть`).toBeVisible({ timeout: 20000 });
    const r = await t.evaluate(el => {
      const b = el.getBoundingClientRect();
      return { left: b.left, right: b.right, top: b.top };
    });
    tiles.push({ label: s.label, ...r, inWindow: r.left >= -0.5 && r.right <= width + 0.5 });
  }
  const doc = await page.evaluate((w) => {
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
  }, width);
  return { gridBox, tiles, doc };
}

async function measureTutorLine(page: Page, width: number) {
  const span = tutorNameSpan(page, CHEM.label);
  await expect(span, `на ${width} у плитки есть строка репетитора`).toBeVisible({ timeout: 20000 });
  const m = await span.evaluate(el => {
    const cs = getComputedStyle(el);
    return {
      text: (el.textContent || '').trim(),
      scrollW: el.scrollWidth, clientW: el.clientWidth,
      textOverflow: cs.textOverflow, whiteSpace: cs.whiteSpace,
    };
  });
  console.log(`(${width}) строка репетитора:`, JSON.stringify(m));
  expect(m.text, `имя репетитора на месте (${m.text})`).toBeTruthy();
  expect(m.textOverflow, 'обрезка многоточием включена').toBe('ellipsis');
  expect(m.whiteSpace, 'строка не переносится').toBe('nowrap');
  return m;
}

// ── С1 (768): три плитки в окне, сетка не шире контейнера, длинное имя обрезано ────────────
test('С1 (768): сетка трёх предметов укладывается в окно, имя репетитора обрезано', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.setViewportSize({ width: 768, height: 1024 });
  await enter(page);
  await page.goto('/');

  await expect(subjectGrid(page), 'на 768 сетка предметов есть').toBeVisible({ timeout: 30000 });
  const info = await layoutInfo(page, 768);
  console.log('С1 (768):', JSON.stringify(info));

  expect(info.gridBox.scrollW, `сетка не шире своего контейнера: ${JSON.stringify(info.gridBox)}`)
    .toBeLessThanOrEqual(info.gridBox.clientW);
  expect(info.tiles.filter(t => t.inWindow).length, `все три плитки в окне: ${JSON.stringify(info.tiles)}`).toBe(3);
  expect(info.doc.docScrollW, 'документ не шире окна').toBeLessThanOrEqual(768);
  expect(info.doc.offenders, `элементы выходят за окно 768: ${JSON.stringify(info.doc.offenders)}`).toEqual([]);

  // Три колонки в один ряд — как было на десктопе
  const tops = new Set(info.tiles.map(t => Math.round(t.top / 8)));
  expect(tops.size, `три плитки стоят в один ряд: ${JSON.stringify(info.tiles.map(t => t.top))}`).toBe(1);

  const t = await measureTutorLine(page, 768);
  expect(t.text, 'имя репетитора длинное').toBe(LONG_TUTOR);
  expect(t.scrollW, 'имя не помещается в плитку — оно обрезается').toBeGreaterThan(t.clientW);
  await page.screenshot({ path: resolve(SHOTS, 's1-768-three-subjects-long-name.png'), fullPage: true });

  expect(errors.filter(e => !e.includes('Failed to load resource') && !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

// ── С2 (360): мобильная ветка не изменена — данные карточки дефекта (имена как их дал бэкенд) ─
test('С2 (360): мобильная раскладка прежняя — плитки столбцом в одну колонку', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await setTutorName('Репетитор: Химия');
  await page.setViewportSize({ width: 360, height: 800 });
  await enter(page);
  await page.goto('/');

  await expect(subjectGrid(page), 'на 360 сетка предметов есть').toBeVisible({ timeout: 30000 });
  const info = await layoutInfo(page, 360);
  console.log('С2 (360):', JSON.stringify(info));

  expect(info.tiles.filter(t => t.inWindow).length, `все три плитки в окне: ${JSON.stringify(info.tiles)}`).toBe(3);
  expect(info.doc.docScrollW, 'документ не шире окна').toBeLessThanOrEqual(360);
  expect(info.doc.offenders, `элементы выходят за окно 360: ${JSON.stringify(info.doc.offenders)}`).toEqual([]);

  // Одна колонка: каждая следующая плитка ниже предыдущей
  const sorted = [...info.tiles].sort((a, b) => a.top - b.top);
  for (let i = 1; i < sorted.length; i++) {
    expect(sorted[i].top, `плитка «${sorted[i].label}» идёт под предыдущей, а не в соседней колонке: ${JSON.stringify(sorted)}`)
      .toBeGreaterThan(sorted[i - 1].top);
  }

  await measureTutorLine(page, 360);
  await page.screenshot({ path: resolve(SHOTS, 's2-360-three-subjects.png'), fullPage: true });

  expect(errors.filter(e => !e.includes('Failed to load resource') && !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

// ── С3 (1280): десктопная ветка не изменена — те же три колонки, переполнения нет ──────────
test('С3 (1280): вид прежний — три колонки в один ряд, переполнения нет', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page);
  await page.goto('/');

  await expect(subjectGrid(page), 'на 1280 сетка предметов есть').toBeVisible({ timeout: 30000 });
  const info = await layoutInfo(page, 1280);
  console.log('С3 (1280):', JSON.stringify(info));

  expect(info.gridBox.scrollW, `сетка не шире своего контейнера: ${JSON.stringify(info.gridBox)}`)
    .toBeLessThanOrEqual(info.gridBox.clientW);
  expect(info.tiles.filter(t => t.inWindow).length, `все три плитки в окне: ${JSON.stringify(info.tiles)}`).toBe(3);
  expect(info.doc.docScrollW, 'документ не шире окна').toBeLessThanOrEqual(1280);
  expect(info.doc.offenders, `элементы выходят за окно 1280: ${JSON.stringify(info.doc.offenders)}`).toEqual([]);

  const tops = new Set(info.tiles.map(t => Math.round(t.top / 8)));
  expect(tops.size, `три плитки стоят в один ряд: ${JSON.stringify(info.tiles.map(t => t.top))}`).toBe(1);

  await measureTutorLine(page, 1280);
  await page.screenshot({ path: resolve(SHOTS, 's3-1280-three-subjects.png'), fullPage: true });

  expect(errors.filter(e => !e.includes('Failed to load resource') && !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

// ── С4 (360, длинное имя): та же min-content проблема в мобильной ветке '1fr' ──────────────
// Фикс 1f133d6a7 менял только десктопную ветку repeat(3, …); '1fr' остался как был.
// Минимум 1fr — min-content содержимого плитки, а nowrap-строка длинного репетитора даёт
// min-content ≈ её собственной ширине: на 360 плитка шире экрана, многоточие не включается.
// Это не регрессия фикса (поведение было таким и раньше) — это тот же дефект в другой ветке.
test('С4 (360, длинное имя репетитора): плитка шире экрана — observed-поведение зафиксировано', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await setTutorName(LONG_TUTOR);
  await page.setViewportSize({ width: 360, height: 800 });
  await enter(page);
  await page.goto('/');

  await expect(subjectGrid(page), 'на 360 сетка предметов есть').toBeVisible({ timeout: 30000 });
  const info = await layoutInfo(page, 360);
  const t = await measureTutorLine(page, 360);
  console.log('С4 (360, длинное имя):', JSON.stringify({ info, t }));

  // Фиксируем фактическое: плитка выходит за правый край, многоточие не сработало
  const wide = info.tiles.find(x => x.label === CHEM.label)!;
  expect(wide.right, 'найдено: плитка шире окна 360').toBeGreaterThan(360);
  expect(t.scrollW, 'найдено: имя не обрезается — его ширина целиком в плитке').toBeLessThanOrEqual(t.clientW);
  await page.screenshot({ path: resolve(SHOTS, 's4-360-long-name-overflow.png'), fullPage: true });

  expect(errors.filter(e => !e.includes('Failed to load resource') && !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});
