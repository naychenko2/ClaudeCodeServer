// ChildTimeBadge (KM-F3): тексты «спокойного времени», тон, полоса и доступность.
// Окружение node (без jsdom), SSR-рендер — как в SharedBadge.test.ts. Видимый текст
// плашки в разметке — «>текст<» внутри span; aria-label — значение атрибута.
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ChildTimeBadge, type ChildTimeBadgeProps } from './ChildTimeBadge';
import type { ChildStatus } from './types';

const status = (over: Partial<ChildStatus> = {}): ChildStatus => ({
  state: 'ok',
  space: 'lessons',
  minutesLeft: 34,
  windowEndsAt: null,
  nextWindowAt: null,
  warnMinutes: 5,
  maxAttachmentBytes: 10 * 1024 * 1024,
  attachmentTypes: [],
  rulesAgreedAt: null,
  dailyMinutes: 60, windows: [], minutesToday: 0,
  ...over,
});

const render = (props: ChildTimeBadgeProps): string => renderToStaticMarkup(<ChildTimeBadge {...props} />);

describe('остаток минут', () => {
  it('34 мин — «Ещё около 35 мин», полная фраза в aria-label', () => {
    const html = render({ status: status({ minutesLeft: 34 }), totalMinutes: 45 });
    expect(html).toContain('Ещё около ');
    expect(html).toContain('>35 мин<');
    expect(html).toContain('aria-label="Осталось около 35 минут"');
  });

  it('округление до 5: 24→25, 38→40; 11 — уже «около 10»', () => {
    expect(render({ status: status({ minutesLeft: 24 }) })).toContain('>25 мин<');
    expect(render({ status: status({ minutesLeft: 38 }) })).toContain('>40 мин<');
    expect(render({ status: status({ minutesLeft: 11 }) })).toContain('>10 мин<');
  });

  it('≤10 — точно и со склонением: 1 минута, 3 минуты, 8 минут, 10 минут', () => {
    expect(render({ status: status({ minutesLeft: 1 }) })).toContain('>1 минута<');
    expect(render({ status: status({ minutesLeft: 3 }) })).toContain('>3 минуты<');
    expect(render({ status: status({ minutesLeft: 8 }) })).toContain('>8 минут<');
    // 10 — верхняя граница точной ветки: при `>=10` она ушла бы в округление
    // («10 мин» через roundTo5), и эта проверка покраснела бы.
    expect(render({ status: status({ minutesLeft: 10 }) })).toContain('>10 минут<');
  });

  it('warn при 5 — «Ещё 5 минут», тон warning: плашка и полоса в warning-токенах', () => {
    const html = render({ status: status({ state: 'warn', minutesLeft: 5 }), totalMinutes: 45 });
    expect(html).toContain('Ещё ');
    expect(html).toContain('>5 минут<');
    expect(html).toContain('aria-label="Осталось 5 минут"');
    // Тон warning — пары токенов Badge (warningBg/warningText) и заливка полосы C.warning
    expect(html).toContain('var(--c-warning-bg)');
    expect(html).toContain('var(--c-warning-text)');
    expect(html).toContain('var(--c-warning)');
  });

  // Доля — от фактического остатка, не от округлённого числа текста: 34 из 45 → 76
  // (в макете 78% посчитано от «35» — на 48-пиксельной полосе это меньше пикселя).
  it('полоса — доля остатка в лимите: 34 из 45 → 76, 5 из 45 → 11; перерасход не выходит за 100', () => {
    expect(render({ status: status({ minutesLeft: 34 }), totalMinutes: 45 })).toContain('aria-valuenow="76"');
    expect(render({ status: status({ state: 'warn', minutesLeft: 5 }), totalMinutes: 45 })).toContain('aria-valuenow="11"');
    expect(render({ status: status({ minutesLeft: 50 }), totalMinutes: 45 })).toContain('aria-valuenow="100"');
  });

  it('без знаменателя полосы нет — полоса без знаменателя была бы ложью о времени', () => {
    const html = render({ status: status({ minutesLeft: 34 }) });
    expect(html).not.toContain('role="progressbar"');
    expect(html).toContain('>35 мин<');
  });
});

describe('без лимита минут — только конец окна', () => {
  it('minutesLeft=null — «до 19:00» из литеральных часов строки, без перевода на часы браузера', () => {
    // Смещение +05:00 нарочно отличное от таймзоны машины: fixture в таймзоне машины
    // (например +03:00 на MSK-машине) даёт «19:00» и при неверном переводе через Date.
    const html = render({ status: status({ minutesLeft: null, windowEndsAt: '2026-11-17T19:00:00+05:00' }) });
    expect(html).toContain('до 19:00');
    expect(html).toContain('aria-label="Разрешённое время до 19:00"');
    expect(html).not.toContain('role="progressbar"');
  });

  it('ни минут, ни окна — плашки нет вовсе', () => {
    expect(render({ status: status({ minutesLeft: null, windowEndsAt: null }) })).toBe('');
  });
});

describe('телефон (compact)', () => {
  it('без полосы, короткий текст; aria-label остаётся полной фразой', () => {
    const html = render({ status: status({ minutesLeft: 34 }), totalMinutes: 45, compact: true });
    expect(html).not.toContain('role="progressbar"');
    expect(html).toContain('>35 мин<');
    expect(html).not.toContain('Ещё около');
    expect(html).toContain('aria-label="Осталось около 35 минут"');
  });

  it('warn на телефоне — «5 мин», тон warning остаётся', () => {
    const html = render({ status: status({ state: 'warn', minutesLeft: 5 }), totalMinutes: 45, compact: true });
    expect(html).not.toContain('role="progressbar"');
    expect(html).toContain('>5 мин<');
    expect(html).toContain('var(--c-warning-bg)');
    expect(html).toContain('aria-label="Осталось 5 минут"');
  });
});

describe('ни красного, ни секунд — ни в одном состоянии', () => {
  const STATES: ChildStatus['state'][] = ['ok', 'warn', 'time_up', 'out_of_window', 'rules_pending', 'budget_exhausted'];

  it('6 состояний × {34 мин, 5 мин, без лимита}: tone danger и секунды не встречаются', () => {
    for (const state of STATES) {
      for (const minutesLeft of [34, 5, null]) {
        for (const withWindow of [false, true]) {
          const html = render({
            status: status({
              state,
              minutesLeft,
              windowEndsAt: withWindow ? '2026-11-17T19:00:00+03:00' : null,
            }),
            totalMinutes: 45,
          });
          // Тона danger нет нигде: ни пары плашки, ни заливки полосы
          expect(html).not.toContain('var(--c-danger');
          // Секунд нет нигде: ни словесно, ни в формате чч:мм:сс
          expect(html).not.toMatch(/\d+\s*сек/);
          expect(html).not.toMatch(/\d+:\d\d:\d\d/);
        }
      }
    }
  });

  it('состояния кроме ok/warn — плашки нет: «время вышло», «вне окна» и правила — отдельные экраны', () => {
    for (const state of ['time_up', 'out_of_window', 'rules_pending', 'budget_exhausted'] as const) {
      expect(render({ status: status({ state, minutesLeft: 34 }), totalMinutes: 45 })).toBe('');
    }
  });
});

it('плашка не интерактивна и озвучена как статус', () => {
  const html = render({ status: status({ minutesLeft: 34 }), totalMinutes: 45 });
  expect(html).toContain('role="status"');
  expect(html).not.toContain('<button');
});
