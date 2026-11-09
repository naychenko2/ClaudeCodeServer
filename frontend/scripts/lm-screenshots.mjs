// Скриншоты панели «Локальные модели» (вкладка «Текст»): все состояния макета
// на моках. Запуск: npx vite (5199) + бэкенд (5000) → node scripts/lm-screenshots.mjs
// Вывод: .cc-attachments/lm-*.png (папка скрыта из дерева файлов и git)
import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const API = 'http://localhost:5000';
const BASE = 'http://localhost:5199';
const OUT = '/home/an/Sources/ClaudeCodeServer-local-model-stats/.cc-attachments';
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
let projectId = null;

try {
  // 1. Логин по API, флаг local-model-stats, тестовый проект
  const api = await browser.newContext();
  const login = await (await api.request.post(`${API}/api/auth/login`, {
    data: { username: 'admin', password: '12345' },
  })).json();
  const token = login.token;
  const userId = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString('utf8')).sub;
  const H = { Authorization: `Bearer ${token}` };
  const flag = await api.request.put(`${API}/api/feature-flags/local-model-stats`, { headers: H, data: { enabled: true } });
  if (!flag.ok()) throw new Error(`флаг: ${flag.status()}`);
  const root = join(tmpdir(), `lm-screens-${Date.now()}`);
  mkdirSync(root, { recursive: true });
  const proj = await (await api.request.post(`${API}/api/projects`, {
    headers: H, data: { name: `lm-screens-${Date.now()}`, rootPath: root },
  })).json();
  projectId = proj.id;
  await api.close();

  // 2. Скриншоты: [имя, состояние, ширина, высота, задержка-скелетон]
  const shots = [
    ['lm-text-working-desktop', 'working', 1280, 900, 0],
    ['lm-text-busy-desktop', 'busy', 1280, 900, 0],
    ['lm-text-down-desktop', 'down', 1280, 900, 0],
    ['lm-text-off-desktop', 'off', 1280, 900, 0],
    ['lm-text-loading-desktop', 'working', 1280, 900, 2500],
    ['lm-text-working-360', 'working', 360, 780, 0],
    ['lm-text-busy-320', 'busy', 320, 720, 0],
    ['lm-text-down-360', 'down', 360, 780, 0],
    ['lm-text-off-320', 'off', 320, 720, 0],
  ];

  for (const [name, state, w, h, delay] of shots) {
    const ctx = await browser.newContext({ viewport: { width: w, height: h } });
    const page = await ctx.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(String(e)));
    await page.addInitScript(({ t, u }) => {
      localStorage.setItem('cc_token', t);
      localStorage.setItem('cc_user_id', u);
    }, { t: token, u: userId });

    const qs = `lmMock=${state}${delay ? `&lmDelay=${delay}` : ''}`;
    await page.goto(`${BASE}/?${qs}`, { waitUntil: 'domcontentloaded' });
    // Открыть проект
    await page.waitForTimeout(2500);
    await page.evaluate(id => { location.hash = `#/project/${id}`; }, projectId);
    await page.waitForTimeout(2500);

    // Значок появляется сам: сторож useLocalModelWatcher опрашивает configured вне панели
    await page.waitForTimeout(2000);

    // Кнопка панели в правом рельсе
    // Новая панель по умолчанию лежит в ящике «Ещё» — открываем его, если кнопки нет в столбце
    const btn = page.getByRole('button', { name: 'Локальные модели' }).first();
    if (!(await btn.isVisible())) {
      await page.getByRole('button', { name: 'Ещё' }).first().click();
      await page.waitForTimeout(500);
    }
    const target = page.getByText('Локальные модели', { exact: true }).first();
    await (await btn.isVisible() ? btn : target).waitFor({ state: 'visible', timeout: 15000 });
    await (await btn.isVisible() ? btn : target).click();
    // Панель: ждём заголовок вкладки «Текст» (InlineSegmented) или скелетон
    await page.waitForFunction(() => {
      const el = [...document.querySelectorAll('[role="complementary"], [role="dialog"]')]
        .find(x => x.getAttribute('aria-label') === 'Локальные модели');
      return !!el;
    }, null, { timeout: 15000 });
    // Дать моку ответить (или дождаться конца задержки)
    await page.waitForTimeout(delay ? delay + 800 : 1200);
    // Дать панели раскрыться (анимация)
    await page.waitForTimeout(600);

    await page.screenshot({ path: join(OUT, `${name}.png`) });
    console.log(`${name}: ok${errors.length ? ` (pageerror: ${errors.join('; ')})` : ''}`);
    await ctx.close();
  }
} catch (e) {
  console.error('FAIL:', e);
  process.exitCode = 1;
} finally {
  // Убрать тестовый проект
  if (projectId) {
    const api = await browser.newContext();
    const login = await (await api.request.post(`${API}/api/auth/login`, {
      data: { username: 'admin', password: '12345' },
    })).json();
    await api.request.delete(`${API}/api/projects/${projectId}`, { headers: { Authorization: `Bearer ${login.token}` } });
    await api.close();
  }
  await browser.close();
}
