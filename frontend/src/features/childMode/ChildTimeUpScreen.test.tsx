// KM-F4: экран «Время вышло» — три состояния, спокойный текст и ровно четыре пункта
// «Чем заняться без экрана». Окружение vitest — node (vitest.config.ts), jsdom нет,
// поэтому рендер статический (react-dom/server), как в TeamPlanScheme.test.ts: меряем
// структуру и тексты, пиксели и ширину 360 — задача ui_probe.
// useIsMobile в статике даёт false (matchMedia нет) → проверяется десктоп-ветка;
// мобильная безопасность доказывается структурно: у списка нет сетки колонок вовсе.
// «Сегодня/завтра» в строке «когда откроются» — на фиксированном системном времени.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ChildTimeUpScreen } from './ChildTimeUpScreen';
import { C } from '../../lib/design';
import type { ChildLimitsStateKind, ChildStatus } from './types';

// «Сейчас» теста: 17 ноября 2026, 10:00 UTC = 13:00 в +03:00 — день 17.11 в таймзоне ребёнка.
const NOW_UTC = '2026-11-17T10:00:00Z';

function status(over: Partial<ChildStatus> = {}): ChildStatus {
  return {
    state: 'time_up', space: 'lessons', minutesLeft: 0,
    windowEndsAt: null, nextWindowAt: null, warnMinutes: 5,
    maxAttachmentBytes: 10 * 1024 * 1024,
    attachmentTypes: ['image/png'], rulesAgreedAt: '2026-11-16T12:00:00+03:00',
    dailyMinutes: 60, windows: [], minutesToday: 0,
    ...over,
  };
}

const render = (state: ChildLimitsStateKind, over: Partial<ChildStatus> = {}, minutesToday = 42) =>
  renderToStaticMarkup(createElement(ChildTimeUpScreen, { status: status({ state, ...over }), minutesToday }));

// Пункты «Чем заняться без экрана» — строки списка; больше <li> в компоненте нет нигде.
const items = (html: string) => (html.match(/<li\b/g) ?? []).length;

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date(NOW_UTC));
});
afterEach(() => {
  vi.useRealTimers();
});

describe('ChildTimeUpScreen: три состояния конца дня', () => {
  it('time_up: «На сегодня с уроками всё», итог дня и четыре пункта', () => {
    const html = render('time_up');
    expect(html).toContain('На сегодня с уроками всё');
    expect(html).toContain('42 минуты');
    expect(html).toContain('Итог дня');
    expect(items(html)).toBe(4);
    // Порядок по контракту: заголовок → итог → «Чем заняться без экрана».
    expect(html.indexOf('На сегодня с уроками всё')).toBeLessThan(html.indexOf('Итог дня'));
    expect(html.indexOf('Итог дня')).toBeLessThan(html.indexOf('Чем заняться без экрана'));
    // Обещания времени здесь нет: nextWindowAt при time_up не приходит (И3).
    expect(html).not.toContain('Уроки снова откроются');
  });

  it('out_of_window: «Уроки отдыхают» и когда откроются — из nextWindowAt', () => {
    const html = render('out_of_window', { nextWindowAt: '2026-11-18T15:00:00+03:00' }, 0);
    expect(html).toContain('Уроки отдыхают');
    expect(html).toContain('Уроки снова откроются завтра в 15:00');
    expect(items(html)).toBe(4);
    // Дня ещё нет — итога дня тоже нет.
    expect(html).not.toContain('Итог дня');
  });

  it('budget_exhausted: та же заставка «Уроки отдыхают», без обещаний и без итога', () => {
    const html = render('budget_exhausted');
    expect(html).toContain('Уроки отдыхают');
    expect(items(html)).toBe(4);
    expect(html).not.toContain('Итог дня');
    expect(html).not.toContain('Уроки снова откроются');
  });

  it('заголовок свой у каждого состояния: три разных заставки', () => {
    const titles = [
      render('time_up'),
      render('out_of_window'),
      render('budget_exhausted'),
    ];
    expect(titles[0]).toContain('На сегодня с уроками всё');
    expect(titles[1]).toContain('Уроки отдыхают');
    expect(titles[2]).toContain('Уроки отдыхают');
    // time_up не надевает чужую заставку и наоборот.
    expect(titles[0]).not.toContain('Уроки отдыхают');
    expect(titles[1]).not.toContain('На сегодня с уроками всё');
  });
});

describe('ChildTimeUpScreen: спокойный текст и холст', () => {
  // Взрослый отказ — ошибка или 403; ребёнку — спокойный конец (записка, «Новое №3»).
  const FORBIDDEN = ['ошибк', 'лимит', 'бюджет', '403'];

  it('ни в одном состоянии нет слов отказа', () => {
    for (const state of ['time_up', 'out_of_window', 'budget_exhausted'] as const) {
      const html = render(state);
      for (const word of FORBIDDEN) expect(html, `${state}: слово «${word}»`).not.toContain(word);
    }
  });

  it('тона danger нет; заставка — тёплый warning-круг', () => {
    for (const state of ['time_up', 'out_of_window', 'budget_exhausted'] as const) {
      const html = render(state);
      expect(html).not.toContain('danger');
      expect(html).toContain(C.warningBg);
      expect(html).toContain(C.warningText);
    }
  });

  it('горизонтальной прокрутке не взяться: нигде нет сетки колонок', () => {
    const html = render('time_up');
    expect(html).not.toContain('gridTemplateColumns');
    expect(html).not.toContain('repeat(');
  });

  it('пункты без цвета предмета: нейтральный холст заставки и текста', () => {
    const html = render('time_up');
    // Пары предмета из ChildHome (info/rose/success-подложки глифов) здесь не появляются.
    expect(html).not.toContain(C.infoBg);
    expect(html).not.toContain(C.rose);
    expect(html).not.toContain(C.successBg);
    expect(html).not.toContain(C.planLight);
  });
});

describe('ChildTimeUpScreen: чужие состояния — экран молчит', () => {
  // ok/warn/rules_pending рисует другой экран. Фолбэк на «Уроки отдыхают» здесь был
  // ложью: ребёнок с warn (урок идёт, осталось 5 минут) прочитал бы «время уроков
  // закрыто» (вердикт KM-F4). Пустой вывод — зафиксированное поведение: тест краснеет,
  // если фолбэк вернётся в любую форму.
  it('ok/warn/rules_pending: ничего не рисуется — ни заставки, ни обещания окна', () => {
    for (const state of ['ok', 'warn', 'rules_pending'] as const) {
      const html = render(state, { minutesLeft: 5, windowEndsAt: '2026-11-17T18:00:00+03:00' });
      expect(html, `${state}: не пустой вывод`).toBe('');
    }
  });
});

describe('ChildTimeUpScreen: «когда откроются» по таймзоне ребёнка', () => {
  const open = (iso: string) => render('out_of_window', { nextWindowAt: iso });

  it('тот же день — «сегодня», следующий — «завтра»', () => {
    expect(open('2026-11-17T19:00:00+03:00')).toContain('Уроки снова откроются сегодня в 19:00');
    expect(open('2026-11-18T15:00:00+03:00')).toContain('Уроки снова откроются завтра в 15:00');
  });

  it('дальше завтра (окно после выходных) — называем день недели', () => {
    // 20.11.2026 — пятница.
    expect(open('2026-11-20T15:00:00+03:00')).toContain('Уроки снова откроются в пятницу в 15:00');
  });

  it('дата берётся из строки, а не из момента: −08:00 окно «сегодня», хоть по UTC это уже завтра', () => {
    // 17.11 23:00−08:00 = 18.11 07:00 UTC. По дате строки и смещению строки ребёнок
    // видит окно сегодня; пересчёт момента в UTC сказал бы «завтра».
    expect(open('2026-11-17T23:00:00-08:00')).toContain('Уроки снова откроются сегодня в 23:00');
  });

  it('nextWindowAt нет — обещания времени нет', () => {
    expect(render('out_of_window')).not.toContain('Уроки снова откроются');
  });
});

describe('ChildTimeUpScreen: итог дня', () => {
  it('минуты склоняются', () => {
    expect(render('time_up', {}, 1)).toContain('1 минута');
    expect(render('time_up', {}, 2)).toContain('2 минуты');
    expect(render('time_up', {}, 5)).toContain('5 минут');
    expect(render('time_up', {}, 11)).toContain('11 минут');
    expect(render('time_up', {}, 21)).toContain('21 минута');
  });

  it('нуля нет: без минут нет и строки итога', () => {
    const html = render('time_up', {}, 0);
    expect(html).not.toContain('Итог дня');
    expect(html).not.toContain('0 минут');
    // Заставка и четыре пункта остаются.
    expect(html).toContain('На сегодня с уроками всё');
    expect(items(html)).toBe(4);
  });
});
