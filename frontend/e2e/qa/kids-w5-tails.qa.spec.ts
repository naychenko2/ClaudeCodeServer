// QA-10: живая приёмка трёх хвостов волны 5 «Детский режим» (Г11/Г12/Г13)
// на стенде :5500. Запуск: target=frontend/e2e/qa (docs/operations/qa-stand.md).
// Г11 — замер getBoundingClientRect кнопок детской рельсы на 768/1280/360,
//       взрослых — тем же замером (контроль 32px); наезды плашки и панели «Чаты».
// Г12 — перехват GET child-profile → 500: отказ над списком разделов на 360,
//       «Повторить» загружает окно, на 1280 отказ не задвоен.
// Г13 — удалённый опекун: REST-цепочка (назначить → удалить → записать → граница
//       guardian_not_found) плюс UI-редактор опекунов в разделе «Безопасность».

import { test, expect, type APIRequestContext } from '@playwright/test';
import {
  BASE, call, tokenOf, loginViaUi, openUserManagement, openChildSettings, type Page,
} from './kids-w5-helpers';

test.describe.configure({ mode: 'serial' });

const PWD = 'qa10-Pass-1';
const STAMP = Date.now().toString(36);
const CHILD = `qa10m${STAMP}`;
const GUARD = `qa10g${STAMP}`;
const GUARD2 = `qa10h${STAMP}`;
const GUARD3 = `qa10i${STAMP}`;
const SUBJECT = 'qa10-subj';
const ADMIN_PW = process.env.E2E_PASS || '12345';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-kids-w5-fix/.cc-attachments/qa10-shots';

let request: APIRequestContext;
let tok: string;
let childId = '';
let guardId = '';
let guard2Id = '';
let guard3Id = '';
let adminProjectId = '';

interface Rect { x: number; y: number; w: number; h: number }

/** Включить/выключить профиль ребёнка: enabled=true, предметы приходят из живого профиля (subjects:null). */
async function putChildProfile(body: Record<string, unknown>): Promise<{ status: number; body: any }> {
  return call(request, 'put', `/api/users/${childId}/child-profile`, tok, {
    // Непустой маршрут обязателен: профиль с пустыми providerKey/model — «не настроен»,
    // ChildModePolicy ставит DenyAll и вход ребёнка блокирован (403 child_mode на /me)
    enabled: true, providerKey: 'qa8', model: 'qa10-fake', acknowledgeRouteRisk: true,
    spaces: {}, subjects: null, dailyBudgetUsd: 1, monthlyBudgetUsd: 10, warnMinutes: 5, ...body,
  });
}

async function shot(page: import('@playwright/test').Page, name: string) {
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

/** Замер детских кнопок рельсы/шапки: aria-label → прямоугольник, потолок. */
const MEASURE = () => {
  const grab = (sel: string): Rect | null => {
    const el = document.querySelector(sel) as HTMLElement | null;
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return r.width || r.height ? { x: r.x, y: r.y, w: r.width, h: r.height } : null;
  };
  const round = (r: Rect | null) => (r ? { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.w), h: Math.round(r.h) } : null);
  const out: Record<string, unknown> = { viewport: window.innerWidth, buttons: {} as Record<string, Rect | null> };
  const btns: Record<string, Rect | null> = {};
  for (const n of ['Меню', 'Чаты', 'Файлы', 'Задачи', 'Ещё']) btns[n] = round(grab(`[aria-label="${n}"]`));
  // Активная панель «Чаты» переименовывает кнопку в «Скрыть „Чаты“» — меряем её как «Чаты»
  if (!btns['Чаты']) {
    const alt = document.querySelector('[aria-label^="Скрыть"]') as HTMLElement | null;
    if (alt) {
      const r = alt.getBoundingClientRect();
      btns['Чаты'] = { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
    }
  }
  out.buttons = btns;
  // Значки предметов (док рельсы): span[data-child-subject] — сам span без размера, берём ребёнка-кнопку
  out.subjectIcons = Array.from(document.querySelectorAll('[data-child-subject]')).map(el => {
    const btn = el.querySelector('button,[role="button"]') as HTMLElement | null ?? el as HTMLElement;
    const r = btn.getBoundingClientRect();
    return { id: el.getAttribute('data-child-subject'), w: Math.round(r.width), h: Math.round(r.height) };
  });
  // Капсула рельсы: предок кнопки «Файлы» шириной 44..56 (детская 48, взрослая 40)
  const capsOf = (btn: HTMLElement | null): HTMLElement | null => {
    let c = btn?.parentElement ?? null;
    while (c) {
      const w = c.getBoundingClientRect().width;
      if (w >= 44 && w <= 56) break;
      c = c.parentElement;
    }
    return c;
  };
  const capsOfRect = (btn: HTMLElement | null) => {
    const c = capsOf(btn);
    if (!c) return null;
    const r = c.getBoundingClientRect();
    return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
  };
  // Якорь «Файлы» — панельная рельса (правая); капсула «Чаты» — своя (левая на 1280)
  const files = document.querySelector('[aria-label="Файлы"]') as HTMLElement | null;
  out.railCapsule = capsOfRect(files);
  const chats = (document.querySelector('[aria-label="Чаты"]') ?? document.querySelector('[aria-label^="Скрыть"]')) as HTMLElement | null;
  out.railCapsuleChats = capsOfRect(chats);
  out.newLesson = round(grab('[data-testid="child-new-lesson"]'));
  return out;
};

const expect40 = (m: any, labels: string[]) => {
  for (const n of labels) {
    const b = m.buttons[n];
    expect(b, `кнопка «${n}» на экране`).not.toBeNull();
    expect(b.w, `«${n}» ширина ≥40 (факт ${b.w})`).toBeGreaterThanOrEqual(40);
    expect(b.h, `«${n}» высота ≥40 (факт ${b.h})`).toBeGreaterThanOrEqual(40);
  }
};

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  tok = await tokenOf(request, 'admin', ADMIN_PW);
  // Детский режим в админке — флаг per-user: без него в карточках нет «Детского режима»
  // и кнопки «Настроить занятия» (UserManagementModal childModeUi)
  await call(request, 'put', '/api/feature-flags/child-mode', tok, { enabled: true });
  const mk = async (u: string, env: string): Promise<string> => {
    const r = await call(request, 'post', '/api/users', tok, { username: u, password: PWD, role: 'user', executionEnvironment: env });
    expect(r.status, `создание ${u}`).toBeLessThan(300);
    return r.body.id as string;
  };
  guardId = await mk(GUARD, 'local');
  guard2Id = await mk(GUARD2, 'local');
  guard3Id = await mk(GUARD3, 'local');
  childId = await mk(CHILD, 'container');
  // Проект админа для взрослого контроля рельсы (у «Дома» взрослого рельсы нет)
  const proj = await call(request, 'post', '/api/projects', tok, {
    name: `qa10-adult-${STAMP}`, rootPath: `/tmp/qa10-adult-${STAMP}`, createDirectory: true,
  });
  expect(proj.status, 'создание проекта админа').toBeLessThan(300);
  adminProjectId = proj.body.id;
  // Профиль ребёнка с одним предметом «Математика» — проект предмета провижнится сам.
  const put = await putChildProfile({
    guardianUserIds: [guardId],
    subjects: [{ id: SUBJECT, label: 'Математика', glyph: '∑', color: 'info', enabled: true }],
  });
  expect(put.status, `PUT child-profile ребёнка (${JSON.stringify(put.body)})`).toBeLessThan(300);
});

test.afterAll(async () => {
  // Свои учётки + мусор дым-проб на qa10* (ребёнка и живых опекунов — последними)
  if (request && tok) {
    const list = await call(request, 'get', '/api/users', tok).catch(() => ({ body: null }) as any);
    const users: Array<{ id: string; username: string }> = list.body?.users ?? list.body ?? [];
    for (const u of users) {
      if (!u.username.startsWith('qa10')) continue;
      if (u.id === childId) continue;
      await call(request, 'delete', `/api/users/${u.id}`, tok).catch(() => undefined);
      if (u.username === GUARD2) guard2Id = '';
      if (u.username === GUARD3) guard3Id = '';
    }
    if (childId) await call(request, 'delete', `/api/users/${childId}`, tok).catch(() => undefined);
    if (adminProjectId) await call(request, 'delete', `/api/projects/${adminProjectId}`, tok).catch(() => undefined);
  }
});

test('Г11: тач-цели детской рельсы ≥40 на 768/1280, наездов нет; на 360 не поехало', async ({ page }) => {
  test.setTimeout(300_000);
  await loginViaUi(page, CHILD, PWD, true);
  await expect(page.getByTestId('child-subject-picker')).toBeVisible({ timeout: 30_000 });
  await page.getByTestId(`child-subject-tile-${SUBJECT}`).click();
  await expect(page.getByTestId('child-new-lesson')).toBeVisible({ timeout: 30_000 });

  const railChecks = async (w: number) => {
    await page.setViewportSize({ width: w, height: 820 });
    await page.waitForTimeout(900);
    const m = await page.evaluate(MEASURE);
    console.log(`QA10-MEASURE child ${w}`, JSON.stringify(m));
    const strict = w > 600;
    const found = (Object.keys(m.buttons) as string[]).filter(n => m.buttons[n]);
    const expectedInRail = strict ? ['Меню', 'Чаты', 'Файлы', 'Задачи', 'Ещё'] : null;
    if (expectedInRail) {
      expect(found, `детские кнопки рельсы на ${w}`).toEqual(expectedInRail);
    } else {
      expect(found.length, 'на 360 найдена хотя бы одна детская кнопка').toBeGreaterThan(0);
    }
    for (const n of found) {
      expect(m.buttons[n].w, `«${n}» ширина ≥40 (факт ${m.buttons[n].w})`).toBeGreaterThanOrEqual(40);
      expect(m.buttons[n].h, `«${n}» высота ≥40 (факт ${m.buttons[n].h})`).toBeGreaterThanOrEqual(40);
    }
    if (strict) {
      expect(m.subjectIcons.length, 'значок предмета в доке рельсы на экране').toBeGreaterThan(0);
      for (const ic of m.subjectIcons) {
        expect(ic.w, `значок «${ic.id}» ширина ≥40 (факт ${ic.w})`).toBeGreaterThanOrEqual(40);
        expect(ic.h, `значок «${ic.id}» высота ≥40 (факт ${ic.h})`).toBeGreaterThanOrEqual(40);
      }
      expect(m.railCapsule, 'капсула детской рельсы найдена').not.toBeNull();
      expect(m.railCapsule.w, `капсула рельсы ≥44 (факт ${m.railCapsule.w})`).toBeGreaterThanOrEqual(44);
      // Плашка предмета при закрытых панелях: НЕ пересекает капсулу рельсы (рельса бывает
      // и правой на 768 — критерий пересечения, не знака зазора)
      if (m.newLesson) {
        const capRight = m.railCapsule.x + m.railCapsule.w;
        const plateRight = m.newLesson.x + m.newLesson.w;
        const sep = Math.max(m.railCapsule.x - plateRight, m.newLesson.x - capRight);
        expect(Math.round(sep), `плашка предмета не пересекает капсулу рельсы (разнос ${sep}px)`).toBeGreaterThanOrEqual(-2);
      }
    } else {
      console.log('QA10-360-FULL', JSON.stringify(m));
    }
    await shot(page, `child-${w}`);
    return m;
  };

  await railChecks(768);
  await railChecks(1280);

  // Панель «Чаты» открыта по умолчанию; её кнопка называется «Скрыть „Чаты“».
  // Рельса бывает и правой (768) — меряем разнос по X между панелью и капсулой.
  const cap0 = await page.evaluate(MEASURE);
  const panel = await page.evaluate(() => {
    const cands = Array.from(document.querySelectorAll('div,span,h1,h2,h3'))
      .filter(el => (el.textContent ?? '').trim() === 'Чаты' && el.getBoundingClientRect().width > 120) as HTMLElement[];
    if (!cands.length) return null;
    let p = cands[0];
    while (p.parentElement && p.getBoundingClientRect().height < 260) p = p.parentElement!;
    const r = p.getBoundingClientRect();
    return { x: r.x, y: r.y, w: r.width, h: r.height };
  });
  console.log('QA10-PANEL chats', JSON.stringify(panel));
  expect(panel, 'панель «Чаты» открылась').not.toBeNull();
  await shot(page, 'child-1280-panel');
  const cap = await page.evaluate(MEASURE);
  // Панель прилегает к СВОЕЙ капсуле (у «Чаты» — левая, панельная рельса «Файлы…» может быть правой)
  const capChats = cap.railCapsuleChats;
  expect(capChats, 'капсула кнопки «Чаты» найдена').not.toBeNull();
  const capRightCh = capChats.x + capChats.w;
  const panelRight = panel.x + panel.w;
  // Панель и капсула «Чаты»: без пересечения и без дыры — расстояние между интервалами по X
  const sep = Math.max(capChats.x - panelRight, panel.x - capRightCh);
  expect(Math.round(sep), `панель «Чаты» вплотную к своей рельсе без нахлёста (разнос ${sep}px)`).toBeGreaterThanOrEqual(-2);
  // 8 — штатный RAIL_GAP между капсулой рельсы и зоной панелей (RailCapsule.tsx), +2 запас
  expect(Math.round(sep), `панель без дыры (разнос ${sep} ≤ 10)`).toBeLessThanOrEqual(10);
  await page.evaluate(() => (document.querySelector('[aria-label^="Скрыть"]') as HTMLElement)?.click());
  await page.waitForTimeout(600);

  // На 360 ничего не поехало: вход заново в мобильной раскладке
  await railChecks(360);

  // Взрослый контроль: рельса админа осталась 32 на 768/1280
  await loginViaUi(page, 'admin', ADMIN_PW, true);
  // Hash-SPA диплинк на проект обрабатывает только при полной загрузке страницы
  await page.evaluate(id => { location.hash = `#/project/${id}`; location.reload(); }, adminProjectId);
  await page.waitForTimeout(2500);
  await shot(page, 'adult-workspace');
  for (const w of [768, 1280]) {
    await page.setViewportSize({ width: w, height: 820 });
    await page.waitForTimeout(900);
    const m = await page.evaluate(MEASURE);
    console.log(`QA10-MEASURE adult ${w}`, JSON.stringify(m));
    // «Чаты» у взрослого — кнопка шапки (40), в рельсе взрослого меряем остальное
    const adult = ['Файлы', 'Задачи', 'Ещё'].filter(n => m.buttons[n]);
    expect(adult.length, `у взрослого найдены кнопки рельсы на ${w}`).toBeGreaterThan(1);
    for (const n of adult) {
      expect(m.buttons[n].w, `взрослая «${n}» ширина 32±2 (факт ${m.buttons[n].w})`).toBeGreaterThanOrEqual(30);
      expect(m.buttons[n].w, `взрослая «${n}» ширина 32±2 (факт ${m.buttons[n].w})`).toBeLessThanOrEqual(34);
      expect(m.buttons[n].h, `взрослая «${n}» высота 32±2 (факт ${m.buttons[n].h})`).toBeGreaterThanOrEqual(30);
      expect(m.buttons[n].h, `взрослая «${n}» высота 32±2 (факт ${m.buttons[n].h})`).toBeLessThanOrEqual(34);
    }
    await shot(page, `adult-${w}`);
  }
});

test('Г12: отказ загрузки настроек виден на списке разделов 360, «Повторить» чинит, на 1280 не задвоен', async ({ page }) => {
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 360, height: 780 });
  await loginViaUi(page, 'admin', ADMIN_PW);
  // Перехват ДО открытия окна: любой GET child-profile отдаёт 500
  await openUserManagement(page);
  // Статусы строк грузят child-profile: перехват ставим ПОСЛЕ их загрузки,
  // иначе кнопки «Настроить занятия» не появится вовсе (статус упадёт 500)
  await expect(page.getByTestId('child-settings-open').first()).toBeVisible({ timeout: 20_000 });
  await page.route('**/api/users/*/child-profile', route =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ code: 'internal', message: 'QA-10: имитация отказа сервера' }) }));
  await openChildSettings(page, CHILD);
  await page.waitForTimeout(1200);
  await shot(page, 'g12-360-error');
  // Отказ виден СРАЗУ на экране списка разделов, кнопка «Повторить» на месте, список цел
  const err = page.getByTestId('child-settings-load-error');
  await expect(err).toBeVisible();
  expect(await err.count(), 'отказ не задвоен уже на 360').toBe(1);
  await expect(page.getByTestId('child-settings-retry')).toBeVisible();
  await expect(page.getByTestId('child-settings-nav-list')).toBeVisible();

  // Сняли перехват — «Повторить» загружает окно
  await page.unroute('**/api/users/*/child-profile');
  await page.getByTestId('child-settings-retry').click();
  await expect(page.getByTestId('child-settings-load-error')).toHaveCount(0, { timeout: 20_000 });
  await expect(page.getByTestId('child-settings-nav-list')).toBeVisible();
  await shot(page, 'g12-360-loaded');

  // Десктоп: тот же отказ — ровно одна плашка
  await page.setViewportSize({ width: 1280, height: 820 });
  await page.waitForTimeout(600);
  await page.route('**/api/users/*/child-profile', route =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ code: 'internal', message: 'QA-10: имитация отказа сервера' }) }));
  // Закрыть и открыть окно заново
  await page.locator('.cc-modal-card, .cc-sheet-card').last().getByRole('button', { name: 'Закрыть' }).last().click({ force: true }).catch(() => {});
  await page.waitForTimeout(600);
  await openUserManagement(page);
  await openChildSettings(page, CHILD);
  await page.waitForTimeout(1200);
  const errD = page.getByTestId('child-settings-load-error');
  await expect(errD.first()).toBeVisible();
  expect(await errD.count(), 'на 1280 отказ ровно один').toBe(1);
  await page.unroute('**/api/users/*/child-profile');
});

test('Г13: удалённый опекун чистится каскадом, правка профиля не блокирована, граница держится', async () => {
  test.setTimeout(300_000);
  // REST-цепочка: назначить qa10h опекуном → удалить его → проверить каскад и правку
  let put = await putChildProfile({ guardianUserIds: [guardId, guard2Id] });
  expect(put.status, 'назначение qa10h опекуном').toBeLessThan(300);
  const del = await call(request, 'delete', `/api/users/${guard2Id}`, tok);
  expect(del.status, 'удаление опекуна').toBe(204);
  guard2Id = '';
  // Каскад: мёртвый id вычтен из живого профиля сразу при удалении учётки
  const get = await call(request, 'get', `/api/users/${childId}/child-profile`, tok);
  expect(get.status).toBe(200);
  const after = get.body?.profile?.guardianUserIds ?? [];
  expect(after, 'каскад: мёртвый id вычтен из профиля').toEqual([guardId]);
  const subj = get.body?.profile?.subjects ?? [];
  expect(subj.map((s: any) => s.id), 'предметы пережили удаление опекуна').toEqual([SUBJECT]);
  // Правка профиля после удаления проходит (200)
  put = await putChildProfile({ guardianUserIds: [guardId] });
  expect(put.status, `правка профиля после удаления (${JSON.stringify(put.body)})`).toBeLessThan(300);

  // UI-часть — отдельным тестом ниже
});

test('Г13-UI: редактор опекунов работает после удаления пользователя', async ({ page }) => {
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 1280, height: 820 });
  await loginViaUi(page, 'admin', ADMIN_PW);
  await openUserManagement(page);
  await openChildSettings(page, CHILD);
  await expect(page.getByTestId('child-settings-load-error')).toHaveCount(0, { timeout: 20_000 });
  // Раздел «Безопасность и согласие»: на десктопе таб, на мобиле ряд списка
  const safetyTab = page.getByRole('tab', { name: 'Безопасность и согласие' });
  const safetyNav = page.getByTestId('child-settings-nav-safety');
  if (await safetyTab.count() > 0) await safetyTab.click();
  else await safetyNav.click();
  await expect(page.getByTestId('child-safety-section')).toBeVisible({ timeout: 10_000 });
  // Живой опекун guardId в списке, мёртвых нет
  await expect(page.getByTestId(`guardian-${guardId}`)).toBeVisible();
  // Добавляем guard3 (живой) через Select + «Добавить», затем «Сохранить»
  const sel = page.getByTestId('guardian-add').locator('select').first();
  const val = await sel.evaluate((el, u) => {
    const s = el as HTMLSelectElement;
    const opt = Array.from(s.options).find(o => o.textContent?.includes(u));
    return opt ? opt.value : '';
  }, GUARD3);
  expect(val, `опция ${GUARD3} в списке добавления`).not.toBe('');
  await sel.selectOption(val);
  await page.getByTestId('guardian-add-btn').click();
  // Строка появляется в ЧЕРНОВИКЕ до сохранения; после «Сохранить» — с сервера
  await page.getByTestId(`guardian-${guard3Id}`).waitFor({ state: 'visible', timeout: 10_000 }).catch(() => {});
  const saveBtn = page.getByTestId('child-settings-save');
  // Кнопка в footer модалки перерисовывается (dirty-флаг) — DOM-клик вместо actionability
  await saveBtn.evaluate((el: HTMLElement) => el.click());
  await expect(page.getByTestId('child-settings-save-error')).toHaveCount(0, { timeout: 15_000 });
  await expect(page.getByTestId(`guardian-${guard3Id}`)).toBeVisible({ timeout: 15_000 });
  await shot(page, 'g13-guardians');
  // Граница REST: заведомо несуществующий id — 400 guardian_not_found
  const fresh = crypto.randomUUID();
  const bad = await putChildProfile({ guardianUserIds: [guardId, fresh] });
  expect(bad.status, `граница: свежий id (${JSON.stringify(bad.body)})`).toBe(400);
  expect(bad.body?.code, 'код границы guardian_not_found').toBe('guardian_not_found');
  // Снять guard3 обратно кнопкой строки — и сохранить
  await page.getByTestId(`guardian-remove-${guard3Id}`).click();
  await page.waitForTimeout(600);
  await saveBtn.evaluate((el: HTMLElement) => el.click());
  await expect(page.getByTestId('child-settings-save-error')).toHaveCount(0, { timeout: 15_000 });
  const fin = await call(request, 'get', `/api/users/${childId}/child-profile`, tok);
  expect((fin.body?.profile?.guardianUserIds ?? []), 'guard3 снят, список опекунов вернулся к guardId').toEqual([guardId]);
});
