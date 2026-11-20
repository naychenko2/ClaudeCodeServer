import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { login, api } from './helpers';

// QA-1 · выкат 1 фичи «LLM-wiki: связи и укрупнение страниц (очередь 2)».
// Стенд из worktree feat/wiki-queue2 (:5500), вики засеяна копией корпуса 19.11:
// раздел A — 16 страниц раздела YuE2 (аудио) + синтетическая страница со ссылками,
// раздел B — 5 страниц (изоляция), раздел C — одна страница без связей (пустое состояние).
// Скриншоты — в .cc-attachments/wiki-q2-qa1/shots/.

const SHOTS = '/home/an/Sources/ClaudeCodeServer-wiki-queue2/.cc-attachments/wiki-q2-qa1/shots';
const A = 'ffb8fa06-0095-4ea3-ba00-38af6d324bb4';
const B = 'a69a5d00-f31f-45b1-bbe5-edd2426a8f97';
const C = 'de047127-380b-4add-806a-21c3593a4a7b';
const SEC = { A: `projects/${A}`, B: `projects/${B}`, C: `projects/${C}` };
const LINKS_PAGE = 'Проверка ссылок вики (QA)';
const TOPIC_PAGE = 'Каверы: Кино «Пачка сигарет»';
const EMPTY_PAGE = 'Одиночная страница без связей (QA)';
const B_PAGE = 'Правило выкатки по готовности';
const T = { timeout: 20000 };

let request: APIRequestContext;
let token: string;

test.describe.configure({ mode: 'serial' });
test.setTimeout(240_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});

test.afterAll(async () => { await request?.dispose(); });

async function openNotes(page: Page, context: any) {
  page.setDefaultTimeout(20000);
  await context.addInitScript((tk: string) => localStorage.setItem('cc_token', tk), token);
  await page.goto('/#/notes');
  await page.waitForTimeout(5000);
  let tree = page.locator('[data-testid="wiki-unified-tree"]');
  if (!(await tree.count())) {
    const nav = page.locator('button', { hasText: 'Заметки' }).first();
    if (await nav.count()) { await nav.click(T); await page.waitForTimeout(7000); }
  }
  tree = page.locator('[data-testid="wiki-unified-tree"]');
  if (!(await tree.count())) {
    // На узкой ширине панель «Заметки» отдаёт дерево по кнопке, а режим «Вики» — переключателем
    const wikiMode = page.locator('button', { hasText: /^Вики/ }).first();
    if (await wikiMode.count()) { await wikiMode.click(T); await page.waitForTimeout(6000); }
    const openTree = page.locator('[data-testid="wiki-open-tree"]');
    if (await openTree.count()) { await openTree.first().click(T); await page.waitForTimeout(6000); }
  }
  await expect(page.locator('[data-testid="wiki-unified-tree"]')).toBeVisible({ timeout: 30000 });
}

// Открыть страницу вики по заголовку из дерева.
async function openPage(page: Page, title: string) {
  const row = page.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: title }).first();
  await expect(row, `в дереве есть страница «${title}»`).toBeVisible({ timeout: 20000 });
  await row.click(T);
  await expect(page.locator('[data-testid="wiki-page"]')).toBeVisible({ timeout: 20000 });
  await page.waitForTimeout(800);
}

async function titleOf(page: Page): Promise<string> {
  return (await page.locator('[data-testid="wiki-page"] h2').first().innerText()).trim();
}

test('ссылки [[…]] в тексте страницы: разрешённые — кликабельны, боковые и несуществующие — плоский текст', async ({ page, context }) => {
  await openNotes(page, context);
  await openPage(page, LINKS_PAGE);
  expect(await titleOf(page)).toBe(LINKS_PAGE);

  const linkButtons = page.locator('[data-testid="wiki-link"]');
  const count = await linkButtons.count();
  const labels = await linkButtons.allInnerTexts();
  test.info().annotations.push({ type: 'note', description: `кнопок-ссылок: ${count}; подписи: ${JSON.stringify(labels)}` });

  // 1) обычная ссылка, 2) ссылка с подписью «|подпись», 3) дубль той же ссылки, 4) ссылка в другой секции текста
  expect(count, 'в тексте пять кликабельных ссылок').toBe(5);
  expect(labels.filter(l => l === 'YuE2: режим cover').length, 'дубль ссылки остаётся кликабельным').toBe(2);
  expect(labels, 'ссылка с подписью показывает подпись').toContain('параметры strength');
  expect(labels, 'ссылка без подписи показывает цель').toContain('YuE2: обязательность слов');

  // Боковая ссылка (чужой раздел) и несуществующая — плоский текст, без кнопки
  const bodyText = await page.locator('[data-testid="wiki-page"]').innerText();
  expect(bodyText, 'боковая ссылка осталась текстом').toContain(B_PAGE);
  expect(bodyText, 'несуществующая ссылка осталась текстом').toContain('Страница которой нет');
  expect(await page.locator('[data-testid="wiki-link"]', { hasText: B_PAGE }).count(), 'боковая ссылка не кликабельна').toBe(0);
  expect(await page.locator('[data-testid="wiki-link"]', { hasText: 'Страница которой нет' }).count(), 'мертвая ссылка не кликабельна').toBe(0);
  expect(bodyText, 'скобки ссылок убраны из текста').not.toContain('[[');

  await page.screenshot({ path: `${SHOTS}/wiki-q2-links-1280.png`, fullPage: true });

  // Клик по ссылке открывает целевую страницу
  await linkButtons.filter({ hasText: 'YuE2: режим cover' }).first().click(T);
  await page.waitForTimeout(2000);
  const afterTitle = await titleOf(page);
  test.info().annotations.push({ type: 'note', description: `после клика: заголовок «${afterTitle}»` });
  expect(afterTitle, 'клик по [[…]] открыл целевую страницу').toBe('YuE2: режим cover');
  await page.screenshot({ path: `${SHOTS}/wiki-q2-link-target-1280.png` });
});

test('группа «По теме»: подпись, пункты и переход по пункту', async ({ page, context }) => {
  await openNotes(page, context);
  await openPage(page, TOPIC_PAGE);
  const group = page.locator('[data-testid="wiki-related-group"][data-kind="topic"]');
  await expect(group, 'есть группа «По теме»').toBeVisible({ timeout: 20000 });
  const header = (await group.locator('> div').first().innerText()).trim();
  const items = await group.locator('[data-testid="wiki-related-item"]').allInnerTexts();
  test.info().annotations.push({ type: 'note', description: `подпись группы: «${header}»; пунктов: ${items.length}: ${JSON.stringify(items)}` });
  expect(header, 'подпись группы «По теме»').toContain('По теме');
  expect(items.length, 'в группе есть пункты').toBeGreaterThan(0);
  expect(items.length, 'пунктов не больше потолка (5)').toBeLessThanOrEqual(5);

  // Пункт «По теме» ведёт на страницу соседа
  await page.screenshot({ path: `${SHOTS}/wiki-q2-topic-block-1280.png`, fullPage: true });
  // Блок «Связанные» живёт во внутренней прокрутке панели — снимок самого блока
  await page.locator('[data-testid="wiki-related"]').scrollIntoViewIfNeeded();
  await page.waitForTimeout(300);
  await page.locator('[data-testid="wiki-related"]').screenshot({ path: `${SHOTS}/wiki-q2-related-block-1280.png` });
  const first = group.locator('[data-testid="wiki-related-item"]').first();
  const label = (await first.innerText()).split('\n')[0].trim();
  const key = await first.getAttribute('data-page-key');
  await first.click(T);
  await page.waitForTimeout(2000);
  const afterTitle = await titleOf(page);
  test.info().annotations.push({ type: 'note', description: `переход по пункту «${label}» (${key}) → «${afterTitle}»` });
  expect(afterTitle, 'пункт «По теме» открыл страницу соседа').not.toBe(TOPIC_PAGE);
  await page.screenshot({ path: `${SHOTS}/wiki-q2-topic-1280.png`, fullPage: true });
});

test('пустое состояние: страница без связей — ни «По теме», ни ссылок', async ({ page, context }) => {
  await openNotes(page, context);
  await openPage(page, EMPTY_PAGE);
  expect(await titleOf(page)).toBe(EMPTY_PAGE);
  expect(await page.locator('[data-testid="wiki-link"]').count(), 'кликабельных ссылок нет').toBe(0);
  expect(await page.locator('[data-testid="wiki-related-group"][data-kind="topic"]').count(), 'группы «По теме» нет').toBe(0);
  const related = await page.locator('[data-testid="wiki-related"]').count();
  test.info().annotations.push({ type: 'note', description: `блок «Связанные» на странице без связей: ${related}` });
  await page.screenshot({ path: `${SHOTS}/wiki-q2-empty-1280.png`, fullPage: true });
});

test('мобильная ширина 360: ссылки на месте, горизонтального переполнения нет', async ({ page, context }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await openNotes(page, context);
  await openPage(page, LINKS_PAGE);
  const linkButtons = page.locator('[data-testid="wiki-link"]');
  await expect(linkButtons.first(), 'ссылка видна на 360').toBeVisible({ timeout: 20000 });
  const labels = await linkButtons.allInnerTexts();
  test.info().annotations.push({ type: 'note', description: `на 360 кнопок-ссылок: ${labels.length}` });
  expect(labels.length, 'на 360 ссылки те же').toBe(5);
  const overflow = await page.evaluate(() => ({
    doc: document.documentElement.scrollWidth,
    win: window.innerWidth,
  }));
  test.info().annotations.push({ type: 'note', description: `ширина документа ${overflow.doc}, окна ${overflow.win}` });
  expect(overflow.doc, 'нет горизонтального переполнения на 360').toBeLessThanOrEqual(overflow.win + 1);
  await page.screenshot({ path: `${SHOTS}/wiki-q2-links-360.png`, fullPage: true });
});

test('API: изоляция разделов — чужие заголовки и пути не утекают', async () => {
  const client = api(request, token);
  const pageA = `Проекты/${A}/${LINKS_PAGE}.md`;
  const resA = await client.get<any>(`/api/wiki/page?section=${encodeURIComponent(SEC.A)}&page=${encodeURIComponent(pageA)}`);

  // links[]: разрешённые — свой раздел, боковые — null/null
  const links = (resA.links ?? []) as Array<{ text: string; path: string | null; section: string | null }>;
  test.info().annotations.push({ type: 'note', description: `links[]: ${JSON.stringify(links)}` });
  for (const l of links) {
    if (l.path === null) {
      expect(l.section, `неразрешённая ссылка «${l.text}» — section тоже null`).toBeNull();
    } else {
      expect(l.path, `цель «${l.text}» — свой раздел`).toContain(`Проекты/${A}/`);
      expect(l.section, `цель «${l.text}» — свой раздел`).toBe(SEC.A);
    }
  }
  const sideways = links.find(l => l.text === B_PAGE);
  expect(sideways, 'ссылка на страницу чужого раздела есть в links[]').toBeTruthy();
  expect(sideways!.path, 'боковая ссылка — path null').toBeNull();
  expect(sideways!.section, 'боковая ссылка — section null').toBeNull();
  const dead = links.find(l => l.text === 'Страница которой нет');
  expect(dead!.path, 'несуществующая ссылка — path null').toBeNull();

  // related[]: только свой раздел
  const related = (resA.related ?? []) as Array<{ kind: string; items: Array<{ pageKey: string; section?: string }> }>;
  for (const g of related) {
    for (const it of g.items) {
      expect(it.section, `пункт «${it.pageKey}» — свой раздел`).toBe(SEC.A);
      expect(it.pageKey, `пункт «${it.pageKey}» — свой раздел`).toContain(`Проекты/${A}/`);
    }
  }
  // заголовков страниц чужого раздела в выдаче нет.
  // Проверяем ПУТИ и РАЗДЕЛЫ целей (и заголовки найденных соседей) — текст самой ссылки
  // («[[Правило выкатки по готовности]]») принадлежит странице A: это её собственный текст,
  // а не утечка чужой страницы; утечкой был бы её path/section.
  const bTitles = (await client.get<any>(`/api/wiki/section?section=${encodeURIComponent(SEC.B)}`)).pages
    .map((p: any) => p.title);
  const payloadA = JSON.stringify({
    linkTargets: links.map(l => ({ path: l.path, section: l.section })),
    related: related.map(g => ({ kind: g.kind, items: g.items })),
  });
  for (const t of bTitles) {
    expect(payloadA.includes(t), `заголовок чужого раздела «${t}» не утёк в links[]/related[]`).toBe(false);
  }
  expect(payloadA.includes('a69a5d00'), 'id чужого раздела не утёк в выдачу').toBe(false);

  // Обратная сторона: страница чужого раздела тоже не видит наш раздел
  const pageB = `Проекты/${B}/${B_PAGE}.md`;
  const resB = await client.get<any>(`/api/wiki/page?section=${encodeURIComponent(SEC.B)}&page=${encodeURIComponent(pageB)}`);
  const relatedB = (resB.related ?? []) as Array<{ kind: string; items: Array<{ pageKey: string; section?: string }> }>;
  for (const g of relatedB)
    for (const it of g.items)
      expect(it.pageKey, `пункт раздела B — свой раздел`).toContain(`Проекты/${B}/`);
  const aTitles = (await client.get<any>(`/api/wiki/section?section=${encodeURIComponent(SEC.A)}`)).pages
    .map((p: any) => p.title);
  const payloadB = JSON.stringify({
    linkTargets: (resB.links ?? []).map((l: any) => ({ path: l.path, section: l.section })),
    related: relatedB.map(g => ({ kind: g.kind, items: g.items })),
  });
  for (const t of aTitles)
    expect(payloadB.includes(t), `заголовок раздела A «${t}» не утёк в выдачу B`).toBe(false);
  expect(payloadB.includes('ffb8fa06'), 'id раздела A не утёк в выдачу B').toBe(false);

  // Пара (раздел, страница) проверяется: страница A под разделом B — отказ
  const wrong = await request.get(
    `/api/wiki/page?section=${encodeURIComponent(SEC.B)}&page=${encodeURIComponent(pageA)}`,
    { headers: { Authorization: `Bearer ${token}` } });
  test.info().annotations.push({ type: 'note', description: `страница A под разделом B → ${wrong.status()}` });
  expect(wrong.status(), 'чужой раздел для страницы отвергается').toBe(400);
});
