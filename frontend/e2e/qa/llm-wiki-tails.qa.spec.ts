// QA-2 · LLM-wiki хвосты: перепроверка W2-1, W2-2, W4-1, W4-2, W4-4 (задача c20322c3).
// Один спек на группу сценариев (docs/operations/qa-stand.md). Фикстуры — данные стенда
// (чаты, страницы, пакеты, журнал, _health), код продукта не трогается. Всё засеянное —
// с префиксом `qa-wl-`, уборка в afterAll.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, collectConsoleErrors, login, seed, type Api } from './helpers';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';

const PREFIX = 'qa-wl-';
// Корень проекта от файла спека: frontend/e2e/qa → корень. Данные стенда start_stand —
// bin/Debug/net10.0/data (DataPath, см. qa-stand.md).
const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const DATA = path.join(ROOT, 'backend', 'ClaudeHomeServer', 'bin', 'Debug', 'net10.0', 'data');
// Свидетельства для отчёта — скриншоты в .cc-attachments (вне дерева проекта, в git не едет).
const SHOTS = path.join(ROOT, '.cc-attachments', 'qa-llm-wiki-tails');
fs.mkdirSync(SHOTS, { recursive: true });

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let ownerId = '';
let wikiRoot = '';
let projectId = '';
let sectionKey = '';
let folder = ''; // папка раздела на диске: «Проекты/{id}/»
const chatIds: Record<string, string> = {};
const chatNames: Record<string, string> = {};
const stamp = Date.now();

const sha256 = (s: string) => crypto.createHash('sha256').update(s, 'utf8').digest('hex');
const guid = () => crypto.randomUUID();

// ─── Формы фикстур — по контрактам бэка (WikiPackage/WikiManifest/WikiFrontmatter) ───

// Страницы фикстуры — без утверждений: канонические четыре секции пустые
// (WikiPage.SectionNames). Утверждений нет — сносок не нужно; непустая «Сводка»
// без сноски на карточку была бы отказом гейта.
const pageFile = (title: string) => `---
type: topic
status: active
updated: 2026-11-02
---

# ${title}

## Сводка

## Хронология

## Противоречия

## История
`;

// Карточка источника: шапка + заголовок, БЕЗ секций (WikiPage: «у карточки источника
// не может быть секций» — с секцией парсер не отдаёт модель, и FindSourceCards её не видит).
// ref — форма гейта 'session:{id}#m{a}-{b}' (тот же формат, что пишет WikiSourceCapture).
const cardFile = (title: string, chatRef: string) => `---
type: source
origin: session
ref: ${chatRef}#m1-2
hash: ${sha256('qa-wl-source-text')}
captured: 2026-11-01T22:00:00+00:00
---

# ${title}
`;

const pkgFile = (id: string, chatId: string, pageRel: string, status: string, decision: string, reason?: string) => JSON.stringify({
  v: 1, id, section: sectionKey,
  sources: [{ origin: 'session', ref: `session:${chatId}` }],
  operations: [{
    op: 'create', page: pageRel, text: `Тестовое утверждение «${pageRel}».`,
    class: 'session×create', decision, ...(reason ? { reason } : {}), baseHashes: {},
  }],
  status, createdAt: '2026-11-01T22:00:00+00:00', updatedAt: '2026-11-01T22:05:00+00:00',
});

const logLine = (pkgId: string, chatId: string, pageRel: string) => JSON.stringify({
  ts: '2026-11-01T22:05:01+00:00', op: 'apply', section: sectionKey,
  pages: [pageRel], source: `session:${chatId}`, actor: 'keeper', policy: 'auto', packages: [pkgId],
});

const write = (rel: string, content: string) => {
  const full = path.join(wikiRoot, rel);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, content, 'utf8');
};

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  ownerId = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).sub as string;
  expect(fs.existsSync(DATA), `каталог данных стенда должен существовать: ${DATA}`).toBeTruthy();

  // Флаг llm-wiki тестовому пользователю (тот же путь, что в QA-П1 после M-1).
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });

  seeded = seed(client);
  const project = await seeded.project(`${PREFIX}p-${stamp}`, `${os.tmpdir()}/${PREFIX}${stamp}`);
  projectId = project.id;
  sectionKey = `projects/${projectId}`;
  folder = `Проекты/${projectId}/`;
  wikiRoot = path.join(DATA, 'wiki', ownerId);

  for (const key of ['wall', 'w1', 'w2', 'w3', 'p1']) {
    const name = `${PREFIX}${key}-${stamp}`;
    const s = await seeded.session(projectId, name);
    chatIds[key] = s.id;
    chatNames[key] = name;
  }

  // История чата «wall» — чтобы «Разобрать этот чат» был включён (hasMessages).
  // Ключ history.json до первого ответа агента — id самого чата (SessionManager.cs:537).
  const histDir = path.join(DATA, 'sessions', chatIds.wall);
  fs.mkdirSync(histDir, { recursive: true });
  fs.writeFileSync(path.join(histDir, 'history.json'), JSON.stringify([
    { kind: 'user_message', text: 'QA-WL: проверяю действия вики в шапке чата', timestamp: Date.now() },
    { kind: 'user_message', text: 'QA-WL: вторая тестовая реплика', timestamp: Date.now() + 1 },
  ]), 'utf8');

  // Вики заводим сразу (тот же путь, что кнопка «Завести вики»).
  await client.post('/api/wiki/initialize');

  // Фикстура «Журнала»: applied-пакеты с прообразом, записью состояния и строкой apply —
  // 1 / 2 / 5 пакетов на три чата (формы тоста) + строка с неизвестным op.
  const counts: Record<string, number> = { w1: 1, w2: 2, w3: 5 };
  const manifest: Record<string, string> = {};
  const logs: string[] = [];
  let pageSeq = 0;
  for (const key of ['w1', 'w2', 'w3']) {
    for (let i = 0; i < counts[key]; i++) {
      pageSeq += 1;
      const title = `Правила отката ${pageSeq}`;
      const pageRel = `${folder}${title}.md`;
      const content = pageFile(title);
      write(`pages/${pageRel}`, content);
      manifest[pageRel] = sha256(content);
      const id = guid();
      write(`keeper/packages/${id}.json`, pkgFile(id, chatIds[key], pageRel, 'applied', 'auto'));
      write(`keeper/preimages/${id}/${pageRel}`, ''); // прообраз пуст: страницы до пакета не было (create)
      write(`keeper/applied/${id}.json`, JSON.stringify({ [pageRel]: sha256(content) }));
      logs.push(logLine(id, chatIds[key], pageRel));
    }
  }
  // Карточка источника и proposed-пакет с операцией create: сценарий «принять → открыть → откатить».
  const cardRel = `${folder}Источники/чат QA-P1.md`;
  const p1Ref = `session:${chatIds.p1}`;
  const cardContent = cardFile(`Чат ${chatNames.p1}`, p1Ref);
  write(`pages/${cardRel}`, cardContent);
  manifest[cardRel] = sha256(cardContent);
  const p1PageRel = `${folder}Правила очереди.md`; // страницы на диске нет: её создаст применение
  const p1Id = guid();
  write(`keeper/packages/${p1Id}.json`, pkgFile(p1Id, chatIds.p1, p1PageRel, 'proposed', 'inbox', 'тестовая причина: проверка решения человека'));

  // Строка с неизвестным op: нейтральная подпись, экран не падает.
  logs.push(JSON.stringify({
    ts: '2026-11-01T22:06:00+00:00', op: 'triage-experiment', section: sectionKey,
    pages: [], source: 'note:qa-wl-unknown', actor: 'keeper', policy: null, packages: [],
  }));
  write('keeper/log.jsonl', logs.join('\n') + '\n');
  write(`keeper/manifest/${sectionKey.replace('/', '__')}.json`, JSON.stringify(manifest));

  // Фикстура «Здоровья»: отчёт с разделом «Не проверено» и нулём находок.
  write(`pages/${folder}_health.md`, `# Здоровье раздела

Проверено: 2026-11-01T22:00:00Z. Код: 1.0.0+qa-fixture

## Находки (0)

## Не проверено
- страниц без фактов (находки «нет источников» и «без изменений» их не видят): 3
`);

  // Чат на стену — колонка «Стены» при широком окне (W2-1).
  await client.put('/api/me/wall', { chatIds: [chatIds.wall] });
});

test.afterAll(async () => {
  // Уборка: стена пуста, флаг снят, проекты (каскадом чаты), затем файлы фикстур.
  try { await client?.put('/api/me/wall', { chatIds: [] }); } catch { /* best-effort */ }
  try { await client?.put('/api/feature-flags/llm-wiki', { enabled: false }); } catch { /* best-effort */ }
  await seeded?.cleanup();
  for (const id of Object.values(chatIds)) fs.rmSync(path.join(DATA, 'sessions', id), { recursive: true, force: true });
  fs.rmSync(wikiRoot, { recursive: true, force: true });
  await request?.dispose();
});

const openApp = async (page: import('@playwright/test').Page, context: import('@playwright/test').BrowserContext, hash: string) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(hash);
};

const wikiOpen = async (page: import('@playwright/test').Page) => {
  await page.locator('[aria-label="Вики"]').click();
};

const selectSection = async (page: import('@playwright/test').Page, key: string) => {
  await page.locator(`[data-testid="wiki-section-row"][title="${key}"]`).click();
};

const countDots = (trigger: import('@playwright/test').Locator) => trigger.locator('span[style*="border-radius: 50%"]');

// Тосты непрочитанных уведомаций (живые данные стенда) ложатся поверх шапки и перехватывают
// клики: перед действиями в шапке закрываем их штатной кнопкой.
const dismissToasts = async (page: import('@playwright/test').Page) => {
  const close = page.locator('button[title="Закрыть"]');
  try { await close.first().waitFor({ state: 'visible', timeout: 4000 }); } catch { return; }
  // dispatchEvent, не click: карточки тостов при снятии перестраиваются и click
  // бесконечно ждёт «стабильности». Это вспомогательная уборка чужих уведомлений,
  // не проверяемое поведение.
  for (let i = 0; i < 12; i++) {
    if (await close.count() === 0) break;
    await close.first().dispatchEvent('click').catch(() => {});
    await page.waitForTimeout(200);
  }
};

// Шум окружения тестового стенда: прогрев историй запрашивает дерево файлов по всем проектам
// (lib/sync.ts:552), включая старые тестовые проекты прошлых прогонов, чьи /tmp-каталоги уже
// снесены, — они дают 404. К фиче вики это не относится: проверяем, что запросы вики и сама
// страница не дают ошибок.
const NET404_TEXT = '[error] Failed to load resource: the server responded with a status of 404 (Not Found)';
const trackNet404 = (page: import('@playwright/test').Page) => {
  const net404: string[] = [];
  page.on('response', r => { if (r.status() === 404) net404.push(`${r.request().method()} ${r.url()}`); });
  return net404;
};
const expectNoWikiErrors = (errors: string[], net404: string[], expected: string[] = []) => {
  const unexpected = net404.filter(u => !u.includes('/files/tree?') && !expected.some(a => u.includes(a)));
  expect(unexpected, '404 вне шума окружения и ожидаемых запросов сценария нет').toEqual([]);
  expect(errors.filter(e => e !== NET404_TEXT), 'ошибок консоли, кроме 404, нет').toEqual([]);
};

// ─── W2-1 · «Стена» узкая колонка: меню «Вики» вместо ряда; точка одна ───

test.describe('W2-1 при окне 1280', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

test('W2-1a. Стена при окне 1280: вместо ряда — одна кнопка-меню «Вики»; после отката ровно одна точка', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const net404 = trackNet404(page);
  await openApp(page, context, '/#/wall');
  await dismissToasts(page);

  const trigger = page.locator('button[title="Действия «Вики» в этом чате"]');
  await expect(trigger, 'в колонке «Стены» при широком окне — ровно один триггер «Вики»').toHaveCount(1);
  await expect(page.locator('[data-testid="wiki-chat-analyze"]'), 'ряда из трёх кнопок нет').toHaveCount(0);
  await expect(page.locator('[data-testid="wiki-chat-retract"]'), 'ряда из трёх кнопок нет').toHaveCount(0);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, 'горизонтального переполнения документа нет').toBe(0);

  await trigger.click();
  const sheet = page.locator('.cc-sheet-card');
  await expect(sheet, 'меню открывается').toHaveCount(1);
  await expect(sheet.getByRole('menuitem', { name: 'Разобрать этот чат' }), 'действие «Разобрать» на месте').toHaveCount(1);
  await expect(sheet.getByRole('menuitem', { name: 'Откатить всё из этого чата' }), 'действие «Откатить» на месте').toHaveCount(1);
  await expect(countDots(trigger), 'до действия — точки на триггере нет').toHaveCount(0);
  await sheet.getByRole('menuitem', { name: 'Откатить всё из этого чата' }).click();

  await expect(page.getByText('Откатить всё из этого чата?'), 'подтверждение отката').toBeVisible();
  await page.getByRole('button', { name: 'Откатить всё', exact: true }).click();

  // Исход чата без пакетов — «ничего не изменилось» — это сообщение: точка одна, не две.
  await expect(countDots(trigger), 'на закрытом триггере ровно одна точка (не двойная)').toHaveCount(1);
  await trigger.click();
  await expect(sheet.getByText('ничего не изменилось'), 'итог отката виден подписью пункта').toBeVisible();
  await page.screenshot({ path: path.join(SHOTS, 'w2-1-wall-1280-menu-dot.png') });
  await page.keyboard.press('Escape');

  expectNoWikiErrors(errors, net404);
});
});

test.describe('W2-1 при окне 360', () => {
  test.use({ viewport: { width: 360, height: 720 } });

test('W2-1b. Обычный чат при окне 360: одна кнопка-меню «Вики», действия доступны, точка одна', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const net404 = trackNet404(page);
  // «Стена» на телефон не ставит колонок по замыслу (WallPage: active = w > MOBILE_MAX),
  // поэтому узкая раскладка на 360 проверяется обычным чатом проекта (тот же триггер).
  await openApp(page, context, `/#/project/${projectId}/chat/${chatIds.wall}`);
  await dismissToasts(page);

  const trigger = page.locator('button[title="Действия «Вики» в этом чате"]');
  await expect(trigger).toHaveCount(1);
  await expect(page.locator('[data-testid="wiki-chat-analyze"]')).toHaveCount(0);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBe(0);

  await trigger.click();
  const sheet = page.locator('.cc-sheet-card');
  await expect(sheet.getByRole('menuitem', { name: 'Разобрать этот чат' })).toHaveCount(1);
  await expect(sheet.getByRole('menuitem', { name: 'Откатить всё из этого чата' })).toHaveCount(1);
  const box = await trigger.boundingBox();
  expect(box!.height, 'цель касания триггера ≥40 px').toBeGreaterThanOrEqual(40);
  await sheet.getByRole('menuitem', { name: 'Откатить всё из этого чата' }).click();
  await page.getByRole('button', { name: 'Откатить всё', exact: true }).click();
  await expect(countDots(trigger), 'ровно одна точка').toHaveCount(1);

  expectNoWikiErrors(errors, net404);
});
});

// ─── W2-2 · «Уведомления»: точка на кнопке «Фильтр» с подписью ───

test.describe('W2-2', () => {
  test.use({ viewport: { width: 360, height: 720 } });

test('W2-2. «Уведомления» на 360: при активном фильтре точка на кнопке «Фильтр», без фильтра — нет', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const net404 = trackNet404(page);
  await openApp(page, context, '/#/notifications');
    await dismissToasts(page);
  const filterBtn = page.locator('button:has-text("Фильтр")');
  await expect(filterBtn, 'кнопка «Фильтр» с подписью на месте').toHaveCount(1);
  await expect(countDots(filterBtn), 'без фильтра точки нет').toHaveCount(0);

  await filterBtn.click();
  await page.getByRole('menuitem', { name: 'Непрочитанные' }).click();
  await expect(countDots(filterBtn), 'при активном фильтре ровно одна точка').toHaveCount(1);
  await page.screenshot({ path: path.join(SHOTS, 'w2-2-notifications-filter-dot.png') });

  await filterBtn.click();
  await page.getByRole('menuitem', { name: 'Все' }).click();
  await expect(countDots(filterBtn), 'после сброса точки нет').toHaveCount(0);

  expectNoWikiErrors(errors, net404);
});
});

// ─── W4-2 · «Разделы» на 360: тач-цель «Назад»; закрытая страница не пишет свой отказ ───

test.describe('W4-2a на 360', () => {
  test.use({ viewport: { width: 360, height: 720 } });

test('W4-2a. Экран «Разделы» на 360: «Назад» с целью касания ≥40 px; возврат к каталогу работает', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const net404 = trackNet404(page);
  await openApp(page, context, '/#/notes');
    await dismissToasts(page);
  await wikiOpen(page);

  await expect(page.locator('[data-testid="wiki-section-switch"]'), 'кнопка «Раздел ▸» есть').toHaveCount(1);
  await page.locator('[data-testid="wiki-section-switch"]').click();

  const back = page.locator('[data-testid="wiki-sections-back"]');
  await expect(back, 'кнопка «Назад» на экране «Разделы»').toHaveCount(1);
  const box = await back.boundingBox();
  expect(box!.height, 'цель касания «Назад» ≥40 px по высоте').toBeGreaterThanOrEqual(40);
  expect(box!.width, 'цель касания «Назад» ≥40 px по ширине').toBeGreaterThanOrEqual(40);
  await page.screenshot({ path: path.join(SHOTS, 'w4-2-sections-360-back.png') });

  // Свой замер по логике ui_probe на области экрана «Разделы»: строки разделов — основная
  // поверхность экрана — и переполнение. Заголовки групп дерева (сворачивание секций) —
  // штатные контролы сайдбара продукта: вынесены из строгой проверки, но меряются и
  // дописываются к отчёту как находка.
  const scan = await page.evaluate(() => {
    const out = { rows: [] as string[], heads: [] as string[], overflow: 0 };
    out.overflow = document.documentElement.scrollWidth - document.documentElement.clientWidth;
    const scope = document.querySelector('[data-testid="wiki-sections-back"]')?.closest('div')?.parentElement;
    const nodes = scope ? scope.querySelectorAll('button, [role="button"], input, select') : [];
    for (const el of nodes) {
      const r = el.getBoundingClientRect();
      const rec = `${el.tagName} ${Math.round(r.width)}x${Math.round(r.height)}`;
      if (el.getAttribute('data-testid') === 'wiki-section-row') { if (r.height < 40 || r.width < 40) out.rows.push(rec); }
      else if (el.hasAttribute('aria-expanded')) out.heads.push(rec);
      else if (r.width < 40 || r.height < 40) out.rows.push(rec);
    }
    return out;
  });
  expect(scan.overflow, 'горизонтального переполнения нет').toBe(0);
  expect(scan.rows, 'строк экрана «Разделы» с целью меньше 40 px нет').toEqual([]);
  if (scan.heads.length) console.log(`НАХОДКА W4-2a: заголовки групп дерева на экране «Разделы» (${scan.heads.length}): ${scan.heads.join(', ')} — штатные сворачиваемые секции сайдбара`);

  await back.click();
  await expect(back, '«Назад» возвращает к каталогу').toHaveCount(0);
  await expect(page.locator('[data-testid="wiki-section-switch"]'), 'каталог снова на месте').toHaveCount(1);

  expectNoWikiErrors(errors, net404);
});
});

test('W4-2b. Открыл страницу и ушёл в другой раздел, пока она читается: поздний отказ не всплывает над новым разделом', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await openApp(page, context, '/#/notes');
    await dismissToasts(page);
  await wikiOpen(page);
  await selectSection(page, sectionKey);

  // Запрос страницы держим и отвечаем 404 поздно — «пока открывается другая».
  let releaseLate: () => void = () => {};
  const late = new Promise<void>(r => { releaseLate = r; });
  await page.route('**/api/wiki/page?**', async route => {
    await late;
    await route.fulfill({ status: 404, contentType: 'application/json', body: JSON.stringify({ code: 'wiki_page_key', message: 'Страница не найдена' }) });
  });

  await page.locator('[data-testid="wiki-page-row"]', { hasText: 'Правила отката 1' }).click();
  await expect(page.locator('[data-testid="wiki-section-row"]').first()).toBeVisible(); // экран ещё жив

  // Осознанная смена раздела закрывает читаемую страницу.
  await selectSection(page, 'common');
  await expect(page.getByText('В разделе пока нет страниц'), 'каталог нового раздела жив').toBeVisible();

  releaseLate(); // поздний отказ закрытой страницы
  await page.waitForTimeout(1000);
  await expect(page.locator('[data-testid="wiki-error"]'), 'ошибка закрытой страницы не всплывает над новым разделом').toHaveCount(0);
  await expect(page.getByText('В разделе пока нет страниц'), 'новый раздел на экране').toBeVisible();

  // 404 в консоли — только мои же mock-ответы перехваченных запросов страницы, не сбои приложения.
  const realErrors = errors.filter(e => !e.includes('404 (Not Found)'));
  expect(realErrors, 'иных ошибок консоли нет').toEqual([]);
});

// ─── W4-4 · «Журнал»: согласование чисел тоста, «из этого чата», неизвестный op ───

test('W4-4a. «Журнал»: откат всего из чата — тост «1 пакет / 2 пакета / 5 пакетов», подпись «из этого чата», неизвестный op назван нейтрально', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const net404 = trackNet404(page);
  await openApp(page, context, '/#/notes');
    await dismissToasts(page);
  await wikiOpen(page);
  await page.locator('[aria-label="Журнал"]').click();

  const cases: Array<[string, string, string]> = [
    [chatNames.w1, '1 применённая запись, 1 стр.', '1 пакет откатан'],
    [chatNames.w2, '2 применённые записи, 2 стр.', '2 пакета откатано'],
    [chatNames.w3, '5 применённых записей, 5 стр.', '5 пакетов откатано'],
  ];
  for (const [name, dialogCount, toastForm] of cases) {
    // у чата с N пакетами — N записей применения: берём первую, откат накрывает все окна
    await page.locator('[data-testid="wiki-journal-row"]', { hasText: name }).first().click();
    const retractBtn = page.locator('[data-testid="wiki-journal-retract"]');
    await expect(retractBtn, 'подпись кнопки — родительный падеж «из этого чата»').toHaveText('Откатить всё из этого чата');
    await retractBtn.click();
    await expect(page.getByText('Откатить всё из этого чата?'), 'заголовок диалога — «из этого чата»').toBeVisible();
    await expect(page.getByText(dialogCount), `диалог считает применённые записи честно: ${dialogCount}`).toBeVisible();
    await page.getByRole('button', { name: 'Откатить всё', exact: true }).click();
    // прежний тост ещё не рассосался: проверяем самый свежий
    await expect(page.getByText('Откат по источнику', { exact: true }).last(), 'тост отката').toBeVisible();
    await expect(page.getByText(toastForm).last(), `тост согласован по числу: ${toastForm}`).toBeVisible();
    if (toastForm === '5 пакетов откатано') await page.screenshot({ path: path.join(SHOTS, 'w4-4-journal-toast-5.png') });
    // после отката «Журнал» сам возвращается к списку: отдельный «Назад» не нужен
  }

  // Неизвестное событие — нейтральная подпись, не сырое слово; экран не падает.
  await expect(page.locator('[data-testid="wiki-journal-row"]', { hasText: 'другое событие' }), 'неизвестный op подписан нейтрально').toHaveCount(1);
  await expect(page.getByText('triage-experiment'), 'сырое слово op в списке не показывается').toHaveCount(0);

  expectNoWikiErrors(errors, net404);
});

// ─── W4-4 · «Здоровье»: «Не проверено», подпись «вся вики», «Нет данных» вместо отказа ───

test('W4-4b. «Здоровье»: блок «Не проверено» и нет фразы «проверка ничего не нашла»; запуск без отчёта — «Нет данных»', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const net404 = trackNet404(page);
  await openApp(page, context, '/#/notes');
    await dismissToasts(page);
  await wikiOpen(page);
  await selectSection(page, sectionKey);
  await page.locator('[aria-label="Здоровье"]').click();

  await expect(page.locator('[data-testid="wiki-health-unchecked"]'), 'блок «Не проверено» виден').toHaveCount(1);
  await expect(page.getByText('Не проверено страниц без фактов: 3')).toBeVisible();
  await expect(page.getByText('Среди проверенных страниц находок нет'), 'пустой отчёт назван по непроверенным страницам').toBeVisible();
  await page.screenshot({ path: path.join(SHOTS, 'w4-4-health-unchecked.png') });
  await expect(page.locator('body'), 'фразы «проверка ничего не нашла» при непроверенных страницах нет').not.toContainText('Последняя проверка ничего не нашла');

  // Раздел без отчёта: «Нет данных» с подписью про всю вики, а не «отказ».
  await selectSection(page, 'common');
  await expect(page.getByText('Нет данных'), '404 отчёта — «Нет данных», не ошибка экрана').toBeVisible();
  await expect(page.getByText('проверяет всю вики'), 'подпись «вся вики» на месте').toBeVisible();

  const resp = page.waitForResponse(r => r.url().includes('/api/wiki/lint'));
  await page.locator('[data-testid="wiki-health-run"]').click();
  await resp;
  await page.waitForTimeout(500);
  await expect(page.getByText('Нет данных'), 'после прогона без отчёта раздела — по-прежнему «Нет данных»').toBeVisible();
  await expect(page.locator('body'), '«отказа» прогона нет').not.toContainText('Не удалось начать проверку');
  await expect(page.locator('body')).not.toContainText('Проверка этого раздела не завершилась');

  // 404 отчёта «Здоровья» для раздела без отчёта — ожидаемый запрос сценария (экран «Нет данных»).
  expectNoWikiErrors(errors, net404, ['/api/wiki/health']);
});

// ─── W4-1 · Регрессия: принять пакет → открыть страницу → откатить → страница не остаётся ───

test('W4-1. Принял пакет, открыл страницу, откатил источник — страница не остаётся на экране', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const net404 = trackNet404(page);
  await openApp(page, context, '/#/notes');
    await dismissToasts(page);
  await wikiOpen(page);

  // Принять пакет в «Входящих» — страница создаётся настоящим применением.
  await page.locator('[aria-label="Входящие"]').click();
  await page.locator('[data-testid="wiki-pkg-row"]', { hasText: 'тестовая причина' }).click();
  await page.locator('[data-testid="wiki-op-accept-0"]').click();
  await page.waitForTimeout(1000);

  await page.locator('[aria-label="Страницы"]').click();
  await selectSection(page, sectionKey);
  const row = page.locator('[data-testid="wiki-page-row"]', { hasText: 'Правила очереди' });
  await expect(row, 'после принятия страница появилась в каталоге').toHaveCount(1);
  await row.click();
  await expect(page.locator('[data-testid="wiki-page"]'), 'страница открыта').toBeVisible();

  // Откат по источнику из «Журнала» — страница на диске снимается.
  await page.locator('[aria-label="Журнал"]').click();
  await page.locator('[data-testid="wiki-journal-row"]', { hasText: chatNames.p1 }).click();
  await page.locator('[data-testid="wiki-journal-retract"]').click();
  await page.getByRole('button', { name: 'Откатить всё', exact: true }).click();
  await expect(page.getByText('Откат по источнику', { exact: true }), 'тост отката').toBeVisible();
  // после отката «Журнал» сам возвращается к списку

  // Возврат к «Страницам»: открытой страницы быть не должно — ни её содержимого, ни её строки.
  await page.locator('[aria-label="Страницы"]').click();
  await expect(page.locator('[data-testid="wiki-page"]'), 'страница не остаётся на экране после отката').toHaveCount(0);
  await expect(page.locator('[data-testid="wiki-page-row"]', { hasText: 'Правила очереди' }), 'страницы нет и в каталоге').toHaveCount(0);
  // Снимок экрана после отката — свидетельство нового дефекта: вместо каталога раздела
  // остаётся вечный скелетон (openedPage в компоненте жив, а стор после 404 перечитки
  // страницу снял → WikiTab рисует LoadingSkeleton). Оформлено отдельным дефектом
  // (зафиксировано прогоном: строк каталога 0, пустого состояния нет, «Раздел не выбран»
  // нет, центр экрана пуст). Здесь проверяется то, что требует постановка: страница
  // со своим содержимым и её строка в каталоге — не остаются.
  await page.screenshot({ path: path.join(SHOTS, 'w4-1-after-retract.png') });

  // 404 перечитывания снятой страницы — ожидаемый механизм сценария (стор снимает её из открытых).
  expectNoWikiErrors(errors, net404, ['/api/wiki/page']);
});
