// QA-приёмка фичи «Заметки+Вики: один раздел, который ведут человек и ИИ вместе»
// (ADR-038, ветка feat/notes-wiki-unified). Стенд поднимается из worktree этой ветки,
// флаги llm-wiki и notes-wiki-unified включаются у тестового пользователя (docs/operations/qa-stand.md).
//
// Фикстура данных готовится самим спеком (reseed) — файлы страниц вики пишет только Хранитель,
// через REST их не создать. Состав:
//   Личное/Замеры локальной GPU.md — страница «вместе»: утверждение ИИ (сноска s1) и человека
//     (сноска s2), «История» с прежней формулировкой человека, секция «Заметки»;
//     в журнале — строка apply с overrodeHuman и пакет supersede@human («ИИ заменил моё»).
//   Личное/Тест правки.md и Личное/Тест правки 2.md — страницы под правку человека
//     (утверждение ИИ + утверждение человека).
//   Личное/Замеры MiniMax H3.md — страница без секции «Заметки» (empty-state секции).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5500';
const MAIN_PAGE = 'Замеры локальной GPU';
const EDIT_PAGE = 'Тест правки';
const EDIT_PAGE_2 = 'Тест правки 2';
const ADD_PAGE = 'Тест добавления';
const CHRONICLE_PAGE = 'Хроника чата QA';
const DISPUTE_PAGE = 'Тест спора';
const DISPUTE_PKG_ID = 'ccddeeff-eeff-0011-2233-445566778899';
const AI_CARD = 'ИИ-разбор 111111';
const HUMAN_CARD = 'Правка человека 222222';
const PKG_ID = 'aabbccdd-eeff-0011-2233-445566778899';
const INBOX_PKG_ID = 'bbccddee-eeff-0011-2233-445566778899';

test.describe.configure({ mode: 'serial' });

// Ошибки консоли без сетевого шума стенда: обрыв соединения и нерезолвящийся внешний
// ресурс (ERR_*) — не дефект интерфейса, а сеть машины. Отказы API с кодами (4xx/5xx)
// ошибками консоли не считаются — их ловит проверка текста на экране.
function uiErrors(sink: string[]): string[] {
  // Отказы API (4xx) консоль пишет «Failed to load resource» — их проверяет текст на экране.
  return sink.filter(t => !t.includes('net::ERR_') && !t.includes('Failed to load resource'));
}


let request: APIRequestContext;
let token: string;
let client: Api;

async function setFlags(unified: boolean, wiki = true): Promise<void> {
  await client.put('/api/feature-flags/llm-wiki', { enabled: wiki });
  await client.put('/api/feature-flags/notes-wiki-unified', { enabled: unified });
}

// ─── Фикстура ───

const CARD_AI = `---
type: source
origin: session
ref: session:qa11111111#m1-m9
hash: ai-card-hash-1111
captured: 2026-11-02T05:48:00.0000000+00:00
aliases: []
owns: []
sources: []
status: active
---

# ${AI_CARD}
`;

const CARD_HUMAN = `---
type: source
origin: human
ref: human:qa22222222
hash: hu-card-hash-2222
captured: 2026-11-01T09:14:00.0000000+00:00
aliases: []
owns: []
sources: []
status: active
---

# ${HUMAN_CARD}
`;

const PAGE_MAIN = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${MAIN_PAGE}

## Сводка
Черновики снимаем на локальной GPU, а не в облаке. [^s1]
Очередь локальной GPU обслуживает Тимур. [^s2]

## Хронология

## Противоречия

## История
Черновики снимаем на локальной GPU. [^s3]

## Заметки
Очередь GPU держим вручную: Тимур смотрит по утрам.

[^s1]: [[${AI_CARD}]] — «Черновики снимаем на локальной GPU, а не в облаке.»
[^s2]: [[${HUMAN_CARD}]] — «Очередь локальной GPU обслуживает Тимур.»
[^s3]: [[${HUMAN_CARD}]] — «Черновики снимаем на локальной GPU.»
`;

function editPage(title: string): string {
  return `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${title}

## Сводка
ИИ написал это утверждение про очередь GPU. [^s1]
Вы написали это утверждение про очередь GPU. [^s2]

## Хронология

## Противоречия

## История

[^s1]: [[${AI_CARD}]] — «ИИ написал это утверждение про очередь GPU.»
[^s2]: [[${HUMAN_CARD}]] — «Вы написали это утверждение про очередь GPU.»
`;
}

// Страница под «＋ Добавить утверждение» (дефект 02125be1): «Сводка» с утверждением ИИ
// и «История», которая в редакторе только для чтения.
const PAGE_ADD = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${ADD_PAGE}

## Сводка
ИИ записал это утверждение про очередь GPU. [^s1]

## Хронология

## Противоречия

## История
Прежняя формулировка про очередь GPU. [^s2]

[^s1]: [[${AI_CARD}]] — «ИИ записал это утверждение про очередь GPU.»
[^s2]: [[${HUMAN_CARD}]] — «Прежняя формулировка про очередь GPU.»
`;

const HUMAN_STATEMENT = 'Вы записали это утверждение про очередь GPU.';
const DISPUTE_TEXT = 'Очередь GPU обслуживают посменно, а не один человек.';

// Страница под спор «ИИ оспаривает ваше утверждение» (конфликт пакета conflict@human):
// состояние собирается фикстурой (пакет + строка журнала), как и пакет замены в первом круге.
const PAGE_DISPUTE = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${DISPUTE_PAGE}

## Сводка
${HUMAN_STATEMENT} [^s2]

## Хронология

## Противоречия
${DISPUTE_TEXT} [^s1]

## История

[^s1]: [[${AI_CARD}]] — «${DISPUTE_TEXT}»
[^s2]: [[${HUMAN_CARD}]] — «${HUMAN_STATEMENT}»
`;

// Страница-хроника (type: chronicle): правки нет, и вместо плашки про Хранителя —
// своё объяснение (дефект a16c6167).
const PAGE_CHRONICLE = `---
type: chronicle
summary: хроника чата QA
status: active
---

# ${CHRONICLE_PAGE}

## О чём
Обсуждали очередь локальной GPU.

## Что решили
- Лимит очереди держим вручную.

## Что выяснили
- Локальная модель быстрее облака.

## Открытые вопросы
- Когда поднимать лимит?
`;

// Пересобрать фикстуру на диске стенда: страницы, карточки, пакет ИИ и строку журнала.
// Требуется потому, что проверки правки и возврата меняют страницу — без пересборки
// повторный прогон спека видел бы уже изменённое состояние.
async function wikiRoot(): Promise<string> {
  const me = await client.get<{ userId: string }>('/api/auth/me');
  const root = path.resolve(process.cwd(), '../../..', 'backend/ClaudeHomeServer/bin/Debug/net10.0/data/wiki', me.userId);
  return existsSync(root) ? root : '';
}

async function reseed(): Promise<void> {
  const root = await wikiRoot();
  if (!root) return; // стенд с другим DataPath — фикстуры не трогаем
  const pages = path.join(root, 'pages', 'Личное');
  const src = path.join(pages, 'Источники');
  mkdirSync(src, { recursive: true });
  const write = (file: string, text: string): string => {
    writeFileSync(file, text, 'utf8');
    return createHash('sha256').update(text, 'utf8').digest('hex');
  };
  const hashes: Record<string, string> = {};
  hashes[`Личное/Источники/${AI_CARD}.md`] = write(path.join(src, `${AI_CARD}.md`), CARD_AI);
  hashes[`Личное/Источники/${HUMAN_CARD}.md`] = write(path.join(src, `${HUMAN_CARD}.md`), CARD_HUMAN);
  hashes[`Личное/${MAIN_PAGE}.md`] = write(path.join(pages, `${MAIN_PAGE}.md`), PAGE_MAIN);
  hashes[`Личное/${EDIT_PAGE}.md`] = write(path.join(pages, `${EDIT_PAGE}.md`), editPage(EDIT_PAGE));
  hashes[`Личное/${EDIT_PAGE_2}.md`] = write(path.join(pages, `${EDIT_PAGE_2}.md`), editPage(EDIT_PAGE_2));
  hashes[`Личное/${ADD_PAGE}.md`] = write(path.join(pages, `${ADD_PAGE}.md`), PAGE_ADD);
  hashes[`Личное/${CHRONICLE_PAGE}.md`] = write(path.join(pages, `${CHRONICLE_PAGE}.md`), PAGE_CHRONICLE);
  hashes[`Личное/${DISPUTE_PAGE}.md`] = write(path.join(pages, `${DISPUTE_PAGE}.md`), PAGE_DISPUTE);

  // Страница раздела «Общее» — для пробы отказа гейта (ссылка вниз, в «Личное»)
  const commonDir = path.join(root, 'pages', 'Общее');
  mkdirSync(commonDir, { recursive: true });
  const commonPage = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# Общая страница

## Сводка
Общее утверждение про очередь GPU. [^s1]

## Хронология

## Противоречия

## История

[^s1]: [[${AI_CARD}]] — «Общее утверждение про очередь GPU.»
`;
  const commonHash = write(path.join(commonDir, 'Общая страница.md'), commonPage);
  const commonManifestPath = path.join(root, 'keeper/manifest/common.json');
  const commonManifest = existsSync(commonManifestPath)
    ? JSON.parse(readFileSync(commonManifestPath, 'utf8')) as Record<string, string>
    : {};
  commonManifest['Общее/Общая страница.md'] = commonHash;
  writeFileSync(commonManifestPath, JSON.stringify(commonManifest), 'utf8');

  const manifestPath = path.join(root, 'keeper/manifest/personal.json');
  const manifest = existsSync(manifestPath)
    ? JSON.parse(readFileSync(manifestPath, 'utf8')) as Record<string, string>
    : {};
  Object.assign(manifest, hashes);
  writeFileSync(manifestPath, JSON.stringify(manifest), 'utf8');

  // Пакет ИИ: замена формулировки человека (supersede@human), применённая автоматом
  const pkg = {
    v: 1, id: PKG_ID, author: 'ai', section: 'personal',
    sources: [{ origin: 'session', ref: 'session:qa11111111#m1-m9' }],
    operations: [{
      op: 'supersede', page: `Личное/${MAIN_PAGE}.md`, pageSection: 'Сводка',
      target: 'Черновики снимаем на локальной GPU.',
      text: 'Черновики снимаем на локальной GPU, а не в облаке.',
      class: 'session×supersede@human', decision: 'auto', reason: null, baseHashes: {},
      inputTokens: null, outputTokens: null, humanDecision: null, humanReason: null,
    }],
    status: 'applied', statusReason: null,
    createdAt: '2026-11-02T05:48:00.0000000+00:00', updatedAt: '2026-11-02T05:48:00.0000000+00:00',
  };
  mkdirSync(path.join(root, 'keeper/packages'), { recursive: true });
  writeFileSync(path.join(root, 'keeper/packages', `${PKG_ID}.json`), JSON.stringify(pkg), 'utf8');
  // Открытый пакет во «Входящих»: причина «Источник старше вашей правки»
  const inboxPkg = {
    v: 1, id: INBOX_PKG_ID, author: 'ai', section: 'personal',
    sources: [{ origin: 'session', ref: 'session:qa11111111#m1-m9' }],
    operations: [{
      op: 'supersede', page: `Личное/${MAIN_PAGE}.md`, pageSection: 'Сводка',
      target: 'Очередь локальной GPU обслуживает Тимур.',
      text: 'Очередь локальной GPU обслуживает команда.',
      class: 'session×supersede@human', decision: 'inbox',
      reason: 'Источник старше вашей правки: разговор от 29.10, ваша правка — сегодня в 09:14.',
      baseHashes: {}, inputTokens: null, outputTokens: null, humanDecision: null, humanReason: null,
    }],
    status: 'proposed', statusReason: null,
    createdAt: '2026-11-02T05:40:00.0000000+00:00', updatedAt: '2026-11-02T05:40:00.0000000+00:00',
  };
  writeFileSync(path.join(root, 'keeper/packages', `${INBOX_PKG_ID}.json`), JSON.stringify(inboxPkg), 'utf8');

  // Пакет ИИ: спор утверждения человека (conflict@human), применённый автоматом.
  // Строка журнала — старше строки замены, чтобы порядок «Журнала» в других сценариях не менялся.
  const disputePkg = {
    v: 1, id: DISPUTE_PKG_ID, author: 'ai', section: 'personal',
    sources: [{ origin: 'session', ref: 'session:qa11111111#m1-m9' }],
    operations: [{
      op: 'conflict', page: `Личное/${DISPUTE_PAGE}.md`, pageSection: 'Сводка',
      target: HUMAN_STATEMENT, text: DISPUTE_TEXT,
      class: 'session×conflict@human', decision: 'auto', reason: null, baseHashes: {},
      inputTokens: null, outputTokens: null, humanDecision: null, humanReason: null,
    }],
    status: 'applied', statusReason: null,
    createdAt: '2026-11-02T05:30:00.0000000+00:00', updatedAt: '2026-11-02T05:30:00.0000000+00:00',
  };
  writeFileSync(path.join(root, 'keeper/packages', `${DISPUTE_PKG_ID}.json`), JSON.stringify(disputePkg), 'utf8');

  const logLines = [
    {
      ts: '2026-11-02T05:30:05.0000000+00:00', op: 'apply', section: 'personal',
      pages: [`Личное/${DISPUTE_PAGE}.md`], source: 'session:qa11111111#m1-m9', actor: 'ai',
      policy: 'auto', packages: [DISPUTE_PKG_ID], overrodeHuman: true,
    },
    {
      ts: '2026-11-02T05:48:05.0000000+00:00', op: 'apply', section: 'personal',
      pages: [`Личное/${MAIN_PAGE}.md`], source: 'session:qa11111111#m1-m9', actor: 'ai',
      policy: 'auto', packages: [PKG_ID], overrodeHuman: true,
    },
  ];
  writeFileSync(path.join(root, 'keeper/log.jsonl'), logLines.map(l => JSON.stringify(l)).join('\n') + '\n', 'utf8');
}

// ─── Хелперы страницы ───

// Открыть раздел «Заметки» с перезагрузкой: флаги приложение читает из /api/auth/me при старте,
// а переход только по хэшу страницу не перезагружает — флаги остались бы прежними.
// Тосты перекрывают кнопки на узких экранах (уведомление о версии CLI) — перед кликами
// закрываем их штатной кнопкой «Закрыть».
async function closeToasts(page: Page): Promise<void> {
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
}

async function openApp(page: Page): Promise<void> {
  await page.goto(BASE + '/#/notes');
  await page.reload();
  await page.waitForTimeout(1500);
  await closeToasts(page);
  // Раздел мог не открыться (после reload стартовый экран иногда возвращается к чатам):
  // идём по навигации, как человек, чтобы сценарии не зависели от стартового состояния
  const inSection = await page.getByRole('button', { name: 'Вики', exact: true }).count()
    + await page.getByTestId('unified-filter').count();
  if (inSection === 0) {
    const nav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
    if (await nav.count()) { await nav.click(); await page.waitForTimeout(1500); }
  }
}

// Строка страницы вики в дереве: только kind=page — иначе под тем же заголовком есть ещё
// и строка-заметка (вики-страницы приходят и списком заметок; см. дефект D1 в отчёте).
function pageRow(page: Page, title: string) {
  return page.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: title });
}

async function openWikiPage(page: Page, title: string): Promise<void> {
  await pageRow(page, title).first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible();
}

// Снимки ключевых состояний второго круга — в тот же каталог, что и первого
const SHOTS = path.resolve(process.cwd(), '../../.cc-attachments/notes-wiki');
async function shot(p: Page, name: string): Promise<void> {
  mkdirSync(SHOTS, { recursive: true });
  await p.screenshot({ path: path.join(SHOTS, name) });
}

// Форма дерева: группы (текст заголовка со счётчиком) и их строки. Обход прямой: строки
// лежат соседями заголовка, а не внутри него — по data-testid, не по вёрстке.
async function treeShape(page: Page): Promise<{ group: string; rows: { kind: string; origin: string; title: string }[] }[] | null> {
  return page.evaluate(() => {
    const root = document.querySelector('[data-testid="wiki-unified-tree"]');
    if (!root) return null;
    const out: { group: string; rows: { kind: string; origin: string; title: string }[] }[] = [];
    let cur: { group: string; rows: { kind: string; origin: string; title: string }[] } | null = null;
    const walk = (el: Element) => {
      for (const ch of Array.from(el.children)) {
        const tid = ch.getAttribute('data-testid');
        if (tid === 'unified-group') {
          cur = { group: (ch.textContent || '').trim(), rows: [] };
          out.push(cur);
        } else if (tid === 'unified-row') {
          if (!cur) { cur = { group: '', rows: [] }; out.push(cur); }
          cur.rows.push({
            kind: ch.getAttribute('data-kind') || '',
            origin: ch.getAttribute('data-origin') || '',
            title: (ch.getAttribute('title') || '').trim(),
          });
        } else walk(ch);
      }
    };
    walk(root);
    return out;
  });
}

// Число строк-утверждений в секциях открытой страницы, содержащих текст.
// Только «li» секций: блок «Источники» цитирует утверждение второй раз по построению,
// и считать его — мера, которая сама себе врёт.
async function occurrences(page: Page, text: string): Promise<number> {
  return page.evaluate((t) => {
    const root = document.querySelector('[data-testid="wiki-page"]');
    if (!root) return -1;
    let n = 0;
    for (const sec of Array.from(root.querySelectorAll('[data-testid="wiki-section"]'))) {
      for (const li of Array.from(sec.querySelectorAll('li'))) {
        if ((li.textContent || '').includes(t)) n++;
      }
    }
    return n;
  }, text);
}

// Мобильная шторка единого дерева: режим «Вики» → кнопка дерева
async function openMobileTree(p: Page): Promise<void> {
  await p.goto(BASE + '/#/notes');
  await p.reload();
  await p.waitForTimeout(1800);
  await closeToasts(p);
  await p.getByRole('button', { name: 'Вики', exact: true }).first().click();
  await p.waitForTimeout(600);
  await p.getByTestId('wiki-open-tree').click();
  // Дерево грузит каталоги разделов само и не синхронно со списком заметок: ждём строки
  // страниц вики, а не первых строк вообще (иначе мерили бы промежуточное состояние).
  // Замер времени до строк — доказательство в отчёте.
  const t0 = Date.now();
  await expect(p.locator('[data-testid="unified-row"][data-kind="page"]').first()).toBeVisible({ timeout: 20000 });
  console.log(`WAIT-page-rows ${p.viewportSize()?.width}: ${Date.now() - t0} ms`);
}

// Что именно рисует дерево в момент замера (для разбора пустых состояний)
async function treeDiag(p: Page): Promise<unknown> {
  return p.evaluate(() => ({
    skeleton: document.querySelectorAll('[data-testid="unified-skeleton"]').length,
    empty: document.querySelectorAll('[data-testid="unified-empty"]').length,
    groups: document.querySelectorAll('[data-testid="unified-group"]').length,
    rows: document.querySelectorAll('[data-testid="unified-row"]').length,
    pageRows: document.querySelectorAll('[data-testid="unified-row"][data-kind="page"]').length,
    noteRows: document.querySelectorAll('[data-testid="unified-row"][data-kind="note"]').length,
    text: (document.querySelector('[data-testid="wiki-unified-tree"]')?.innerText || '').slice(0, 300),
  }));
}

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  token = await login(request);
  client = api(request, token);
  await setFlags(true);
  // Фикстура — перед каждым прогоном: проверки правки и возврата меняют страницы
  await reseed();
});

test.afterAll(async () => {
  await request?.dispose();
});

test('сценарий 1: флаг «Заметки и Вики» выключен — старая раскладка вики, кнопок совместной правки нет', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(false);
  await openApp(page);

  // Переключатель режимов в сайдбаре есть
  await expect(page.getByRole('button', { name: 'Вики', exact: true })).toBeVisible();

  // Единого дерева нет — старая раскладка
  await expect(page.getByTestId('wiki-unified-tree')).toHaveCount(0);
  await expect(page.getByTestId('unified-row')).toHaveCount(0);

  // Вкладки вики — в центре (старая раскладка)
  await page.getByRole('button', { name: 'Вики', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Страницы', exact: true })).toBeVisible();
  await expect(page.getByTestId('wiki-rail')).toHaveCount(0);

  // Кнопок совместной правки нет нигде
  await expect(page.getByTestId('wiki-edit-open')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Перенести в вики' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Принять как мою правку' })).toHaveCount(0);

  // llm-wiki выключен — вкладки «Вики» в разделе «Заметки» нет вовсе
  await setFlags(false, false);
  await openApp(page);
  await expect(page.getByRole('button', { name: 'Вики', exact: true })).toHaveCount(0);

  await setFlags(true);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 1: флаг включён — дерево слева, рельса справа, в центре нет вкладок и навигации', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openApp(page);

  // Единое дерево заметок и страниц вики слева
  await expect(page.getByTestId('wiki-unified-tree')).toBeVisible();
  await expect(pageRow(page, MAIN_PAGE)).toBeVisible();
  // Значки авторства в дереве
  await expect(page.getByTestId('unified-legend')).toContainText('заметка');
  await expect(page.getByTestId('unified-legend')).toContainText('ИИ');
  await expect(page.getByTestId('unified-legend')).toContainText('вместе');
  await expect(page.getByTestId('unified-legend')).toContainText('вы');

  // Переключателя режимов «Заметки | Вики» в сайдбаре нет — навигация только в боковых панелях
  await expect(page.getByRole('button', { name: 'Вики', exact: true })).toHaveCount(0);

  await openWikiPage(page, MAIN_PAGE);

  // Правая рельса разделов вики на месте
  await expect(page.getByTestId('wiki-rail')).toBeVisible();
  await expect(page.getByTestId('wiki-rail-inbox')).toBeVisible();
  await expect(page.getByTestId('wiki-rail-journal')).toBeVisible();
  await expect(page.getByTestId('wiki-rail-health')).toBeVisible();
  await expect(page.getByTestId('wiki-rail-settings')).toBeVisible();

  // В центре — только документ: вкладок каталога и кнопок правки в центре нет
  await expect(page.getByRole('button', { name: 'Страницы', exact: true })).toHaveCount(0);
  await expect(page.getByTestId('wiki-page-row')).toHaveCount(0);

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 2: страница вики — бейдж авторства, сноски, поповер источника, Esc и возврат фокуса', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openApp(page);
  await openWikiPage(page, MAIN_PAGE);

  // Бейдж авторства страницы — «вместе»
  const badge = page.getByTestId('wiki-authorship');
  await expect(badge).toHaveAttribute('data-authorship', 'together');
  await expect(badge).toContainText('Ведёте вместе');

  // Сноски: ИИ — номером, человек — «✎ …»
  const footnotes = page.getByTestId('wiki-footnote');
  await expect(footnotes).toHaveCount(3);
  await expect(footnotes.first()).toContainText('s1');
  await expect(footnotes.nth(1)).toContainText('✎');

  // Поповер ИИ-сноски: «Открыть в чате»
  await footnotes.first().click();
  const pop = page.getByTestId('wiki-footnote-popover');
  await expect(pop).toBeVisible();
  await expect(pop).toHaveAttribute('data-mode', 'popover');
  await expect(pop).toContainText('Сноска 1');
  await expect(pop.getByTestId('wiki-footnote-source')).toBeVisible();
  await expect(pop.getByTestId('wiki-footnote-open-chat')).toContainText('Открыть в чате');

  // Esc закрывает поповер, фокус возвращается на сноску
  await page.keyboard.press('Escape');
  await expect(pop).toHaveCount(0);
  await expect(footnotes.first()).toBeFocused();

  // Сноска человека: свой текст, без «Открыть в чате»
  await footnotes.nth(1).click();
  await expect(pop).toBeVisible();
  await expect(pop).toContainText('Написали вы');
  await expect(pop.getByTestId('wiki-footnote-open-chat')).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(pop).toHaveCount(0);

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 3: правка — предупреждение о формулировке ИИ, «Убрать» и «Сохранить»', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await openWikiPage(page, EDIT_PAGE);

  await page.getByTestId('wiki-edit-open').click();
  await expect(page.getByTestId('wiki-editor')).toBeVisible();

  // Правка утверждения ИИ → предупреждение «уйдёт в Историю»
  const aiField = page.getByTestId('wiki-edit-item-0').locator('textarea');
  await aiField.fill('ИИ написал это утверждение про очередь GPU — и человек его поправил.');
  await expect(page.getByTestId('wiki-edit-ai-warn-0')).toContainText('Это формулировка ИИ');
  await expect(page.getByTestId('wiki-edit-ai-warn-0')).toContainText('«Историю»');

  // «Убрать» — не удаление: спрашивает подтверждение и уводит в «Историю»
  await page.getByTestId('wiki-edit-item-1').getByRole('button', { name: 'Убрать утверждение' }).click();
  await expect(page.getByTestId('wiki-edit-remove-ask-1')).toContainText('«Историю»');
  await page.getByTestId('wiki-edit-remove-ok-1').click();
  await expect(page.getByTestId('wiki-edit-item-1')).toHaveAttribute('data-gone', 'true');

  // «Сохранить» — правка уходит на сервер
  await page.getByTestId('wiki-edit-save').click();
  await expect(page.getByTestId('wiki-editor')).toHaveCount(0, { timeout: 15000 });
  await expect(page.getByTestId('wiki-page')).toContainText('ИИ написал это утверждение про очередь GPU — и человек его поправил.');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 4: конфликт 409 в двух окнах — сравнение «Моё / ИИ / Было» и заблокированные поля', async ({ browser }) => {
  const errors: string[] = [];
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const a = await ctx.newPage();
  const b = await ctx.newPage();
  // 409 в консоли — это сам сценарий (конфликт), его в ошибки не считаем
  const push = (t: string) => { if (!t.includes('409')) errors.push(t); };
  a.on('console', m => { if (m.type() === 'error') push(m.text()); });
  b.on('console', m => { if (m.type() === 'error') push(m.text()); });

  await reseed();
  await a.goto(BASE + '/#/notes'); await a.waitForTimeout(1500);
  await openWikiPage(a, EDIT_PAGE_2);
  await a.getByTestId('wiki-edit-open').click();
  await expect(a.getByTestId('wiki-editor')).toBeVisible();
  await a.getByTestId('wiki-edit-item-0').locator('textarea').fill('Моя версия утверждения из первого окна.');

  // Второе окно сохраняет правку той же страницы — база у первого окна устарела
  await b.goto(BASE + '/#/notes'); await b.waitForTimeout(1500);
  await openWikiPage(b, EDIT_PAGE_2);
  await b.getByTestId('wiki-edit-open').click();
  await expect(b.getByTestId('wiki-editor')).toBeVisible();
  await b.getByTestId('wiki-edit-item-0').locator('textarea').fill('Правка из второго окна.');
  await b.getByTestId('wiki-edit-save').click();
  await expect(b.getByTestId('wiki-editor')).toHaveCount(0, { timeout: 15000 });

  // Первое окно сохраняет — 409 и сравнение
  await a.getByTestId('wiki-edit-save').click();
  const conflict = a.getByTestId('wiki-edit-conflict');
  await expect(conflict).toBeVisible({ timeout: 15000 });
  await expect(conflict).toContainText('Моё');
  await expect(conflict).toContainText('ИИ');
  await expect(conflict).toContainText('Было, когда вы начали');

  // Три решения и заблокированные поля на время сравнения
  await expect(a.getByTestId('wiki-conflict-take-mine')).toBeVisible();
  await expect(a.getByTestId('wiki-conflict-take-ai')).toBeVisible();
  await expect(a.getByTestId('wiki-conflict-keep')).toBeVisible();
  await expect(a.getByTestId('wiki-edit-item-0').locator('textarea')).toBeDisabled();

  // «Оставить моё» — моя правка встаёт, страница сохраняется
  await a.getByTestId('wiki-conflict-take-mine').click();
  await expect(a.getByTestId('wiki-editor')).toHaveCount(0, { timeout: 15000 });
  await expect(a.getByTestId('wiki-page')).toContainText('Моя версия утверждения из первого окна.');

  await ctx.close();
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 5: секция «Заметки» — последняя, с подписью, empty-state и правкой', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openApp(page);
  await openWikiPage(page, MAIN_PAGE);

  // Секция «Заметки» — с подписью и после «Истории»
  const notes = page.getByTestId('wiki-notes');
  await expect(notes).toBeVisible();
  await expect(notes).toContainText('свободный текст · ИИ его не меняет, но читает');
  const sectionNames = await page.getByTestId('wiki-section').evaluateAll(els => els.map(e => e.getAttribute('data-section-name')));
  expect(sectionNames[sectionNames.length - 1], 'последняя секция страницы — «История», «Заметки» идут после неё').toBe('История');
  const notesBox = await notes.boundingBox();
  const historyBox = await page.getByTestId('wiki-section').last().boundingBox();
  expect(notesBox!.y, 'секция «Заметки» идёт после «Истории»').toBeGreaterThan(historyBox!.y);

  // Правка секции заметок доступна
  await expect(page.getByTestId('wiki-notes-body')).toBeVisible();

  // На странице без секции «Заметки» — empty-state с призывом записать
  await openWikiPage(page, 'Замеры MiniMax H3');
  await expect(page.getByTestId('wiki-notes-empty')).toBeVisible();
  await expect(page.getByTestId('wiki-notes-write')).toContainText('Записать заметку');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 6: замена формулировки ИИ — метка, «Вернуть мою», журнал и настройки', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await openWikiPage(page, MAIN_PAGE);

  // Плашка «ИИ заменил вашу формулировку» и метка у утверждения
  await expect(page.getByTestId('wiki-override-replaced')).toContainText('ИИ заменил вашу формулировку');
  const tag = page.getByTestId('wiki-override-tag');
  await expect(tag).toHaveAttribute('data-kind', 'replaced');
  await expect(tag).toContainText('ИИ заменил вашу формулировку');

  // Точка на «Журнале» горит, пока замена не возвращена
  await expect(page.getByTestId('wiki-replacement-dot')).toBeVisible();

  // «Журнал»: фильтр «ИИ поменял моё» и запись о замене с действием «Вернуть мою»
  await page.getByTestId('wiki-rail-journal').click();
  await expect(page.getByText('ИИ поменял моё').first()).toBeVisible();
  await expect(page.getByTestId('wiki-journal-row').first()).toContainText('заменил');
  await expect(page.getByTestId('wiki-journal-restore')).toContainText('Вернуть мою');

  // «Настройки»: строка класса «Замена ваших формулировок»
  await page.getByTestId('wiki-rail-settings').click();
  await expect(page.getByTestId('wiki-apply-human-row')).toContainText('Замена ваших формулировок');
  await expect(page.getByTestId('wiki-apply-human-row')).toContainText('ИИ меняет ваше сам');

  // «Вернуть мою» — действие без подтверждения
  await openWikiPage(page, MAIN_PAGE);
  await page.getByTestId('wiki-restore-btn').click();
  await expect(page.getByTestId('wiki-override-replaced')).toHaveCount(0, { timeout: 15000 });
  await expect(page.getByTestId('wiki-override-tag')).toHaveCount(0);
  await expect(page.getByTestId('wiki-replacement-dot')).toHaveCount(0);
  await expect(page.getByTestId('wiki-page')).toContainText('Черновики снимаем на локальной GPU.');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 7: «Перенести в вики» из заметки — подтверждение и итог', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);

  const title = `Заметка для переноса QA ${Date.now()}`;
  await client.post('/api/notes', { title, source: 'personal', content: 'Текст заметки для переноса в вики.' });

  await openApp(page);
  await page.locator('[data-testid="unified-row"][data-kind="note"]', { hasText: title }).first().click();
  await expect(page.getByTestId('wiki-page')).toHaveCount(0);

  const transfer = page.getByRole('button', { name: 'Перенести в вики' });
  await expect(transfer).toBeVisible();
  await transfer.click();
  await expect(page.getByText('Перенести заметку в вики?')).toBeVisible();
  await page.getByRole('button', { name: 'Перенести', exact: true }).click();

  // Итог: страница открылась
  await expect(page.getByTestId('wiki-page')).toBeVisible({ timeout: 15000 });
  await expect(page.getByTestId('wiki-page')).toContainText(title);

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 8: ширины 360, 400 и 1280 — нет горизонтального переполнения', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const findings: string[] = [];

  for (const width of [360, 400, 1280]) {
    const p = await ctx.newPage();
    await p.setViewportSize({ width, height: 800 });
    await p.goto(BASE + '/#/notes');
    await p.reload();
    await p.waitForTimeout(1800);
    await closeToasts(p);
    if (width >= 768) {
      // Десктоп: открываем страницу вики из единого дерева
      await p.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
    } else {
      // Мобильные ширины: вики живёт в шторках — переключатель режимов, затем дерево
      await p.getByRole('button', { name: 'Вики', exact: true }).first().click();
      await p.waitForTimeout(600);
      const treeBtn = p.getByTestId('wiki-open-tree');
      if (await treeBtn.count()) await treeBtn.click();
    }
    await p.waitForTimeout(1000);

    const overflow = await p.evaluate(() => ({
      doc: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      body: document.body.scrollWidth - document.body.clientWidth,
    }));
    if (overflow.doc > 1) findings.push(`${width}: горизонтальное переполнение документа на ${overflow.doc} px`);

    // Обрезанный текст в элементах фичи (ellipsis на слишком узком ряду)
    const clipped = await p.evaluate(() => {
      const out: string[] = [];
      const els = Array.from(document.querySelectorAll('[data-testid^="wiki-"] *, [data-testid^="unified-"] *')) as HTMLElement[];
      for (const el of els) {
        if (el.children.length > 0) continue;
        const st = getComputedStyle(el);
        if (st.textOverflow !== 'ellipsis' || st.overflow !== 'hidden') continue;
        if (el.scrollWidth > el.clientWidth + 1) out.push(`${el.textContent?.slice(0, 30)} (${el.scrollWidth}>${el.clientWidth})`);
      }
      return out.slice(0, 8);
    });
    if (clipped.length) findings.push(`${width}: обрезанный текст: ${clipped.join('; ')}`);

    await p.close();
  }
  await ctx.close();

  expect(findings, 'находки по вёрстке').toEqual([]);
});

// ─── QA-2: повторная проверка дефектов первого круга ───

test('QA2 44db9ab9: страница вики не дублируется в «Другие заметки»; счётчики чипов равны числу строк', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();

  // Данные, на которых вообще есть что проверять: бэк отдаёт страницы вики и списком заметок
  const notes = await client.get<{ id: string; title: string; source: string }[]>('/api/notes');
  const asNote = notes.filter(n => n.source === 'wiki' && n.title === MAIN_PAGE);
  expect(asNote.length, 'бэк отдаёт страницу вики заметкой source=wiki — без этих данных проверка не о чём').toBeGreaterThan(0);

  await openApp(page);
  // Один ряд на страницу: только как страница раздела
  await expect(page.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE })).toHaveCount(1);
  await expect(page.locator('[data-testid="unified-row"][data-kind="note"]', { hasText: MAIN_PAGE })).toHaveCount(0);

  const shape = await treeShape(page);
  expect(shape, 'дерево открылось').not.toBeNull();
  // «Другие заметки» (если есть) — только заметки, вики-страниц там быть не должно
  for (const g of shape!) {
    if (g.group.includes('Другие заметки')) {
      const wikiRows = g.rows.filter(r => r.kind === 'page');
      expect(wikiRows.map(r => r.title), 'в «Другие заметки» нет страниц вики').toEqual([]);
    }
  }

  // Счётчики чипов «кто написал» = числу строк дерева по происхождению
  const chips = await page.getByTestId('unified-filter').evaluate(el =>
    Array.from(el.querySelectorAll('[role="option"], button, [class*="chip" i]'))
      .map(c => (c.textContent || '').trim())
      .filter(t => /\s\d+$/.test(t)));
  expect(chips.length, 'чипы со счётчиками на месте').toBeGreaterThanOrEqual(2);
  const claimed: Record<string, number> = {};
  for (const c of chips) {
    const m = /^(.+?)\s+(\d+)$/.exec(c);
    if (m) claimed[m[1].trim()] = Number(m[2]);
  }
  const rows = shape!.flatMap(g => g.rows);
  const counted = {
    Все: rows.length,
    Заметки: rows.filter(r => r.origin === 'note').length,
    ИИ: rows.filter(r => r.origin === 'ai').length,
    Вместе: rows.filter(r => r.origin === 'together').length,
    Мои: rows.filter(r => r.origin === 'me').length,
  };
  for (const [label, want] of Object.entries(counted)) {
    if (claimed[label] !== undefined) {
      expect(claimed[label], `счётчик чипа «${label}» = числу строк`).toBe(want);
    }
  }

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('QA2 3e2ec144: карточки «Источники/…» не в дереве и не в счётчиках; хроника и обычные страницы на месте', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  // Дерево может монтироваться чуть позже загрузки страницы: ждём его, а не читаем сразу
  await expect(page.getByTestId('wiki-unified-tree')).toBeVisible({ timeout: 20000 });

  const shape = await treeShape(page);
  expect(shape, 'дерево открылось').not.toBeNull();
  const titles = shape!.flatMap(g => g.rows.map(r => r.title));
  expect(titles, 'служебных карточек источников в дереве нет').not.toContain(AI_CARD);
  expect(titles, 'служебных карточек источников в дереве нет').not.toContain(HUMAN_CARD);
  // Обычная страница и хроника — на месте
  expect(titles).toContain(MAIN_PAGE);
  expect(titles).toContain(CHRONICLE_PAGE);

  // Счётчик в заголовке группы = числу её строк (карточки в него не входят)
  for (const g of shape!) {
    const m = /(\d+)\s*$/.exec(g.group);
    expect(Number(m?.[1] ?? -1), `счётчик группы «${g.group}»`).toBe(g.rows.length);
  }
  await shot(page, 'qa2-01-tree-1280.png');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('QA2 a16c6167: плашки «Только чтение» рядом с «Править» нет; у хроники — своё объяснение', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);

  // Страница, которую можно править: кнопки «Править» и плашки «Только чтение» вместе нет
  await openWikiPage(page, MAIN_PAGE);
  await expect(page.getByTestId('wiki-edit-open')).toBeVisible();
  await expect(page.getByTestId('wiki-readonly')).toHaveCount(0);
  await shot(page, 'qa2-02-page-editable-1280.png');

  // Хроника: правки нет, но объяснение — про хронику, а не про Хранителя
  await openWikiPage(page, CHRONICLE_PAGE);
  await expect(page.getByTestId('wiki-edit-open')).toHaveCount(0);
  const ro = page.getByTestId('wiki-readonly');
  await expect(ro).toBeVisible();
  await expect(ro).toContainText('Хроника пишется сама');
  await expect(ro).not.toContainText('вики ведёт Хранитель');
  await shot(page, 'qa2-03-chronicle-1280.png');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('QA2 72bb73fd: на 360 и 400 в шторке единое дерево со значками; выбор страницы открывает её и закрывает шторку', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await reseed();
  for (const width of [360, 400]) {
    const p = await ctx.newPage();
    await p.setViewportSize({ width, height: 800 });
    await openMobileTree(p);

    await expect(p.getByTestId('wiki-unified-tree'), `${width}: в шторке единое дерево`).toBeVisible();
    await expect(p.getByTestId('unified-legend'), `${width}: значки авторства на месте`).toContainText('заметка');
    await expect(p.getByTestId('unified-legend')).toContainText('вместе');
    // Строки и заметок, и страниц вики
    const pageRows = p.locator('[data-testid="unified-row"][data-kind="page"]');
    if (await pageRows.count() === 0) console.log('DIAG ' + width + ' ' + JSON.stringify(await treeDiag(p)));
    expect(await pageRows.count(), `${width}: в шторке есть строки страниц вики`).toBeGreaterThan(0);
    expect(await p.locator('[data-testid="unified-row"][data-kind="note"]').count(), `${width}: в шторке есть заметки}`).toBeGreaterThan(0);

    await shot(p, `qa2-04-drawer-tree-${width}.png`);
    await pageRows.filter({ hasText: MAIN_PAGE }).first().click();
    await expect(p.getByTestId('wiki-page'), `${width}: страница открылась`).toBeVisible({ timeout: 15000 });
    await expect(p.getByTestId('wiki-drawer-overlay'), `${width}: шторка закрылась`).toHaveCount(0);
    await p.close();
  }
  await ctx.close();
});

test('QA2 4dfec12c: цели касания в шторке (заголовки групп и строки) ≥ 40 px на 360 и 400', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await reseed();
  for (const width of [360, 400]) {
    const p = await ctx.newPage();
    await p.setViewportSize({ width, height: 800 });
    await openMobileTree(p);

    const measured = await p.locator('[data-testid="unified-group"]').count();
    expect(measured, `${width}: заголовки групп в шторке есть`).toBeGreaterThan(0);
    const tooSmall = await p.evaluate(() => {
      const bad: string[] = [];
      for (const sel of ['[data-testid="unified-group"]', '[data-testid="unified-row"]']) {
        for (const el of Array.from(document.querySelectorAll(sel))) {
          const h = Math.round(el.getBoundingClientRect().height);
          if (h < 40) bad.push(`${sel} «${(el.textContent || '').trim().slice(0, 24)}» = ${h}px`);
        }
      }
      return bad;
    });
    expect(tooSmall, `${width}: цели касания ниже 40 px`).toEqual([]);
    await p.close();
  }
  await ctx.close();
});

test('QA2 02125be1: «＋ Добавить утверждение» — добавить, сохранить, ✎; пустое не сохраняется; «История» только для чтения', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await openWikiPage(page, ADD_PAGE);
  await page.getByTestId('wiki-edit-open').click();
  await expect(page.getByTestId('wiki-editor')).toBeVisible();

  // «История» — только для чтения: кнопки добавить в ней нет
  await expect(page.getByTestId('wiki-edit-add-История')).toHaveCount(0);
  await expect(page.getByTestId('wiki-edit-add-Сводка')).toBeVisible();

  // Пустое утверждение не сохраняется: кнопка «Сохранить» заблокирована, есть пояснение.
  // Ключ строки — счётчик внутри редактора (0, 1, …), поэтому строку ищем не по ключу, а префиксом.
  const addedRow = page.locator('[data-testid^="wiki-edit-added-"]').last();
  await page.getByTestId('wiki-edit-add-Сводка').click();
  await expect(addedRow).toBeVisible();
  await expect(page.getByTestId('wiki-edit-save')).toBeDisabled();
  await expect(page.getByText('Пустое утверждение не сохранить')).toBeVisible();
  await addedRow.getByRole('button', { name: 'Убрать новое утверждение' }).click();
  await expect(page.locator('[data-testid^="wiki-edit-added-"]')).toHaveCount(0);

  // Новое утверждение: добавлено, сохранено, на странице со сноской ✎
  const NEW_TEXT = 'Новое утверждение человека про очередь.';
  await page.getByTestId('wiki-edit-add-Сводка').click();
  await page.locator('[data-testid^="wiki-edit-added-"]').last().locator('textarea').fill(NEW_TEXT);
  await shot(page, 'qa2-05-editor-add-1280.png');
  await page.getByTestId('wiki-edit-save').click();
  await expect(page.getByTestId('wiki-editor')).toHaveCount(0, { timeout: 15000 });
  await expect(page.getByTestId('wiki-page')).toContainText(NEW_TEXT);
  // Строка-«li» нового утверждения: её сноска — человеческая «✎ …»
  const line = page.locator('[data-testid="wiki-section"] li', { hasText: NEW_TEXT });
  await expect(line).toHaveCount(1);
  await expect(line.getByTestId('wiki-footnote')).toContainText('✎');
  await shot(page, 'qa2-06-new-statement-1280.png');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('QA2 02125be1: конфликт 409 с добавленным утверждением — «Оставить моё» не теряет новое и не дублирует такое же', async ({ browser }) => {
  const errors: string[] = [];
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const a = await ctx.newPage();
  const b = await ctx.newPage();
  const push = (t: string) => { if (!t.includes('409')) errors.push(t); };
  a.on('console', m => { if (m.type() === 'error') push(m.text()); });
  b.on('console', m => { if (m.type() === 'error') push(m.text()); });

  const UNIQUE = 'Уникальное новое утверждение про очередь.';
  const SAME = 'Одинаковое утверждение про очередь.';

  await reseed();
  // Окно A: два новых утверждения — одно уникальное, второе такое же, что допишет окно B
  await a.goto(BASE + '/#/notes'); await a.waitForTimeout(1500);
  await openWikiPage(a, EDIT_PAGE_2);
  await a.getByTestId('wiki-edit-open').click();
  await a.getByTestId('wiki-edit-add-Сводка').click();
  await a.getByTestId('wiki-edit-added-0').locator('textarea').fill(UNIQUE);
  await a.getByTestId('wiki-edit-add-Сводка').click();
  await a.getByTestId('wiki-edit-added-1').locator('textarea').fill(SAME);

  // Окно B: та же страница, то же утверждение — и сохраняет первым
  await b.goto(BASE + '/#/notes'); await b.waitForTimeout(1500);
  await openWikiPage(b, EDIT_PAGE_2);
  await b.getByTestId('wiki-edit-open').click();
  await b.getByTestId('wiki-edit-item-0').locator('textarea').fill(SAME);
  await b.getByTestId('wiki-edit-save').click();
  await expect(b.getByTestId('wiki-editor')).toHaveCount(0, { timeout: 15000 });

  // A сохраняет — 409 и сравнение; «Оставить моё»
  await a.getByTestId('wiki-edit-save').click();
  const conflict = a.getByTestId('wiki-edit-conflict');
  await expect(conflict).toBeVisible({ timeout: 15000 });
  await shot(a, 'qa2-07-conflict-1280.png');
  await a.getByTestId('wiki-conflict-take-mine').click();
  await expect(a.getByTestId('wiki-editor')).toHaveCount(0, { timeout: 15000 });

  // Новое не потеряно — ровно один раз; то, что уже есть у ИИ, не задублировано
  expect(await occurrences(a, UNIQUE), 'новое утверждение сохранено ровно один раз').toBe(1);
  expect(await occurrences(a, SAME), 'совпадающее утверждение не задублировано').toBe(1);

  await ctx.close();
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// Из «не проверено» первого круга: спор «ИИ оспаривает». Состояние собирается фикстурой
// (пакет conflict@human + строка журнала apply с overrodeHuman) — проверяется UI спора,
// а не ночной разбор, который такой пакет в жизни порождает.
test('QA2 «ИИ оспаривает» (conflict@human): метка на утверждении, карточка спора и «оспорил» в журнале', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await expect(page.getByTestId('wiki-unified-tree')).toBeVisible({ timeout: 20000 });
  await openWikiPage(page, DISPUTE_PAGE);

  // Карточка спора и метка на человеческом утверждении; текст человека на месте
  await expect(page.getByTestId('wiki-override-disputed')).toContainText('ИИ оспаривает ваше утверждение');
  await expect(page.getByTestId('wiki-override-disputed')).toContainText('Ваш текст на месте');
  const tag = page.getByTestId('wiki-override-tag');
  await expect(tag).toHaveCount(1);
  await expect(tag).toHaveAttribute('data-kind', 'disputed');
  await expect(tag).toContainText('ИИ оспаривает');
  await expect(page.getByTestId('wiki-page')).toContainText(HUMAN_STATEMENT);
  // Где именно метка: по коду она у строки previous, а previous живого спора бэк отдаёт только
  // из «Истории» — фиксирую фактическое место как доказательство в отчёт
  console.log('DISPUTE-MARK ' + JSON.stringify(await page.evaluate(() => {
    const t = document.querySelector('[data-testid="wiki-override-tag"]');
    return {
      section: t?.closest('[data-testid="wiki-section"]')?.getAttribute('data-section-name') ?? null,
      line: (t?.closest('li')?.textContent || '').slice(0, 80),
    };
  })));
  await shot(page, 'qa2-08-dispute-1280.png');

  // «Журнал»: вид «оспорил» и действие «Открыть страницу» (не «Вернуть мою»).
  // Заголовок строки — имя источника, страницы в тексте строки нет: мерим по виду действия
  await page.getByTestId('wiki-rail-journal').click();
  const row = page.getByTestId('wiki-journal-row').filter({ hasText: 'оспорил' });
  await expect(row).toHaveCount(1, { timeout: 15000 });
  // Кнопка действия — сосед строки (не внутри неё): у спора «Открыть страницу», не «Вернуть мою»
  await expect(page.getByTestId('wiki-journal-open-page')).toContainText('Открыть страницу');
  await shot(page, 'qa2-09-journal-dispute-1280.png');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 2: на 360 сноска открывает шторку снизу (bottom-sheet)', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await reseed();
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 360, height: 800 });
  await reseed();
  await openMobileTree(p);
  // Страницу выбираем в едином дереве шторки (после правки 081a9bdc отдельного дерева
  // разделов на 360 больше нет)
  await p.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
  await expect(p.getByTestId('wiki-page')).toBeVisible({ timeout: 15000 });
  const fn = p.getByTestId('wiki-footnote').first();
  await fn.click();
  const pop = p.getByTestId('wiki-footnote-popover');
  await expect(pop).toBeVisible();
  await expect(pop).toHaveAttribute('data-mode', 'sheet');
  await expect(pop.getByTestId('wiki-footnote-open-chat')).toBeVisible();
  await ctx.close();
});

test('сценарий 3: отказ гейта (ссылка вбок) — русский текст, правка не теряется', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  // Страница раздела «Общее»: ссылка вниз (в «Личное») гейт отбивает
  await pageRow(page, 'Общая страница').first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible();
  await page.getByTestId('wiki-edit-open').click();
  await expect(page.getByTestId('wiki-editor')).toBeVisible();

  const field = page.getByTestId('wiki-edit-item-0').locator('textarea');
  await field.fill('Ссылка вниз: [[Замеры локальной GPU]]');
  await page.getByTestId('wiki-edit-save').click();

  // Отказ — русским текстом, правка осталась в поле
  const err = page.getByTestId('wiki-edit-error');
  await expect(err).toBeVisible({ timeout: 15000 });
  await expect(err).toContainText(/[А-Яа-яЁё]/);
  await expect(page.getByTestId('wiki-editor')).toBeVisible();
  await expect(field).toHaveValue(/Замеры локальной GPU/);

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 5: сноска в секции «Заметки» — отказ гейта русским текстом, текст не теряется', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await openWikiPage(page, EDIT_PAGE);

  await page.getByTestId('wiki-edit-open').click();
  await expect(page.getByTestId('wiki-editor')).toBeVisible();
  const notes = page.getByTestId('wiki-edit-notes').locator('textarea');
  await notes.fill('Заметка со сноской [^s1] — так нельзя.');
  await page.getByTestId('wiki-edit-save').click();

  const err = page.getByTestId('wiki-edit-error');
  await expect(err).toBeVisible({ timeout: 15000 });
  await expect(err).toContainText(/[А-Яа-яЁё]/);
  await expect(page.getByTestId('wiki-editor')).toBeVisible();
  await expect(notes).toHaveValue(/Заметка со сноской/);

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 6: «Входящие» — карточка пакета с причиной отказа', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await openWikiPage(page, MAIN_PAGE);

  await page.getByTestId('wiki-rail-inbox').click();
  const pkgRow = page.getByTestId('wiki-pkg-row');
  await expect(pkgRow).toBeVisible();
  await expect(pkgRow).toContainText('Источник старше вашей правки');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 7: «Здоровье» — страницу изменили снаружи, «Принять как мою правку»', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  // Правка файла на диске мимо приложения — дрейф (файл разошёлся с манифестом)
  const root = await wikiRoot();
  if (root) {
    const file = path.join(root, 'pages', 'Личное', `${MAIN_PAGE}.md`);
    const text = readFileSync(file, 'utf8');
    writeFileSync(file, text.replace('## Хронология', '## Хронология\nПравка снаружи, мимо AI Home.'), 'utf8');
  }
  await openApp(page);
  await openWikiPage(page, MAIN_PAGE);

  await page.getByTestId('wiki-rail-health').click();
  // Отчёт линта «Проверить сейчас» — источник строк о расхождении
  await page.getByTestId('wiki-health-run').click();
  const driftRow = page.getByTestId('wiki-health-row').filter({ has: page.getByTestId('wiki-health-adopt-mine') });
  await expect(driftRow).toBeVisible({ timeout: 30000 });
  await expect(driftRow).toContainText(MAIN_PAGE);
  await expect(page.getByTestId('wiki-health-adopt-mine')).toBeVisible();
  await page.getByTestId('wiki-health-adopt-mine').click();
  await expect(page.getByText('Принять как мою правку?')).toBeVisible();
  await page.getByRole('button', { name: 'Принять как мою правку', exact: true }).last().click();
  // После принятия — подсказка про «Проверить сейчас»
  await expect(page.getByTestId('wiki-health-adopt-mine-flash')).toBeVisible({ timeout: 15000 });
  await expect(page.getByTestId('wiki-health-adopt-mine-flash')).toContainText('Проверить сейчас');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 8: тёмная тема — страница вики рисуется по тёмным токенам', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  await p.goto(BASE + '/#/notes');
  await p.reload();
  await p.waitForTimeout(1800);
  await closeToasts(p);
  await p.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
  await expect(p.getByTestId('wiki-page')).toBeVisible();

  const light = await p.evaluate(() => getComputedStyle(document.body).backgroundColor);
  await p.evaluate(() => { document.documentElement.setAttribute('data-theme', 'dark'); });
  await p.waitForTimeout(500);
  const dark = await p.evaluate(() => getComputedStyle(document.body).backgroundColor);
  const pageDark = await p.evaluate(() => {
    const el = document.querySelector('[data-testid="wiki-page"]');
    return el ? getComputedStyle(el).color : '';
  });
  await ctx.close();

  expect(light, 'светлая тема — светлый фон').not.toBe(dark);
  expect(pageDark, 'текст страницы в тёмной теме задан токеном').not.toBe('');
});
