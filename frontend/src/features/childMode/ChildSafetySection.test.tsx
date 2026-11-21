// Ф2e: ChildSafetySection — раздел «Безопасность и согласие» (ADR-041 ред. 4 решение 26 —
// текст согласия и его трёхзначность; ред. 6 решения 60–61 — опекуны-кураторы, флаг
// shared-projects и рубильник инстанса; макет kids-settings-window.html, раздел `safety`).
// Окружение — node + linkedom, как в соседних .test.tsx волны: пикер ChildRoutePicker
// монтируется настоящий (тот же путь, что в ChildRoutePicker.test), смена значения — прямым
// вызовом onChange узла. Каждое «сменилось» проверяется полным циклом: действие → значение
// у родителя → повторный рендер с ним → контрол показывает новое.
// Мутации этого прогона (копия дерева, вывод — в отчёте задачи):
// (1) снятая галка отдаёт null вместо false → кейс «снята после данного согласия → false»
//     краснеет (ожидался [false]).
// (2) смена поставщика считает сброшенный пикером флаг действием согласия → кейс «смена
//     поставщика согласие не трогает» краснеет (ожидался пустой список действий).
// (3) «уведомление показывается всегда» (снято условие sharedProjectsInstanceEnabled === false
//     в ChildSafetySection.tsx:233) → кейс «рубильник инстанса» краснеет: при включённом и
//     неизвестном рубильнике плашка появилась.

import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile в пикере читает window.matchMedia при mount.
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

import { ChildSafetySection } from './ChildSafetySection';
import type { ChildRoute, ChildSettingsGuardian } from './types';

// Тот же состав, что отдаёт GET /api/admin/child-routes: пул первым, затем провайдеры
// (ChildRoutesController.cs:27-28). Для чата правил — весь список (api.md:376, решение 25).
const ROUTES: ChildRoute[] = [
  {
    providerKey: 'anthropic-api-kids', kind: 'pool', label: 'Anthropic · ключ «kids»',
    priced: true, requiresAck: false,
    models: [{ id: 'sonnet', label: 'Sonnet' }, { id: 'haiku', label: 'Haiku' }],
  },
  {
    providerKey: 'glm', kind: 'cloud', label: 'GLM (Zhipu)',
    priced: true, requiresAck: true,
    models: [{ id: 'glm-5', label: 'GLM-5' }],
  },
  {
    providerKey: 'strata', kind: 'local', label: 'Strata (локально)',
    priced: false, requiresAck: true,
    models: [{ id: 'qwen-a', label: 'Qwen A' }],
  },
];

const ACK_LABEL = 'Я понимаю риски и беру их на себя';
// Локальная ISO-строка (без Z): дата читается тем же днём в любой таймзоне прогона —
// проверка строки «Дано …» не должна зависеть от часов машины.
const ACKED_AT = '2026-11-18T12:00:00';
// Точная строка заголовка плашки «рубильника инстанса» (ChildSafetySection.tsx:234):
// отсутствие проверяется по ней и по data-testid плашки — строчная подстрока «рубильник»
// заглавному выводу компонента не соответствует и не краснеет ни на какой ошибке (ревью 5dc619cd).
const BREAKER_TITLE = 'Рубильник общих проектов на сервере выключен';

interface MountProps {
  guardians: ChildSettingsGuardian[];
  sharedProjectsInstanceEnabled?: boolean;
  userNames?: Record<string, string>;
  acknowledge: boolean | null;
  acknowledgedAt?: string | null;
  acknowledgedBy?: string | null;
  routes: ChildRoute[] | null;
  route: { providerKey: string; model: string };
  disabled?: boolean;
}

const base = (over: Partial<MountProps> = {}): MountProps => ({
  guardians: [], acknowledge: null, routes: null, route: { providerKey: '', model: '' }, ...over,
});

let ackActions: boolean[] = [];
let routeChanges: { providerKey: string; model: string }[] = [];

async function mount(props: MountProps) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  ackActions = [];
  routeChanges = [];
  const render = (p: MountProps) =>
    createElement(ChildSafetySection, {
      ...p,
      // Родитель (каркас окна) копит действия черновика: «не трогали» живёт как отсутствие
      // действия, и тест проверяет именно его.
      onAcknowledgeChange: (v: boolean) => { ackActions.push(v); },
      onRouteChange: (n: { providerKey: string; model: string }) => { routeChanges.push(n); },
    });
  await act(async () => { root.render(render(props)); });
  return {
    host: host as HTMLElement,
    // Тот же экземпляр с новыми значениями родителя: полный цикл, а не только вызов onChange.
    async apply(next: Partial<MountProps>) {
      await act(async () => { root.render(render({ ...props, ...next })); });
    },
  };
}

const rootEl = (host: HTMLElement) => host.querySelector('[data-testid="child-safety-section"]') as HTMLElement;
const hasText = (root: Element, text: string) => (root.textContent ?? '').includes(text);

const ackBox = (host: HTMLElement) =>
  rootEl(host).querySelector(`[role="checkbox"][aria-label="${ACK_LABEL}"]`) as HTMLElement | null;
const clickAck = async (host: HTMLElement) => {
  const b = ackBox(host);
  expect(b, 'галка согласия').not.toBeNull();
  await act(async () => { b!.click(); });
};
const givenLine = (host: HTMLElement) => rootEl(host).querySelector('[data-testid="consent-given"]') as HTMLElement | null;
const breakerNotice = (host: HTMLElement) => rootEl(host).querySelector('[data-testid="shared-projects-off"]');

// Поле пикера по подписи Field: обёртка Field — общий родитель label и select.
const selectByLabel = (host: HTMLElement, label: string) => {
  const lbl = Array.from(rootEl(host).querySelectorAll('label')).find((l) => (l.textContent ?? '').includes(label));
  return (lbl?.parentElement?.querySelector('select') ?? null) as Element | null;
};
const options = (sel: Element): string[] => Array.from(sel.querySelectorAll('option')).map((o) => o.getAttribute('value') ?? '');
const reactProps = (el: Element): { onChange?: (e: { target: { value: string } }) => void } | undefined => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  return key ? (el as unknown as Record<string, unknown>)[key] as { onChange?: (e: { target: { value: string } }) => void } : undefined;
};
const changeSelect = async (el: Element, v: string) => {
  await act(async () => { reactProps(el)?.onChange?.({ target: { value: v } }); });
};
const pickerAck = (host: HTMLElement) =>
  rootEl(host).querySelector('[data-testid="child-route-picker"] [role="checkbox"]') as HTMLElement | null;

describe('ChildSafetySection', () => {
  it('согласие: текст — все шесть пунктов решения 26 и галка', async () => {
    const { host } = await mount(base());
    for (const t of [
      '18+',
      'Облачный поставщик получает переписку ребёнка',
      'Локальная модель слабее держит правила и стоп-сигнал',
      'При сбое урок на один ход ответит соседняя модель из цепочки',
      'Лимиты общие со взрослыми',
      'Бюджет по подписке считается «как по API»',
    ]) {
      expect(hasText(rootEl(host), t), t).toBe(true);
    }
    expect(ackBox(host), 'галка согласия').not.toBeNull();
  });

  it('галка не тронута: действий нет, состояние — живое согласие сервера', async () => {
    // согласия нет: галка снята, «Дано» нет, ни одного действия
    const off = await mount(base());
    expect(ackBox(off.host)!.getAttribute('aria-checked'), 'без согласия — снята').toBe('false');
    expect(ackActions, 'чтение — не действие').toEqual([]);
    expect(givenLine(off.host)).toBeNull();

    // согласие дано (read-only поля ответа): галка отмечена без действия, строка «Дано …» с именем
    const on = await mount(base({ acknowledgedAt: ACKED_AT, acknowledgedBy: 'u-1', userNames: { 'u-1': 'Андрей' } }));
    expect(ackBox(on.host)!.getAttribute('aria-checked'), 'живое согласие — отмечена').toBe('true');
    expect(ackActions, 'чтение — не действие').toEqual([]);
    expect(givenLine(on.host)?.textContent, '«Дано {дата} · {кто}»').toContain('Дано 18.11.2026 · Андрей');
  });

  it('галка поставлена → true; полный цикл — отмечена; даты у нового согласия ещё нет', async () => {
    const m = await mount(base());
    await clickAck(m.host);
    expect(ackActions).toEqual([true]);
    await m.apply({ acknowledge: true });
    expect(ackBox(m.host)!.getAttribute('aria-checked')).toBe('true');
    expect(givenLine(m.host), 'момент и автора штампует сервер при сохранении').toBeNull();
  });

  it('галка снята после данного согласия → false; строка «Дано» снимается', async () => {
    const m = await mount(base({ acknowledgedAt: ACKED_AT, acknowledgedBy: 'u-1', userNames: { 'u-1': 'Андрей' } }));
    expect(ackBox(m.host)!.getAttribute('aria-checked'), 'было дано').toBe('true');
    await clickAck(m.host);
    expect(ackActions, 'отзыв').toEqual([false]);
    await m.apply({ acknowledge: false });
    expect(ackBox(m.host)!.getAttribute('aria-checked'), 'отозвано').toBe('false');
    expect(givenLine(m.host), 'отзываемое согласие уже не «дано»').toBeNull();
  });

  it('опекуны: флаг выключен — имя и «включите shared-projects у {имя}»; флаг включён — только бейдж', async () => {
    const m = await mount(base({
      guardians: [{ userId: 'u-1', sharedProjectsFlag: true }, { userId: 'u-2', sharedProjectsFlag: false }],
      userNames: { 'u-1': 'Ольга', 'u-2': 'Петя' },
    }));
    expect(hasText(rootEl(m.host), 'Ольга')).toBe(true);
    expect(hasText(rootEl(m.host), 'shared-projects включён'), 'бейдж включённого флага').toBe(true);
    expect(hasText(rootEl(m.host), 'Включите shared-projects у Петя'), 'текст с именем').toBe(true);
    expect(hasText(rootEl(m.host), 'нужен флаг shared-projects'), 'бейдж выключенного флага').toBe(true);
    // без имени строка показывает id (тот же порядок, что nameOf в Overview)
    const anon = await mount(base({ guardians: [{ userId: 'u-9', sharedProjectsFlag: false }] }));
    expect(hasText(rootEl(anon.host), 'Включите shared-projects у u-9'), 'без имени — id').toBe(true);
  });

  it('рубильник инстанса: выключен — текст про рубильник; включён или неизвестен — текста нет', async () => {
    const off = await mount(base({
      guardians: [{ userId: 'u-1', sharedProjectsFlag: true }],
      sharedProjectsInstanceEnabled: false,
    }));
    expect(breakerNotice(off.host), 'плашка при выключенном рубильнике').not.toBeNull();
    expect(hasText(rootEl(off.host), BREAKER_TITLE), 'текст про рубильник').toBe(true);
    const on = await mount(base({
      guardians: [{ userId: 'u-1', sharedProjectsFlag: true }],
      sharedProjectsInstanceEnabled: true,
    }));
    expect(breakerNotice(on.host), 'при включённом рубильнике плашки нет').toBeNull();
    expect(hasText(rootEl(on.host), BREAKER_TITLE), 'при включённом рубильнике строки нет').toBe(false);
    const unknown = await mount(base({ guardians: [{ userId: 'u-1', sharedProjectsFlag: true }] }));
    expect(breakerNotice(unknown.host), 'поля ответа нет — не выдумываем').toBeNull();
    expect(hasText(rootEl(unknown.host), BREAKER_TITLE), 'поля ответа нет — не выдумываем').toBe(false);
  });

  it('модель помощника правил: полный список реестра; смена поставщика — только маршрут, согласие не трогает', async () => {
    const m = await mount(base({
      routes: ROUTES,
      route: { providerKey: 'anthropic-api-kids', model: 'sonnet' },
      acknowledgedAt: ACKED_AT, acknowledgedBy: 'u-1', userNames: { 'u-1': 'Андрей' },
    }));
    const prov = selectByLabel(m.host, 'Поставщик');
    expect(prov, 'поле «Поставщик»').not.toBeNull();
    // не только пул: облачные и локальные открыты чату правил (решение 25)
    expect(options(prov!)).toEqual(['anthropic-api-kids', 'glm', 'strata']);

    await changeSelect(prov!, 'glm');
    expect(routeChanges).toEqual([{ providerKey: 'glm', model: 'glm-5' }]);
    expect(ackActions, 'согласие сменой маршрута не сбрасывается (решение 26)').toEqual([]);
    await m.apply({ route: { providerKey: 'glm', model: 'glm-5' } });
    expect(ackBox(m.host)!.getAttribute('aria-checked'), 'согласие осталось').toBe('true');
  });

  it('галка пикера — то же согласие профиля: её снятие → false', async () => {
    const m = await mount(base({
      routes: ROUTES,
      route: { providerKey: 'glm', model: 'glm-5' },
      acknowledgedAt: ACKED_AT, acknowledgedBy: 'u-1', userNames: { 'u-1': 'Андрей' },
    }));
    const pa = pickerAck(m.host);
    expect(pa, 'галка пикера').not.toBeNull();
    expect(pa!.getAttribute('aria-checked'), 'проекция данного согласия — отмечена').toBe('true');
    await act(async () => { pa!.click(); });
    expect(ackActions).toEqual([false]);
  });

  it('список моделей грузится: пикера нет — он не врёт «нет записей» о списке, которого ещё не знаем', async () => {
    const { host } = await mount(base({ routes: null }));
    expect(hasText(rootEl(host), 'Загружаю список моделей')).toBe(true);
    expect(rootEl(host).querySelector('[data-testid="child-route-picker"]')).toBeNull();
  });

  it('стоп-сигнал: текст как в макете — кому уходит уведомление и чего в нём нет', async () => {
    const { host } = await mount(base());
    expect(hasText(rootEl(host), 'Кнопка «Мне неприятно» и сигнал от репетитора присылают уведомление тебе и опекунам')).toBe(true);
    expect(hasText(rootEl(host), 'Текста переписки в уведомлении нет')).toBe(true);
  });

  it('пустой список опекунов — empty-state, а не пустая таблица', async () => {
    const { host } = await mount(base({ guardians: [] }));
    expect(hasText(rootEl(host), 'Опекунов нет')).toBe(true);
    expect(rootEl(host).querySelector('[data-testid="guardians-list"]')).toBeNull();
  });

  it('на мобиле (360) и с выключенным режимом сохранения: галка ≥ 40px и гасится', async () => {
    mobile = true;
    try {
      const m = await mount(base());
      const box = ackBox(m.host)!;
      expect(Number.parseFloat(box.style.width), 'тач-цель по ширине').toBeGreaterThanOrEqual(40);
      expect(Number.parseFloat(box.style.height), 'тач-цель по высоте').toBeGreaterThanOrEqual(40);
      const d = await mount(base({ disabled: true }));
      const db = ackBox(d.host)!;
      expect((db as unknown as { disabled?: boolean }).disabled === true || db.getAttribute('disabled') !== null, 'галка на сохранении гасится').toBeTruthy();
    } finally {
      mobile = false;
    }
  });
});
