// KM-F6: онбординг правил — порядок пяти шагов, gate двух отметок, один agree на два клика
// и поведение при отказе. Окружение — node + linkedom, как в ChildHintsPanel.test: Modal
// рендерится порталом в document.body, поэтому запросы идут по всему документу, а после
// каждого теста окно снимается (иначе следующий тест видел бы чужую модалку).
// Мобильная ветка — переключателем matchMedia, тач-цели — по inline высоте контролов;
// шторка и ширина 360 — задача ui_probe (сторож по пути правки).
// «Необработанных rejection нет» не слушается отдельно: обработчик agree весь в try/catch,
// а неохваченный rejection сам краснит прогон vitest.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import { CHILD_RULES_STEPS, ChildRulesOnboarding, PHOTO_NOTE } from './ChildRulesOnboarding';
import type { ChildRulesBrief } from './ChildRulesOnboarding';
import { C } from '../../lib/design';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body></body></html>');
// Мобильная ветка: useIsMobile и useIsMobileModal читают window.matchMedia при mount.
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({
    matches: mobile, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
// TZ — для теста «дата согласия»: process вне типов браузерной сборки (test.tsx входит в
// tsconfig.app, node-типы в неё не входят). Доступ через globalThis, как у IS_REACT_ACT_ENVIRONMENT.
const nodeEnv = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env;

// childApi.rulesAgree: ответ по индексу вызова — Error сразу отказывает promise (как
// отказывает request() из lib/offline), записи нет → promise держится in-flight
// (на этом проверяются блокировка кнопки и закрытие до ответа).
let calls: { sessionId: string; summary: string }[] = [];
let answers: (Error | object)[] = [];
let pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
let callIdx = 0;
let agreedCalls = 0;
let closedCalls = 0;
let roots: ReturnType<typeof createRoot>[] = [];

vi.mock('./api', () => ({
  childApi: {
    rulesAgree: (sessionId: string, summary: string) => {
      const i = callIdx++;
      calls.push({ sessionId, summary });
      const a = answers[i];
      if (a instanceof Error) return Promise.reject(a);
      if (a) return Promise.resolve(a);
      return new Promise((res, rej) => { pending.set(i, { resolve: res, reject: rej }); });
    },
  },
}));

const httpErr = (status: number, code?: string) =>
  Object.assign(new Error('отказ'), { status, body: code ? { code } : {} });

function brief(over: Partial<ChildRulesBrief> = {}): ChildRulesBrief {
  return {
    childName: 'Лиза',
    dailyMinutes: 40,
    windows: [{ from: '15:00', to: '19:00', weekdays: [1, 2, 3, 4, 5] }],
    warnMinutes: 5,
    sessionId: 'rules-chat-1',
    ...over,
  };
}

async function mount(mode: 'agree' | 'review', rules = brief()) {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  roots.push(root);
  const render = (open: boolean) => act(async () => {
    root.render(createElement(ChildRulesOnboarding, {
      open,
      mode,
      rules,
      onAgreed: () => { agreedCalls += 1; },
      onClose: () => { closedCalls += 1; },
    }));
  });
  await render(true);
  // Тот же смонтированный экземпляр с open=false: на этом проверяется закрытие до ответа,
  // а не сброс состояния новым монтированием.
  return { setOpen: render };
}

const all = <T extends Element>(sel: string) => Array.from(doc.querySelectorAll(sel)) as T[];
const titleText = () => (doc.querySelector('h2')?.textContent ?? '').trim();
const btns = () => all<HTMLButtonElement>('button');
const buttonByText = (text: string) => btns().find((b) => (b.textContent ?? '').trim() === text) ?? null;
const boxes = () => all<HTMLElement>('[role="checkbox"]');
const bars = () => all<HTMLElement>('[role="progressbar"]');
const fillOf = (bar: HTMLElement) => bar.firstElementChild as HTMLElement;

async function click(el: HTMLElement) {
  await act(async () => { el.click(); });
}

// Ответ + выгнать цепочку then/catch обработчика (несколько hops микротасков с запасом).
async function settle(fn: () => void) {
  await act(async () => {
    fn();
    for (let i = 0; i < 6; i++) await Promise.resolve();
  });
}

// Дойти до последнего шага теми же кнопками, что видит ребёнок: в agree на шаге 2
// основное действие называется «Нам подходит» (макет), в review — везде «Дальше».
const nextLabels = (mode: 'agree' | 'review') =>
  mode === 'agree' ? ['Дальше', 'Нам подходит', 'Дальше', 'Дальше'] : ['Дальше', 'Дальше', 'Дальше', 'Дальше'];

async function toLastStep(mode: 'agree' | 'review' = 'agree') {
  for (const label of nextLabels(mode)) {
    const b = buttonByText(label);
    expect(b, `кнопка «${label}» на своём шаге`).toBeTruthy();
    await click(b!);
  }
  expect(titleText()).toContain(CHILD_RULES_STEPS[4]);
}

async function markBoth() {
  for (const box of boxes()) await click(box);
  expect(boxes().map((b) => b.getAttribute('aria-checked'))).toEqual(['true', 'true']);
}

beforeEach(() => {
  calls = []; answers = []; pending = new Map(); callIdx = 0;
  agreedCalls = 0; closedCalls = 0; mobile = false;
});

afterEach(async () => {
  for (const root of roots) await act(async () => { root.unmount(); });
  roots = [];
});

describe('ChildRulesOnboarding: пять шагов в порядке макета', () => {
  it('шаги идут по порядку CHILD_RULES_STEPS: «Дальше» и «Нам подходит» ведут от 1 к 5', async () => {
    await mount('agree');
    const seen = [titleText()];
    for (const label of ['Дальше', 'Нам подходит', 'Дальше', 'Дальше']) {
      const b = buttonByText(label);
      expect(b, `кнопка «${label}»`).toBeTruthy();
      await click(b!);
      seen.push(titleText());
    }
    expect(seen.length).toBe(5);
    CHILD_RULES_STEPS.forEach((title, i) => {
      expect(seen[i]).toContain(title);
      expect(seen[i]).toContain(`шаг ${i + 1} из 5`);
    });
    // Каждый шаг назван один раз: повтор или пропуск был бы виден как дубликат.
    expect(new Set(seen).size).toBe(5);
  });

  it('над заголовком — 5 сегментов: пройденные success, текущий accent, будущие пусты', async () => {
    await mount('agree');
    await click(buttonByText('Дальше')!);
    await click(buttonByText('Нам подходит')!);
    const segs = bars();
    expect(segs.length).toBe(5);
    const colors = segs.map((s) => fillOf(s).style.background);
    const widths = segs.map((s) => fillOf(s).style.width);
    expect(colors.slice(0, 2)).toEqual([C.success, C.success]);
    expect(colors[2]).toBe(C.accent);
    expect(widths.slice(0, 3)).toEqual(['100%', '100%', '100%']);
    expect(widths.slice(3)).toEqual(['0%', '0%']);
  });

  it('на шаге 2 — лимиты из профиля: минуты, дни и часы окна', async () => {
    await mount('agree');
    await click(buttonByText('Дальше')!);
    const html = doc.body.innerHTML;
    expect(html).toContain('40 минут');
    expect(html).toContain('пн–пт');
    expect(html).toContain('с 15:00 до 19:00');
    // Порог предупреждения — из warnMinutes, не из текста макета.
    expect(html).toContain('За 5 минут до конца я предупрежу');
  });

  it('дни окна: сплошной отрезок — «вт–чт», редкий набор — перечислением, все дни — «каждый день»', async () => {
    const cases: [number[] | undefined, string][] = [
      [[2, 3, 4], 'вт–чт'],
      [[1, 3, 5], 'пн, ср, пт'],
      [[1, 2, 3, 4, 5, 6, 7], 'каждый день'],
      [undefined, 'каждый день'],
    ];
    for (const [weekdays, expected] of cases) {
      await mount('agree', brief({ windows: [{ from: '15:00', to: '19:00', weekdays }] }));
      await click(buttonByText('Дальше')!);
      expect(doc.body.innerHTML, `weekdays=${weekdays}`).toContain(expected);
    }
  });

  it('без дневного лимита плитки минут нет: пустой «0 минут» читался бы как запрет', async () => {
    await mount('agree', brief({ dailyMinutes: null }));
    await click(buttonByText('Дальше')!);
    const html = doc.body.innerHTML;
    expect(html).not.toContain('0 минут');
    expect(html).toContain('пн–пт');
  });

  it('при пороге предупреждения 0 строки о предупреждении нет: остальной текст шага на месте', async () => {
    await mount('agree', brief({ warnMinutes: 0 }));
    await click(buttonByText('Дальше')!);
    const html = doc.body.innerHTML;
    expect(html).not.toContain('до конца я предупрежу');
    expect(html).toContain('Остаток называю крупно');
  });

  it('review: последний шаг — «Закрыть», без отметок и без запроса', async () => {
    const { setOpen } = await mount('review', brief({ agreedAt: '2026-11-16T12:00:00+03:00' }));
    await toLastStep('review');
    expect(boxes().length).toBe(0);
    expect(titleText()).toContain('Наши правила');
    expect(doc.body.innerHTML).toContain('Договорились 16 ноября: Лиза и папа');
    const close = buttonByText('Закрыть')!;
    expect(close).toBeTruthy();
    expect(close.disabled).toBe(false);
    await click(close);
    expect(closedCalls).toBe(1);
    expect(calls.length).toBe(0);
    await setOpen(false);
  });

  it('дата согласия — по строке профиля: таймзона браузера не сдвигает день', async () => {
    // Зона машины не важна: тест сам ставит зону браузера. Дата лежит в зоне ребёнка:
    // 00:30 по +03:00 — предыдущий день в UTC. Реализация с переводом в зону браузера
    // дала бы «15 ноября».
    const prevTZ = nodeEnv?.TZ;
    if (nodeEnv) nodeEnv.TZ = 'UTC';
    try {
      await mount('review', brief({ agreedAt: '2026-11-16T00:30:00+03:00' }));
      await toLastStep('review');
      expect(doc.body.innerHTML).toContain('Договорились 16 ноября: Лиза и папа');
    } finally {
      if (nodeEnv) {
        if (prevTZ === undefined) delete nodeEnv.TZ;
        else nodeEnv.TZ = prevTZ;
      }
    }
  });
});

// Шаг «Что видит папа» и строка о сигнале на шаге 4: тексты должны быть правдой —
// куратор видит все уроки ребёнка дословно (решение Андрея 19.11). Формулировки карточек —
// по постановке Ф1i.
describe('ChildRulesOnboarding: честные тексты о том, что видит папа', () => {
  // Дойти до нужного шага теми же кнопками, что видит ребёнок.
  async function toStep(n: number) {
    await mount('agree');
    for (const label of nextLabels('agree').slice(0, n - 1)) {
      const b = buttonByText(label);
      expect(b, `кнопка «${label}» на своём шаге`).toBeTruthy();
      await click(b!);
    }
    expect(titleText()).toContain(CHILD_RULES_STEPS[n - 1]);
  }

  it('«Папа видит» — уроки целиком, время и предметы', async () => {
    await toStep(3);
    const html = doc.body.innerHTML;
    expect(html).toContain('Твои уроки целиком — что ты пишешь и что отвечает помощник');
    expect(html).toContain('Сколько времени ты занималась');
    expect(html).toContain('Какие предметы и темы ты проходила');
  });

  it('«Папа не видит» — одна строка про черновики: «Ваши сообщения» и «Ошибки по одной» убраны', async () => {
    await toStep(3);
    const html = doc.body.innerHTML;
    expect(html).toContain('Черновики, которые ты не отправила');
    expect(html).not.toContain('Ваши сообщения');
    expect(html).not.toContain('Ошибки по одной');
  });

  it('на шаге «Что видит папа» нет обещания, что папа не читает разговор', async () => {
    await toStep(3);
    expect(doc.body.innerHTML).not.toMatch(/не читает|Ваш разговор/);
  });

  it('на шаге «Если что-то не так» строка о сигнале не обещает, что папа не читает разговор', async () => {
    await toStep(4);
    const html = doc.body.innerHTML;
    expect(html).toContain('Папа узнаёт, что тебе нужна помощь.');
    expect(html).not.toMatch(/не читает|Ваш разговор/);
    expect(html).toContain('Ты ни в чём не виновата');
  });
});

describe('ChildRulesOnboarding: согласие', () => {
  it('«Договорились» активна только при обеих отметках', async () => {
    await mount('agree');
    await toLastStep();
    const b = buttonByText('Договорились')!;
    expect(b).toBeTruthy();
    expect(b.disabled, 'без отметок').toBe(true);
    await click(boxes()[0]);
    expect(b.disabled, 'одна отметка').toBe(true);
    await click(boxes()[1]);
    expect(b.disabled, 'обе отметки').toBe(false);
  });

  it('отметки читаются текстом своей строки', async () => {
    await mount('agree');
    await toLastStep();
    expect(boxes().map((b) => b.getAttribute('aria-label'))).toEqual(['Я, Лиза, согласна', 'Папа рядом и тоже согласен']);
  });

  it('agree: summary — строки шага 5 плюс строка согласия, sessionId — из правил', async () => {
    await mount('agree');
    await toLastStep();
    await markBoth();
    await click(buttonByText('Договорились')!);
    await settle(() => pending.get(0)?.resolve({ rulesAgreedAt: '2026-11-17T12:00:00+03:00' }));
    expect(calls.length).toBe(1);
    expect(calls[0].sessionId).toBe('rules-chat-1');
    const summary = calls[0].summary;
    expect(summary).toContain('Я знаю, что помощник — программа, а не человек и не друг.');
    expect(summary).toContain('Я занимаюсь 40 минут в день: по будням с 15:00 до 19:00.');
    expect(summary).toContain('Решаю сама, а помощник подсказывает.');
    expect(summary).toContain('Если что-то не так, я говорю папе.');
    expect(summary).toContain('Лиза и папа согласны.');
    expect(agreedCalls).toBe(1);
  });

  it('summary не выходит за потолок ручки (400 summary_too_long): режем по границе строки', async () => {
    await mount('agree', brief({ childName: 'Л'.repeat(3000) }));
    await toLastStep();
    await markBoth();
    await click(buttonByText('Договорились')!);
    await settle(() => pending.get(0)?.resolve({ rulesAgreedAt: '2026-11-17T12:00:00+03:00' }));
    const summary = calls[0].summary;
    expect(summary.length).toBeLessThanOrEqual(2000);
    // Отсечка стоит на границе строки, а не посреди слова.
    expect(summary.endsWith('\n')).toBe(false);
    expect(summary).not.toContain('Л'.repeat(3000));
  });

  it('без дневного лимита строка договорённости — «Я занимаюсь в своё время для уроков»', async () => {
    await mount('agree', brief({ dailyMinutes: null }));
    await toLastStep();
    // Та же строка — на шаге 5 и в summary: один источник для отмеченного и записанного.
    expect(doc.body.innerHTML).toContain('Я занимаюсь в своё время для уроков');
    await markBoth();
    await click(buttonByText('Договорились')!);
    await settle(() => pending.get(0)?.resolve({ rulesAgreedAt: '2026-11-17T12:00:00+03:00' }));
    expect(calls[0].summary).toContain('Я занимаюсь в своё время для уроков: по будням с 15:00 до 19:00.');
  });

  it('double click sends one agree: после первого клика кнопка в загрузке до ответа', async () => {
    await mount('agree');
    await toLastStep();
    await markBoth();
    const b = buttonByText('Договорились')!;
    await click(b);
    expect(calls.length).toBe(1);
    // Флаг загрузки — механизм контракта: без него второй клик шлёт второй agree.
    expect(b.disabled, 'кнопка в загрузке до ответа').toBe(true);
    await click(b);
    expect(calls.length).toBe(1);
    await settle(() => pending.get(0)?.resolve({ rulesAgreedAt: '2026-11-17T12:00:00+03:00' }));
    expect(agreedCalls).toBe(1);
  });

  it('guard busy держит один запрос: два прямых вызова onConfirm — один agree', async () => {
    await mount('agree');
    await toLastStep();
    await markBoth();
    const b = buttonByText('Договорились')!;
    // Зовём onConfirm напрямую через props узла (тот же путь, что событие клика), без опоры
    // на disabled кнопки: держать запрос обязан guard, а не состояние контрола.
    const callConfirm = () => {
      const key = Object.keys(b).find((k) => k.startsWith('__reactProps$'));
      const props = key ? (b as unknown as Record<string, unknown>)[key] as { onClick?: () => void } : undefined;
      expect(props?.onClick, 'onConfirm на кнопке').toBeTruthy();
      props!.onClick!();
    };
    // Каждый вызов — свой act: после первого React перерисовывает кнопку (busy=true),
    // и второй читает уже новый замыкающий onConfirm.
    await act(async () => { callConfirm(); });
    expect(calls.length).toBe(1);
    await act(async () => { callConfirm(); });
    expect(calls.length).toBe(1);
    await settle(() => pending.get(0)?.resolve({ rulesAgreedAt: '2026-11-17T12:00:00+03:00' }));
    expect(agreedCalls).toBe(1);
  });

  it('модалку закрыли до ответа — onAgreed не зовётся', async () => {
    const { setOpen } = await mount('agree');
    await toLastStep();
    await markBoth();
    await click(buttonByText('Договорились')!);
    expect(calls.length).toBe(1);
    const cross = doc.querySelector('[aria-label="Закрыть"]') as HTMLElement;
    expect(cross).toBeTruthy();
    await click(cross);
    expect(closedCalls).toBe(1);
    await setOpen(false);
    await settle(() => pending.get(0)?.resolve({ rulesAgreedAt: '2026-11-17T12:00:00+03:00' }));
    expect(agreedCalls).toBe(0);
  });

  it('отказ 503: warning-плашка, обе отметки на месте, onAgreed не вызван, кнопка снова доступна', async () => {
    answers = [httpErr(503, 'policy_unavailable')];
    await mount('agree');
    await toLastStep();
    await markBoth();
    await click(buttonByText('Договорились')!);
    await settle(() => {});
    const notice = doc.querySelector('[role="status"]') as HTMLElement;
    expect(notice, 'warning-плашка').toBeTruthy();
    // Тон — по атрибуту style: border-шортхенд через CSSOM linkedom не читается целиком.
    expect(notice.getAttribute('style'), 'тон warning').toContain(C.warning);
    expect(doc.body.innerHTML).toContain('Не получилось сохранить');
    expect(boxes().map((b) => b.getAttribute('aria-checked'))).toEqual(['true', 'true']);
    expect(agreedCalls).toBe(0);
    expect(buttonByText('Договорились')!.disabled).toBe(false);
  });
});

describe('ChildRulesOnboarding: фото и личные данные (шаг 1)', () => {
  it('step 1 shows photo note: абзац на шаге 1 в agree и review', async () => {
    for (const mode of ['agree', 'review'] as const) {
      await mount(mode, brief({ agreedAt: '2026-11-16T12:00:00+03:00' }));
      expect(doc.body.innerHTML, mode).toContain(PHOTO_NOTE);
      const el = all<HTMLElement>('div, span, p').find((n) => (n.textContent ?? '').trim() === PHOTO_NOTE);
      expect(el, `${mode}: элемент с текстом PHOTO_NOTE`).toBeTruthy();
    }
  });

  it('на шагах 2–5 абзаца про фото нет', async () => {
    await mount('agree');
    for (const label of ['Дальше', 'Нам подходит', 'Дальше', 'Дальше']) {
      await click(buttonByText(label)!);
      expect(doc.body.innerHTML).not.toContain(PHOTO_NOTE);
    }
  });

  it('тон — подсказка: в тексте нет «опасно», «нельзя», «запрещено»', () => {
    expect(PHOTO_NOTE).not.toMatch(/опасн|нельзя|запрещ/i);
  });

  it('о голосовом вводе ни слова: ни «голос», ни «микрофон», ни «наговори»', async () => {
    await mount('agree');
    expect(doc.body.innerHTML).not.toMatch(/голос|микрофон|наговор/i);
    for (const label of ['Дальше', 'Нам подходит', 'Дальше', 'Дальше']) {
      await click(buttonByText(label)!);
      expect(doc.body.innerHTML).not.toMatch(/голос|микрофон|наговор/i);
    }
  });
});

describe('ChildRulesOnboarding: мобила (360)', () => {
  it('тач-цели шага согласия ≥ 40px: «Договорились» и обе отметки', async () => {
    mobile = true;
    await mount('agree');
    await toLastStep();
    await click(boxes()[0]);
    await click(boxes()[1]);
    const confirm = buttonByText('Договорились')!;
    expect(Number.parseFloat(confirm.style.minHeight), 'кнопка «Договорились»').toBeGreaterThanOrEqual(40);
    for (const box of boxes()) {
      expect(Number.parseFloat(box.style.width), 'ширина отметки').toBeGreaterThanOrEqual(40);
      expect(Number.parseFloat(box.style.height), 'высота отметки').toBeGreaterThanOrEqual(40);
    }
  });

  it('на узком экране нет горизонтальной прокрутки: многостолбцовых сеток нигде нет', async () => {
    mobile = true;
    await mount('agree');
    for (const label of ['Дальше', 'Нам подходит', 'Дальше', 'Дальше']) {
      await click(buttonByText(label)!);
      // 'repeat(' — только многоколоночная сетка; на мобиле каждая сетка сведена к '1fr'.
      expect(doc.body.innerHTML).not.toContain('repeat(');
    }
  });
});
