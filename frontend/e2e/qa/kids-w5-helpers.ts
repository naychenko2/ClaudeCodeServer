// Общие хелперы приёмки волны 5 «Опекуны и раскладка» (QA-8).
// Стенд: http://127.0.0.1:5500, worktree kids-guardians, ветка feat/kids-guardians-ui.
// Учётки: admin/masha — мастер-пароль стенда (Auth:DevPassword), qa_w5_* — с паролем.

import { expect, type APIRequestContext, type Page } from '@playwright/test';

export const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5500';

export const ADMIN_U = 'admin';
export const ADMIN_PW = process.env.E2E_PASS || 'dev-master';
export const CHILD_U = 'masha';
export const CHILD_PW = process.env.E2E_PASS || 'dev-master';
export const GUARD_U = 'qa_w5_guard';
export const GUARD_PW = 'Guardqa12345';
export const CHILD2_U = 'qa_w5_child';   // профиль без согласия
export const CHILD2_PW = 'Childqa12345';
export const CHILD3_U = 'qa_w5_child2';  // пустой профиль (без предметов)
export const CHILD3_PW = 'Childqa12345';

export async function call(
  req: APIRequestContext, method: string, url: string, token: string, data?: unknown,
): Promise<{ status: number; body: any }> {
  const r = await (req as any)[method](url, {
    headers: { Authorization: `Bearer ${token}` },
    ...(data === undefined ? {} : { data }),
  });
  const text = await r.text();
  let body: any = undefined;
  try { body = text ? JSON.parse(text) : undefined; } catch { body = text; }
  return { status: r.status(), body: body ?? null };
}

export async function tokenOf(req: APIRequestContext, username: string, password: string): Promise<string> {
  const r = await req.post('/api/auth/login', { data: { username, password } });
  expect(r.ok(), `логин ${username} (${r.status()})`).toBeTruthy();
  return (await r.json()).token as string;
}

/** Вход в приложение токеном. clean=true — снести прошлый открытый проект (ребёнку нужен пикер). */
export interface EnterOpts {
  // Роль и id: приложение берёт роль ИЗ ХРАНИЛИЩА (App.tsx:161 — cc_role), а не из /me,
  // поэтому без этих ключей админ не увидит «Пользователей» в меню аватара.
  role?: string;
  userId?: string;
  username?: string;
  // Снести открытый проект (cc_open_project): без этого ребёнок вернётся в проект,
  // а не к экрану выбора предмета.
  clean?: boolean;
}

export async function enter(page: Page, token: string, opts: EnterOpts = {}) {
  // addInitScript принимает ОДИН аргумент-подстановку, поэтому роли/токен
  // уезжают одним объектом (раньше было три аргумента — tsc ругался TS2554).
  await page.addInitScript(({ t, o }: { t: string; o: EnterOpts }) => {
    localStorage.setItem('cc_token', t);
    localStorage.setItem('__probe', 'yes');
    if (o.role) localStorage.setItem('cc_role', o.role);
    if (o.userId) localStorage.setItem('cc_user_id', o.userId);
    if (o.username) localStorage.setItem('cc_username', o.username);
    if (o.clean) localStorage.removeItem('cc_open_project');
  }, { t: token, o: opts });
}

/** Настоящий вход через форму: init-скрипт на этом стенде не выживает (токен сносит инициализация). */
export async function loginViaUi(page: Page, username: string, password: string, clean = false) {
  await page.goto('/#/home');
  await page.waitForTimeout(1500);
  if (clean) {
    await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
    await page.reload();
    await page.waitForTimeout(2500);
  }
  const userInput = page.locator('input[placeholder="Имя пользователя"]').first();
  await expect(userInput, 'форма входа видна').toBeVisible({ timeout: 20000 });
  // Вход под rate-limit: попытка может вернуть 429 («слишком часто»). Тогда ждём и
  // повторяем — иначе тест падает на «Неверное имя пользователя или пароль».
  for (let attempt = 1; attempt <= 4; attempt++) {
    await userInput.fill(username);
    await page.locator('input[placeholder="Пароль"]').first().fill(password);
    // При isError (429 rate-limit) кнопка НЕ рендерится — клик ловит таймаут,
    // перезагружаем страницу и пробуем заново
    const clicked = await page.getByRole('button', { name: /Войти|Вхожу/ }).first()
      .click({ force: true, timeout: 8000 })
      .then(() => true)
      .catch(() => false);
    if (!clicked) {
      console.log('QA-DEBUG-LOGIN', attempt, 'кнопка скрыта (isError) — перезагрузка и повтор');
      await page.reload();
      await page.waitForTimeout(2500);
      continue;
    }
    await page.waitForTimeout(6000);
    const stillForm = await page.locator('input[placeholder="Имя пользователя"]').count();
    console.log('QA-DEBUG-LOGIN', attempt, stillForm > 0 ? 'форма ещё на экране' : 'вошли');
    if (stillForm === 0) { await page.waitForTimeout(1500); return; }
    const text = await page.locator('body').innerText();
    console.log('QA-DEBUG-LOGIN-TEXT', text.slice(0, 300));
    if (attempt === 4) throw new Error('вход не удался');
    await page.waitForTimeout(20000);
  }
}

/** Все не-2xx ответы страницы. */
export function badResponses(page: Page): string[] {
  const out: string[] = [];
  page.on('response', r => {
    const s = r.status();
    if (s >= 400) out.push(`${s} ${r.request().method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')}`);
  });
  return out;
}

/** Замер интерфейса: тач-цели <40px, горизонтальное переполнение, обрезанный текст. */
export async function uiProbe(page: Page, w: number): Promise<any> {
  return page.evaluate((vw) => {
    const small: string[] = [];
    let smallCount = 0;
    for (const el of Array.from(document.querySelectorAll('button,a,[role="button"],input,textarea,select,[role="tab"],[role="menuitem"]'))) {
      const r = (el as HTMLElement).getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      if (r.width < 40 || r.height < 40) {
        smallCount++;
        if (small.length < 10) small.push(`${(el.getAttribute('aria-label') || el.getAttribute('title') || (el.textContent ?? '').trim().slice(0, 30))} ${Math.round(r.width)}x${Math.round(r.height)}`);
      }
    }
    let clipped = 0;
    const clippedSamples: string[] = [];
    for (const el of Array.from(document.querySelectorAll('*'))) {
      const he = el as HTMLElement;
      const cs = getComputedStyle(he);
      if (cs.textOverflow === 'ellipsis' && he.scrollWidth > he.clientWidth + 1 && he.textContent?.trim()) {
        clipped++;
        if (clippedSamples.length < 5) clippedSamples.push((he.textContent ?? '').trim().slice(0, 40));
      }
    }
    return {
      viewport: vw,
      docScrollWidth: document.documentElement.scrollWidth,
      horizontalOverflow: document.documentElement.scrollWidth > vw,
      smallTargets: smallCount,
      smallSamples: small,
      clippedText: clipped,
      clippedSamples,
    };
  }, w);
}

/** Открыть модалку «Пользователи» из меню аватара (админ). */
export async function openUserManagement(page: Page) {
  const avatar = page.locator('[role="button"][aria-label^="Меню пользователя"]').first();
  if (await avatar.count() === 0) {
    console.log('QA-DEBUG-AVATAR-MISSING', (await page.locator('body').innerText()).slice(0, 400));
    console.log('QA-DEBUG-ROLES', JSON.stringify(await page.locator('[role="button"]').evaluateAll(
      els => els.map(e => (e.getAttribute('aria-label') ?? e.textContent ?? '').slice(0, 40)).slice(0, 20))));
    throw new Error('триггер меню пользователя не найден');
  }
  // Клик по САМОМУ элементу, а не по координатам: поверх шапки висит тост, и
  // force-клик по точке уходил в тост — меню не открывалось (пункт «Пользователи»
  // так и не появлялся, тест висел до потолка).
  await avatar.evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(1000);
  console.log('QA-DEBUG-MENU', (await page.locator('body').innerText()).slice(0, 500));
  const usersItem = page.getByRole('button', { name: 'Пользователи', exact: true }).first();
  await expect(usersItem, 'в меню аватара есть пункт «Пользователи»').toBeVisible({ timeout: 15000 });
  await usersItem.evaluate((el: HTMLElement) => el.click());
  await expect(page.locator('text=Пользователи').first()).toBeVisible({ timeout: 15000 });
  await page.waitForTimeout(1500);
}

/** Открыть окно настроек детского режима конкретного ребёнка (по логину в подписи окна). */
export async function openChildSettings(page: Page, login: string) {
  const buttons = page.locator('[data-testid="child-settings-open"]');
  // Статус детского режима строки грузится асинхронно — кнопка появляется после него
  await expect(buttons.first(), 'в списке пользователей есть кнопка «Настроить занятия»')
    .toBeVisible({ timeout: 20_000 });
  const n = await buttons.count();
  // Логин — ТОЧНОЕ слово: 'qa_w5_child' входит в 'qa_w5_child2', и по includes
  // открывалось окно другого ребёнка (правка уходила в чужой профиль).
  const loginRe = new RegExp(`(^|\\s)${login.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}($|\\s)`);
  for (let i = 0; i < n; i++) {
    await buttons.nth(i).click({ force: true });
    await page.waitForTimeout(1500);
    // окно настроек — верхняя модалка (последняя в DOM)
    const dialog = page.locator('.cc-modal-card, .cc-sheet-card').last();
    const text = await dialog.innerText().catch(() => '');
    if (text.includes('Настройки детского режима') && loginRe.test(text)) {
      return true;
    }
    // окно не того ребёнка — закрываем
    await page.locator('.cc-modal-card, .cc-sheet-card').last().getByRole('button', { name: 'Закрыть' }).last()
      .click({ force: true }).catch(async () => { await page.keyboard.press('Escape').catch(() => {}); });
    await page.waitForTimeout(900);
  }
  throw new Error(`окно настроек ребёнка ${login} не открылось`);
}
