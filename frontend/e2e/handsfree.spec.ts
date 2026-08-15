import { test, expect, type APIRequestContext, type Page } from '@playwright/test';

// Режим разговора (hands-free): круг петли, защита от эха на пути браузерного фолбэка,
// грамматика полосы ввода и гашение петли при провале PUT voiceMode.
//
// Web Speech и speechSynthesis подменяются управляемыми заглушками до загрузки приложения:
// в headless-браузере ни распознавания, ни голосов нет, а проверять надо именно логику
// петли — что микрофон не открывается под играющую озвучку.

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

// Заглушки браузерных API: счётчик созданных распознавателей — им и меряется эхо
const MOCKS = () => {
  const w = window as unknown as Record<string, unknown>;
  const recs: Record<string, unknown>[] = [];
  w.__recs = recs;
  class MockRec {
    lang = ''; interimResults = false; continuous = false; maxAlternatives = 1;
    onstart: (() => void) | null = null;
    onaudiostart: (() => void) | null = null;
    onsoundstart: (() => void) | null = null;
    onspeechstart: (() => void) | null = null;
    onresult: ((e: unknown) => void) | null = null;
    onend: (() => void) | null = null;
    onerror: ((e: unknown) => void) | null = null;
    constructor() { recs.push(this as unknown as Record<string, unknown>); }
    start() { setTimeout(() => this.onstart?.(), 0); }
    stop() { this.onend?.(); }
    abort() { this.onerror?.({ error: 'aborted' }); this.onend?.(); }
  }
  w.SpeechRecognition = MockRec;

  const utts: Record<string, unknown>[] = [];
  w.__utts = utts;
  class MockUtterance {
    onend: (() => void) | null = null;
    onerror: (() => void) | null = null;
    lang = ''; voice: unknown = null;
    constructor(public text: string) { utts.push(this as unknown as Record<string, unknown>); }
  }
  w.SpeechSynthesisUtterance = MockUtterance;
  const synth = {
    speaking: false, pending: false,
    speak(u: MockUtterance) { utts.push(u as unknown as Record<string, unknown>); synth.speaking = true; },
    cancel() {
      synth.speaking = false;
      const u = utts[utts.length - 1] as unknown as MockUtterance | undefined;
      u?.onerror?.();
    },
    getVoices: () => [{ lang: 'ru-RU', name: 'mock' }],
    addEventListener: () => {}, removeEventListener: () => {},
  };
  Object.defineProperty(window, 'speechSynthesis', { value: synth, configurable: true });

  // Управление из теста
  w.__say = (text: string) => {
    const r = recs[recs.length - 1] as unknown as MockRec;
    r.onresult?.({ results: { length: 1, 0: { isFinal: true, 0: { transcript: text } } } });
    r.onend?.();
  };
  w.__finishSpeech = () => {
    const u = utts[utts.length - 1] as unknown as MockUtterance | undefined;
    synth.speaking = false;
    u?.onend?.();
  };
  // Пустой цикл распознавания ровно так, как его отдаёт настоящий движок:
  // сначала onerror('no-speech'), СЛЕДОМ onend — на этой паре счётчик бесплодных
  // циклов и рос вдвое
  w.__barrenCycle = () => {
    const r = recs[recs.length - 1] as unknown as MockRec;
    r.onerror?.({ error: 'no-speech' });
    r.onend?.();
  };
  w.__speaking = () => synth.speaking;
  w.__spoken = () => utts.map(u => String((u as unknown as MockUtterance).text ?? ''));
};

// Хелперы чтения из страницы
const recCount = (page: Page) => page.evaluate(() => (window as unknown as { __recs: unknown[] }).__recs.length);
const spoken = (page: Page) => page.evaluate(() => (window as unknown as { __spoken: () => string[] }).__spoken());

async function setVoiceMode(page: Page, on: boolean) {
  await page.evaluate(async (v: boolean) => {
    const id = location.hash.split('/').pop();
    await fetch(`/api/chats/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${localStorage.getItem('cc_token')}` },
      body: JSON.stringify({ voiceMode: v }),
    });
  }, on);
}

async function openChat(page: Page, token: string, chatId: string, serverTts = false) {
  await page.addInitScript(MOCKS);
  await page.addInitScript((t: string) => localStorage.setItem('cc_token', t), token);
  // По умолчанию гасим серверный синтез: уходим на браузерный фолбэк — самый частый путь
  // отказа и тот самый канал эха, который чинили. serverTts=true проверяет второй путь
  if (!serverTts) {
    await page.route('**/api/tts', route => route.fulfill({
      status: 503, contentType: 'application/json', body: JSON.stringify({ reason: 'not_configured' }),
    }));
  }
  await page.goto(`/#/chats/${chatId}`);
  await expect(page.locator('textarea.cc-composer-input')).toBeVisible({ timeout: 20_000 });
}

test.describe('режим разговора', () => {
  let token: string;
  let chatId: string;

  test.beforeAll(async ({ playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    token = await login(request);
    const r = await request.post('/api/chats', {
      data: { mode: 'auto', name: `E2E hands-free ${Date.now()}` },
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(r.ok(), 'чат должен создаться').toBeTruthy();
    chatId = (await r.json()).id as string;
    await request.dispose();
  });

  test('грамматика полосы: кнопка режима уступает место «Отправить» при тексте', async ({ page }) => {
    await openChat(page, token, chatId);
    const voiceBtn = page.getByRole('button', { name: 'Режим разговора' });
    await expect(voiceBtn).toBeVisible();

    await page.locator('textarea.cc-composer-input').fill('текст');
    await expect(voiceBtn).toBeHidden();
    await expect(page.locator('button[title*="Отправить"]')).toBeVisible();

    await page.locator('textarea.cc-composer-input').fill('');
    await expect(voiceBtn).toBeVisible();
  });

  test('круг петли: слушаю → окно отмены → отправка → озвучка → снова слушаю, без эха', async ({ page }) => {
    await openChat(page, token, chatId);
    await page.getByRole('button', { name: 'Режим разговора' }).click();
    await expect(page.getByText('слушаю')).toBeVisible();

    // Распознали фразу — окно отмены с текстом и отсчётом. Фраза уникальна: в ленте
    // могли остаться сообщения прошлых прогонов
    const phrase = `скажи слово ${Date.now() % 10000}`;
    await page.evaluate((t: string) => (window as unknown as { __say: (t: string) => void }).__say(t), phrase);
    await expect(page.getByText(phrase).first()).toBeVisible();

    // Через 2 секунды уходит отправка и начинается ход
    await expect(page.getByText('думает…')).toBeVisible({ timeout: 8_000 });
    // Микрофон на время хода закрыт
    const recsWhileThinking = await page.evaluate(() => (window as unknown as { __recs: unknown[] }).__recs.length);

    // Дожидаемся ответа и озвучки (браузерный фолбэк)
    await expect(page.getByText('отвечает…')).toBeVisible({ timeout: 240_000 });
    // Фаза выставляется синхронно, ДО ответа сервера на /api/tts — ждём сам факт
    // ухода текста в браузерный синтезатор
    await expect.poll(
      () => page.evaluate(() => (window as unknown as { __utts: unknown[] }).__utts.length),
      { message: 'озвучка должна пойти голосом браузера', timeout: 15_000 },
    ).toBeGreaterThan(0);

    // ЭХО: пока синтезатор говорит, новых распознавателей не создаётся
    await page.waitForTimeout(3000);
    const recsWhileSpeaking = await page.evaluate(() => (window as unknown as { __recs: unknown[] }).__recs.length);
    expect(recsWhileSpeaking, 'микрофон не открывается под озвучку').toBe(recsWhileThinking);

    // Синтезатор замолчал — петля снова слушает
    await page.evaluate(() => (window as unknown as { __finishSpeech: () => void }).__finishSpeech());
    await expect(page.getByText('слушаю')).toBeVisible({ timeout: 10_000 });
    const recsAfter = await page.evaluate(() => (window as unknown as { __recs: unknown[] }).__recs.length);
    expect(recsAfter, 'после озвучки микрофон открывается снова').toBeGreaterThan(recsWhileSpeaking);
  });

  test('серверная озвучка (mp3): круг замыкается сам, микрофон под неё не открывается', async ({ page }) => {
    const ttsCalls: number[] = [];
    page.on('response', r => { if (r.url().includes('/api/tts')) ttsCalls.push(r.status()); });
    await openChat(page, token, chatId, true);
    await page.getByRole('button', { name: 'Режим разговора' }).click();
    await expect(page.getByText('слушаю')).toBeVisible();

    await page.evaluate(() => (window as unknown as { __say: (t: string) => void })
      .__say(`ответь одним словом ${Date.now() % 1000}`));
    await expect(page.getByText('думает…')).toBeVisible({ timeout: 8_000 });
    const recsWhileThinking = await page.evaluate(() => (window as unknown as { __recs: unknown[] }).__recs.length);

    await expect(page.getByText('отвечает…')).toBeVisible({ timeout: 240_000 });
    const recsWhileSpeaking = await page.evaluate(() => (window as unknown as { __recs: unknown[] }).__recs.length);
    expect(recsWhileSpeaking, 'микрофон не открывается под серверную озвучку').toBe(recsWhileThinking);

    // Никакого ручного «синтезатор замолчал» — путь mp3 обязан закрыться сам
    await expect(page.getByText('слушаю')).toBeVisible({ timeout: 60_000 });
    expect(ttsCalls.filter(s => s === 200).length, 'озвучка шла через /api/tts').toBeGreaterThan(0);
    const spokenByBrowser = await page.evaluate(() => (window as unknown as { __utts: unknown[] }).__utts.length);
    expect(spokenByBrowser, 'на серверном пути браузерный синтез не подключается').toBe(0);
  });

  test('в разговоре кнопка всегда останавливает: прерывает ход и выходит из режима', async ({ page }) => {
    await openChat(page, token, chatId);
    await page.getByRole('button', { name: 'Режим разговора' }).click();
    await expect(page.getByText('слушаю')).toBeVisible();

    // В петле кнопка режима уступает место ОДНОЙ «Остановить разговор» — и так в любой
    // фазе: разбираться на ходу, что означает кнопка сейчас, человек не может
    const stopTalk = page.getByRole('button', { name: 'Остановить разговор' });
    await expect(stopTalk).toBeVisible();
    await expect(page.getByRole('button', { name: 'Режим разговора' })).toBeHidden();

    await page.evaluate(() => (window as unknown as { __say: (t: string) => void })
      .__say(`посчитай до пяти ${Date.now() % 1000}`));
    await expect(page.getByText('думает…')).toBeVisible({ timeout: 8_000 });
    await expect(stopTalk).toBeVisible();

    // Один тап делает всё сразу: обрывает ход и выходит из режима, а не возвращает
    // петлю в слушание
    await stopTalk.click();
    await expect(page.getByRole('button', { name: 'Режим разговора' })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('слушаю')).toBeHidden();
  });

  test('реплика «ты ещё здесь?» звучит при закрытом микрофоне', async ({ page }) => {
    await openChat(page, token, chatId);
    // Чат общий на весь файл: ход предыдущего сценария мог ещё идти, а во время хода
    // кнопки режима в полосе нет — ждём именно её
    const voiceBtn = page.getByRole('button', { name: 'Режим разговора' });
    await expect(voiceBtn).toBeVisible({ timeout: 120_000 });
    // Ход предыдущего сценария мог ещё догорать: кнопка режима возвращается по снятию
    // isGenerating, а гейт старта смотрит на него же — клик в этот зазор отбивается тостом
    // «идёт ответ». Поэтому жмём до фактического старта петли, а не ровно один раз
    await expect.poll(async () => {
      if (await page.getByText('слушаю').isVisible()) return true;
      await voiceBtn.click().catch(() => { /* кнопка перерисовалась — повторим на следующем тике */ });
      return false;
    }, { message: 'петля должна стартовать', timeout: 60_000, intervals: [1000] }).toBe(true);

    // Три бесплодных цикла подряд (перезапуск распознавания дебаунсится 1.5 с)
    for (let i = 0; i < 3; i++) {
      await expect.poll(() => recCount(page), { timeout: 10_000 }).toBeGreaterThan(i);
      await page.evaluate(() => (window as unknown as { __barrenCycle: () => void }).__barrenCycle());
    }

    // Реплика ушла в синтезатор — и микрофон на её время закрыт: иначе она распозналась
    // бы как речь человека, ушла в чат и петля заговорила бы сама с собой
    await expect.poll(
      async () => (await spoken(page)).some(t => t.includes('ещё здесь')),
      { message: 'петля должна произнести «Ты ещё здесь?»', timeout: 15_000 },
    ).toBe(true);
    const recsWhileNotice = await recCount(page);
    expect(await page.evaluate(() => (window as unknown as { __speaking: () => boolean }).__speaking())).toBe(true);
    await page.waitForTimeout(3000);
    expect(await recCount(page), 'микрофон не открывается под реплику петли').toBe(recsWhileNotice);

    // Реплика дочитана — петля снова слушает
    await page.evaluate(() => (window as unknown as { __finishSpeech: () => void }).__finishSpeech());
    await expect(page.getByText('слушаю')).toBeVisible({ timeout: 10_000 });
    await expect.poll(() => recCount(page), { timeout: 10_000 }).toBeGreaterThan(recsWhileNotice);
  });

  test('тап по кнопке во время озвучки обрывает её, и только потом открывается микрофон', async ({ page }) => {
    await openChat(page, token, chatId);
    // Голосовой режим персистится: ответ читается вслух и без петли — самый частый вход
    // в этот сценарий
    await setVoiceMode(page, true);
    await page.reload();
    await expect(page.locator('textarea.cc-composer-input')).toBeVisible({ timeout: 20_000 });

    await page.locator('textarea.cc-composer-input').fill(`ответь одним словом ${Date.now() % 1000}`);
    await page.locator('textarea.cc-composer-input').press('Enter');
    await expect.poll(
      () => page.evaluate(() => (window as unknown as { __speaking: () => boolean }).__speaking()),
      { message: 'ответ должен читаться вслух', timeout: 240_000 },
    ).toBe(true);

    const recsWhileSpeaking = await recCount(page);
    await page.getByRole('button', { name: 'Режим разговора' }).click();
    // Тап означает «хватит читать, говорю я»: озвучка обрывается…
    await expect.poll(
      () => page.evaluate(() => (window as unknown as { __speaking: () => boolean }).__speaking()),
      { message: 'озвучка должна оборваться тапом', timeout: 5_000 },
    ).toBe(false);
    // …и только после этого открывается микрофон (под играющий звук гвард его не пустил бы)
    await expect(page.getByText('слушаю')).toBeVisible({ timeout: 10_000 });
    await expect.poll(() => recCount(page), { timeout: 10_000 }).toBeGreaterThan(recsWhileSpeaking);
  });

  test('отказ отправки возвращает петлю в слушание с тостом', async ({ page }) => {
    await openChat(page, token, chatId);
    const voiceBtn = page.getByRole('button', { name: 'Режим разговора' });
    await expect(voiceBtn).toBeVisible({ timeout: 120_000 });
    await voiceBtn.click();
    await expect(page.getByText('слушаю')).toBeVisible({ timeout: 15_000 });

    // Механику взвели уже ВНУТРИ петли: «Командная реализация» вне проекта требует
    // состава, поэтому отправка не состоится и ход не уйдёт. Петля обязана вернуться
    // слушать сразу по ответу композера, а не стоять минуту до сторожа бездействия
    await page.locator('button[title="Обсудить с командой"]').click();
    await page.getByRole('button', { name: /Командная реализация/ }).click();

    const recsBefore = await recCount(page);
    await page.evaluate(() => (window as unknown as { __say: (t: string) => void }).__say('сделай фичу'));

    await expect(page.getByText('Сообщение не ушло')).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => recCount(page), { timeout: 10_000 }).toBeGreaterThan(recsBefore);
  });

  test('после выхода из разговора обычная диктовка снова пишет в поле', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', e => errors.push('pageerror: ' + e.message));
    page.on('console', m => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
    await openChat(page, token, chatId);
    const voiceBtn = page.getByRole('button', { name: 'Режим разговора' });
    await expect(voiceBtn).toBeVisible({ timeout: 120_000 });
    await expect.poll(async () => {
      if (await page.getByText('слушаю').isVisible()) return true;
      await voiceBtn.click().catch(() => { /* перерисовалась — повторим */ });
      return false;
    }, { message: 'петля должна стартовать', timeout: 60_000, intervals: [1000] }).toBe(true);

    // Выходим одной кнопкой
    await page.getByRole('button', { name: 'Остановить разговор' }).click();
    await expect(voiceBtn).toBeVisible({ timeout: 15_000 });

    // Липкий клавиатурный фолбэк не должен взводиться петлёй: он живёт в localStorage
    // и молча превращает микрофон в «просто фокус поля» на все следующие сессии
    expect(await page.evaluate(() => localStorage.getItem('micKeyboardFallback')),
      'петля не должна включать клавиатурный фолбэк').toBeNull();

    // Обычная диктовка: распознанное идёт в поле, а не в буфер петли
    const recsBeforeMic = await recCount(page);
    await page.getByRole('button', { name: 'Голосовой ввод' }).click();
    // Запись реально открылась? Полоса на время диктовки подменяется на ✕/✓ — если их нет,
    // значит startMic вышел по гварду, а распознаватель создал кто-то другой
    const flagAfter = await page.evaluate(() => localStorage.getItem('micKeyboardFallback'));
    const recsAfterClick = await recCount(page);
    await expect(page.getByRole('button', { name: 'Отменить запись' }),
      `кнопка микрофона должна открыть запись (фолбэк-флаг: ${flagAfter}, распознавателей: ${recsBeforeMic}→${recsAfterClick})`,
    ).toBeVisible({ timeout: 5_000 });
    await expect.poll(() => recCount(page), {
      message: 'кнопка микрофона обязана открыть новый распознаватель',
      timeout: 10_000,
    }).toBeGreaterThan(recsBeforeMic);
    // Бьём в распознаватель, открытый ИМЕННО кнопкой микрофона: если петля втихую
    // продолжает свои циклы, «последний» распознаватель окажется её, и диагноз уплывёт
    const micRecIdx = (await recCount(page)) - 1;
    await page.evaluate((i: number) => {
      const r = (window as unknown as { __recs: Record<string, unknown>[] }).__recs[i] as unknown as
        { onresult?: (e: unknown) => void; onend?: () => void };
      r.onresult?.({ results: { length: 1, 0: { isFinal: true, 0: { transcript: 'проверка диктовки' } } } });
      r.onend?.();
    }, micRecIdx);
    await expect(page.locator('textarea.cc-composer-input'),
      'ошибки страницы: ' + (errors.join(' | ') || 'нет')).toHaveValue(/проверка диктовки/, { timeout: 10_000 });
    await page.locator('textarea.cc-composer-input').fill('');
  });

  test('провал PUT voiceMode гасит петлю', async ({ page }) => {
    await openChat(page, token, chatId);
    // Режим на чате уже включён предыдущим тестом — выключаем через API, чтобы тап
    // действительно пошёл включать (и упёрся в наш перехват)
    await setVoiceMode(page, false);
    await page.reload();
    await expect(page.locator('textarea.cc-composer-input')).toBeVisible({ timeout: 20_000 });

    await page.route('**/api/chats/*', route =>
      route.request().method() === 'PUT' ? route.fulfill({ status: 500, body: '{}' }) : route.continue());

    await page.getByRole('button', { name: 'Режим разговора' }).click();
    await expect(page.getByText('Не удалось включить голосовой режим')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('слушаю')).toBeHidden();
  });
});
