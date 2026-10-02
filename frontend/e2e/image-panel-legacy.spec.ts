import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import {
  D, FILL, H, P, S, anchor, brush, closeSheet, finishJob, heroThread, hubSend, input, newWorld, openChat, panel, pushRecord, record, shot,
  strip, w, type Thread,
} from './imagePanelMock';

// Регресс-гейт панели «Картинки» БЕЗ флага image-panel-v5: полоса, панель «Настройки», попап
// «Редактор», карточки ленты и «Сохранить как…» обязаны вести себя как в master. Сменила шесть
// устаревших спек редактора v2 (image-editor-v2-chat/-agent/-local/-save, image-editor-draft-chat),
// которые искали окно редактора v2 («К файлам», data-prompt-card, чат картинки): смысл их
// проверок перенесён на картинку в основном чате (ADR-019).
// Бэкенд не нужен: собранный dist раздаётся статикой, /api/** и хаб — моки (imagePanelMock.ts).
// Гоняется и на ветке, и на master (сверка: dist master раздаётся так же).
//
//   (cd dist && python3 -m http.server 5241) &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5241 I4_SHOTS_DIR=../.cc-attachments/image-i4 \
//     npx playwright test e2e/image-panel-legacy.spec.ts

const SHOTS = process.env.I4_SHOTS_DIR ? `${process.env.I4_SHOTS_DIR}/legacy` : '';
const TRACE = !!process.env.I4_TRACE;
const W = { width: 1440, height: 900 };
const M = { width: 360, height: 780 };
const HERO_FILE = `/#/project/${P}/file/${encodeURIComponent('images/hero.png')}`;

function open(page: Page, vp: { width: number; height: number }, threads: Thread[], focus: string | null, o: { route?: string; autoFinish?: boolean; feed?: Record<string, unknown>[] } = {}) {
  newWorld({ focus, threads, v5: false, autoFinish: o.autoFinish, feed: o.feed ?? threads.map(t => anchor(t.id)) });
  return openChat(page, { vp, route: o.route, trace: TRACE });
}

// hero.png с версией 1 прошлого запуска: есть что сохранять, в ленте — её карточка
const heroEdited = (): Thread => ({
  ...heroThread(),
  versions: [...heroThread().versions, { id: 'v1', number: 1, jobId: 'job-h1', variant: 0, baseVersionId: 'origin', baseStepId: null, steps: ['st-h1'], currentStepId: 'st-h1', createdAt: '2026-10-02T09:10:00Z' }],
  currentVersionId: 'v1',
  launches: [{ jobId: 'job-h1', baseVersionId: 'origin', baseStepId: null, at: '2026-10-02T09:09:00Z', status: 'done', initiator: 'human', prompt: 'вечер' }],
});
const heroEditedFeed = () => [anchor(H), record('image_launch_versions', { threadId: H, jobId: 'job-h1', prompt: 'вечер', count: 1, initiator: 'human' })];

const editor = (page: Page) => page.getByRole('heading', { name: /^Редактор · / });
const chatToggle = (page: Page) => page.getByRole('button', { name: 'Режим «Чат»' });

test.afterEach(async ({ page }, info) => {
  if (info.status !== info.expectedStatus) {
    fs.mkdirSync('/tmp/i4', { recursive: true });
    await page.screenshot({ path: `/tmp/i4/legacy-fail-${info.title.slice(0, 12).replace(/[^\p{L}\d]+/gu, '_')}.png` }).catch(() => {});
  }
});

// Было image-editor-v2-chat: «чат картинки создаётся, находится снова, снимок — только при изменении
// холста». Стало: вход из просмотра файла ведёт в последний чат проекта, повторный вход — та же
// нить, пометки уходят снимком со следующим сообщением и только один раз
test('1440: «Редактировать» под просмотром файла — чат, та же нить, пометки уходят снимком один раз', async ({ page }) => {
  await open(page, W, [], null, { route: HERO_FILE });
  await page.getByRole('button', { name: 'Редактировать' }).first().click();
  await expect(editor(page)).toHaveText('Редактор · hero.png');
  expect(w().threads.map(t => t.file)).toEqual(['images/hero.png']);
  await expect(page).toHaveURL(new RegExp(`/chat/${S}`));

  // Пометка на холсте — подпись в подвале попапа, после «Готово» — чип над полем ввода
  const pic = page.locator('svg[viewBox="0 0 320 240"]');
  await expect(pic).toBeVisible();
  await brush(page, pic);
  await expect(page.getByText('Пометки уйдут со следующим сообщением — в режиме «Картинка» или агенту')).toBeVisible();
  await shot(page, SHOTS, 'w1440-editor-mark');
  await page.getByRole('button', { name: 'Готово' }).click();
  await expect(editor(page)).toHaveCount(0);
  await expect(strip(page)).toContainText('Работаем с: hero.png');
  await expect(input(page)).toHaveAttribute('placeholder', /Что изменить/);
  const chip = page.getByText('hero.png · 1 пометка');
  await expect(chip).toBeVisible();

  // Режим «Чат»: сообщение агенту уходит со снимком пометок, чип гаснет
  await chatToggle(page).click();
  await input(page).fill('Что здесь можно улучшить?');
  await input(page).press('Enter');
  await expect.poll(() => w().invocations.filter(i => i.target === 'SendMessage').length).toBe(1);
  expect(w().uploads).toEqual(['.cc-attachments/hero-пометки.png']);
  expect(w().invocations.find(i => i.target === 'SendMessage')!.args[2]).toEqual(['.cc-attachments/hero-пометки.png']);
  await expect(chip).toHaveCount(0);

  // Холст не менялся — второе сообщение без снимка
  await input(page).fill('А если сделать вечер?');
  await input(page).press('Enter');
  await expect.poll(() => w().invocations.filter(i => i.target === 'SendMessage').length).toBe(2);
  expect(w().invocations.filter(i => i.target === 'SendMessage')[1].args[2]).toEqual([]);
  expect(w().uploads).toHaveLength(1);

  // Повторный вход из просмотра файла (новая загрузка страницы) — та же нить, вторая не заводится
  await page.goto('about:blank');
  await page.goto(HERO_FILE);
  await page.getByRole('button', { name: 'Редактировать' }).first().click();
  await expect(editor(page)).toHaveText('Редактор · hero.png');
  expect(w().threads).toHaveLength(1);
  expect(w().feed.filter(r => r.recordType === 'image_thread')).toHaveLength(1);
});

test('360: попап «Редактор» во весь экран, полоса без подписи чипа', async ({ page }) => {
  await open(page, M, [], null, { route: HERO_FILE });
  await page.getByRole('button', { name: 'Редактировать' }).first().click();
  await expect(editor(page)).toHaveText('Редактор · hero.png');
  // Выбор картинки на телефоне поднимает шторку панели (как в master) — опускаем её к редактору
  await closeSheet(page);
  // Подвал попапа помещается в 360
  const done = (await page.getByRole('button', { name: 'Готово' }).boundingBox())!;
  expect(done.x).toBeGreaterThanOrEqual(0);
  expect(done.x + done.width).toBeLessThanOrEqual(360);
  await shot(page, SHOTS, 'm360-editor');
  await page.getByRole('button', { name: 'Готово' }).click();
  await expect(strip(page)).toBeVisible();
  await expect(strip(page)).not.toContainText('Работаем с:');
  await shot(page, SHOTS, 'm360-strip');
});

// Было image-editor-draft-chat: «новая картинка — черновик, генерация агентом, «Применить»
// привязывает к файлу». Стало: «Нарисовать картинку» в папке дерева — черновик в ленте, агент
// рисует в него, «Сохранить в проект» кладёт файл в ту же папку, и карточка идёт за файлом
for (const vp of [W, M]) {
  test(`${vp.width}: черновик из папки, генерация агентом, «Сохранить в проект» в его папку`, async ({ page }) => {
    const mobile = vp.width < 800;
    // Дерево файлов открываем на широком экране, ленту затем смотрим в нужной ширине
    await open(page, W, [], null, { route: `/#/project/${P}/file/${encodeURIComponent('images/описание.txt')}`, autoFinish: false });
    await page.getByRole('button', { name: 'Файлы', exact: true }).click();
    await page.getByText('images', { exact: true }).first().click({ button: 'right' });
    await page.getByText('Нарисовать картинку', { exact: true }).click();
    await expect.poll(() => w().threads.map(t => [t.id, t.draftFolder])).toEqual([[D, 'images']]);
    await expect(page).toHaveURL(new RegExp(`/chat/${S}`));
    // Черновик завёл человек — поле ввода сразу «Картинка»
    await expect(input(page)).toHaveAttribute('placeholder', /Опишите новую/);
    if (mobile) {
      // Сужение окна уводит телефон к списку чатов проекта — открываем чат заново в 360
      await page.goto('about:blank');
      await page.setViewportSize(vp);
      await page.goto(`/#/project/${P}/chat/${S}`);
      await closeSheet(page);
    }
    // Пустой черновик карточкой в ленте не рисуется (просьба 02.10: лента без плашки «Новая картинка»);
    // состояние остаётся — нить-черновик есть, чип в полосе, а папка «images» едет в запуск
    await expect(page.locator('[data-image-draft]')).toHaveCount(0);
    await expect(page.getByText('Опишите её в поле ввода')).toHaveCount(0);
    await shot(page, SHOTS, `${mobile ? 'm360' : 'w1440'}-draft`);

    // Агент запускает генерацию в черновик: карточка запуска «Claude: генерация» и «Отменить»
    const t = w().threads.find(x => x.id === D)!;
    t.launches.push({ jobId: 'job-agent', baseVersionId: null, baseStepId: null, at: new Date().toISOString(), status: 'running', initiator: 'agent', prompt: 'рыжий кот на подоконнике, акварель' });
    w().revision++;
    pushRecord(record('image_launch_versions', { threadId: D, jobId: 'job-agent', prompt: 'рыжий кот на подоконнике, акварель', count: 2, model: 'FLUX Fill', initiator: 'agent', estimate: { amount: 0.16, unit: 'usd', approx: true } }, Date.now()));
    hubSend({ type: 'image_thread_changed', sessionId: S, projectId: P, revision: w().revision, state: { focus: w().focus, revision: w().revision, threads: w().threads } });
    const launch = page.locator('[data-image-launch="running"]');
    await expect(launch).toContainText('Claude: генерация');
    await expect(launch).toContainText('≈ $0.16');
    await expect(launch.getByRole('button', { name: 'Отменить' })).toBeVisible();

    // Готово: два варианта — две версии, у каждой «Сохранить в проект»
    finishJob('job-agent', [0, 1]);
    const versions = page.locator('[data-image-version="1"], [data-image-version="2"]');
    await expect(versions).toHaveCount(2);
    await shot(page, SHOTS, `${mobile ? 'm360' : 'w1440'}-draft-versions`);
    await page.locator('[data-image-version="2"]').getByRole('button', { name: 'Сохранить в проект' }).click();
    await expect.poll(() => w().saves.length).toBe(1);
    expect(w().saves[0]).toMatchObject({ threadId: D, folder: 'images', mode: 'as' });
    await expect(page.getByText(/^Сохранено в проект: images\/kartinka\.png/).first()).toBeVisible();
    // Нить пошла за файлом: в полосе — имя файла, сохранённая версия — «в проекте»
    expect(w().threads.find(x => x.id === D)!.file).toBe('images/kartinka.png');
    await expect(page.locator('[data-image-version="2"]')).toContainText('в проекте');
    if (!mobile) await expect(strip(page)).toContainText('kartinka.png');
  });
}

// Было image-editor-v2-agent: «агент меняет открытый редактор, карточки запуска и промпта, тихая
// строка». Стало: карточка запуска агентом в ленте (цена, что изменил, «Отменить» без списания),
// карточка «Промпт» запускает его в выбранную картинку, тихая строка архивного чата читается
test('1440: агент — карточка запуска с отменой, карточка «Промпт», тихая строка архивного чата', async ({ page }) => {
  const archived = { kind: 'image_launch', by: 'human', prompt: 'вечер', provider: 'fal', model: FILL, count: 1,
    estimate: { amount: 0.08, unit: 'usd', approx: true, source: 'catalog' }, jobId: 'job-old', timestamp: Date.parse('2026-10-02T09:05:00Z') };
  await open(page, W, [heroThread()], H, { autoFinish: false, feed: [archived, anchor(H)] });
  await expect(strip(page)).toContainText('Работаем с: hero.png');
  await expect(page.locator('[data-image-sysline]').first()).toHaveText('Вы запустили: «вечер» · FLUX Fill · ≈ $0.08 · 1 вариант');

  // Запуск агентом мимо нити (старый сервер или чужая задача): своя карточка в ленте
  const agentPrompt = 'Убери торшер справа и сделай тёплый вечерний свет';
  hubSend({ type: 'tool_use', sessionId: S, id: 'toolu_launch', name: 'mcp__image-editor__image_generate', input: { threadId: H, prompt: agentPrompt, model: FILL, count: 2 } });
  hubSend({ type: 'tool_result', sessionId: S, toolUseId: 'toolu_launch', isError: false, content: JSON.stringify({
    jobId: 'job-agent',
    quote: { provider: 'fal', model: FILL, estimate: { amount: 0.16, unit: 'usd', approx: true, source: 'catalog' }, expectedSeconds: 30 },
    changes: [{ field: 'prompt', from: '', to: agentPrompt }, { field: 'provider', from: null, to: 'fal' }, { field: 'model', from: 'auto', to: FILL }, { field: 'count', from: 1, to: 2 }],
  }) });
  const card = page.locator('[data-image-card]').filter({ hasText: 'Запущена генерация' });
  await expect(card).toContainText('≈ $0.16 · 2 варианта');
  await expect(card.locator('[data-image-changed]')).toHaveText('Изменил: промпт, поставщик → fal, модель → FLUX Fill, вариантов: 2');
  await shot(page, SHOTS, 'w1440-agent-launch');
  await card.getByRole('button', { name: 'Отменить' }).click();
  await expect.poll(() => w().cancelled).toEqual(['job-agent']);
  const cancelled = page.locator('[data-image-card]').filter({ hasText: 'Генерация отменена' });
  await expect(cancelled).toContainText('Деньги не списаны.');

  // Агент предлагает промпт: карточка запускает его в выбранную картинку
  const suggestion = 'Обложка блога: светлая гостиная, мягкий утренний свет, много воздуха';
  hubSend({ type: 'tool_use', sessionId: S, id: 'toolu_suggest', name: 'mcp__image-editor__image_suggest_prompt', input: { prompt: suggestion, count: 1 } });
  hubSend({ type: 'tool_result', sessionId: S, toolUseId: 'toolu_suggest', isError: false, content: JSON.stringify({ prompt: suggestion, count: 1, note: 'Промпт показан человеку карточкой' }) });
  const promptCard = page.locator('[data-image-card]').filter({ hasText: 'Промпт' });
  await expect(promptCard.locator('[data-image-prompt]')).toHaveText(suggestion);
  const gen = promptCard.getByRole('button', { name: /^Сгенерировать/ });
  await expect(gen).toBeEnabled({ timeout: 10_000 });
  await gen.click();
  await expect.poll(() => w().jobs.length).toBe(1);
  expect(w().jobs[0]).toMatchObject({ threadId: H, prompt: suggestion });
  await expect(promptCard.getByRole('button', { name: 'Запущено' })).toBeVisible();
  await shot(page, SHOTS, 'w1440-agent-prompt');
});

// Отличие от v5 по построению: без флага нет переключателя «Создать / Править», ✕ на чипе даёт
// прежний тост про режим «Чат», а не плашку «Вернуть», и полоса предлагает «Нарисовать новую»
test('1440: снять выбор без флага — тост про «Чат», без «Вернуть» и без режимов', async ({ page }) => {
  await open(page, W, [heroThread()], H);
  await expect(strip(page)).toContainText('Работаем с: hero.png');
  await expect(strip(page).locator('[data-images-mode-switch]')).toHaveCount(0);
  await strip(page).getByText('×', { exact: true }).first().click();
  await expect(page.getByText('Картинка больше не выбрана: режим «Чат»')).toBeVisible();
  await expect(page.locator('[data-images-release]')).toHaveCount(0);
  await expect(strip(page)).toContainText('Нарисовать новую');
  await expect(input(page)).not.toHaveAttribute('placeholder', /Опишите новую|Что изменить/);
});

// Было image-editor-v2-local: «Локальные модели» — «Бесплатно · …» вместо цены, выбор поставщика,
// «Улучшить лица» только у локальных. Стало: то же в панели «Настройки» и в быстрых действиях
// попапа «Редактор»; живой ComfyUI проверяют отдельно (docs/features/local-media.md)
test('1440: «Локальные модели» — «Бесплатно» в низу панели, у fal цена и «Улучшить лица» недоступно', async ({ page }) => {
  await open(page, W, [heroThread()], H);
  await expect(strip(page)).toContainText('Работаем с: hero.png');
  await page.locator('[data-images-settings-toggle] button').click();
  await expect(panel(page).locator('[data-image-settings]')).toBeVisible();
  // Поставщик по умолчанию у админа — локальные: «Бесплатно», время и без «$»
  await expect(panel(page)).toContainText(/Бесплатно/);
  await expect(panel(page)).not.toContainText('$');
  await expect(panel(page).getByRole('button', { name: /^Изменить|^Сгенерировать|^Запустить/ }).last()).toBeVisible();
  await shot(page, SHOTS, 'w1440-panel-local');

  // fal: цена в долларах; «Улучшить лица» в редакторе — выключено, с предложением локальных
  await panel(page).getByRole('button', { name: /^fal/ }).first().click();
  await expect(panel(page)).toContainText('$');
  await shot(page, SHOTS, 'w1440-panel-fal');
  await page.locator('[data-image-version="0"]').getByRole('button', { name: 'Открыть', exact: true }).click();
  await expect(editor(page)).toHaveText('Редактор · hero.png');
  const quick = page.locator('section').filter({ hasText: 'Быстрые действия' }).last();
  const faces = quick.getByRole('button', { name: 'Улучшить лица' });
  await expect(faces).toBeDisabled();
  await expect(quick.locator('[data-quick-fallback="enhanceFaces"]')).toContainText('Локальные модели');
  await shot(page, SHOTS, 'w1440-editor-fal-faces');
});

test('360: панель шторкой — «Бесплатно» в первом экране', async ({ page }) => {
  await open(page, M, [heroThread()], H);
  await expect(page.locator('[data-composer-strip="images"]')).toBeVisible({ timeout: 30_000 });
  const mini = page.locator('[data-images-strip="mini"] [data-images-summary]');
  if (await mini.isVisible()) await mini.click();
  else await page.locator('[data-images-settings-toggle] button').click();
  const sheet = page.locator('[data-gen-sheet="sheet"]');
  await expect(sheet).toBeVisible();
  await expect(sheet.locator('[data-image-settings]')).toBeVisible();
  await expect(sheet).toContainText(/Бесплатно/);
  const sb = (await sheet.boundingBox())!;
  expect(sb.x + sb.width).toBeLessThanOrEqual(360);
  await shot(page, SHOTS, 'm360-panel-local');
});

// Было image-editor-v2-save: «Сохранить как…» на живых ручках. Стало: тот же диалог из попапа
// «Редактор» нити: новое имя в другую папку, занятое имя блокирует и предлагает свободное
for (const vp of [W, M]) {
  test(`${vp.width}: «Сохранить как…» — занятое имя блокирует, «Взять …» даёт свободное, файл в другой папке`, async ({ page }) => {
    await open(page, vp, [heroEdited()], H, { feed: heroEditedFeed() });
    if (vp.width < 800) await closeSheet(page);
    await page.locator('[data-image-version="1"]').getByRole('button', { name: 'Открыть', exact: true }).click();
    await expect(editor(page)).toHaveText('Редактор · hero.png');
    await page.getByRole('button', { name: 'Сохранить как…' }).click();
    const dialog = page.locator('[data-save-as]');
    await expect(dialog).toBeVisible();
    const name = dialog.getByRole('textbox').first();
    await expect(name).toHaveValue('hero.v2');
    await expect(dialog.getByRole('button', { name: /^images\/\s*здесь сейчас/ })).toBeVisible();

    await dialog.getByRole('button', { name: /^images\/generated/ }).click();
    await expect(page.locator('[data-save-warn]')).toContainText('Такой файл уже есть: images/generated/hero.v2.png');
    const save = page.getByRole('button', { name: 'Сохранить', exact: true }).last();
    await expect(save).toBeDisabled();
    const sbox = (await save.boundingBox())!;
    expect(sbox.x + sbox.width).toBeLessThanOrEqual(vp.width);
    await shot(page, SHOTS, `${vp.width === 360 ? 'm360' : 'w1440'}-save-as-taken`);

    await page.getByRole('button', { name: 'Взять «hero.v3.png»' }).click();
    await expect(name).toHaveValue('hero.v3');
    await expect(page.getByText('Файл ляжет сюда: images/generated/hero.v3.png')).toBeVisible();
    await expect(save).toBeEnabled();
    await save.click();
    await expect.poll(() => w().saves.length).toBe(1);
    expect(w().saves[0]).toMatchObject({ folder: 'images/generated', fileName: 'hero.v3', mode: 'as', threadId: H, stepId: 'st-h1' });
    await expect(page.getByText('Сохранено в проект: images/generated/hero.v3.png').first()).toBeVisible();
    await expect(page.locator('[data-save-as]')).toHaveCount(0);
  });
}

