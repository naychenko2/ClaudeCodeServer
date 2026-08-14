import { describe, expect, it } from 'vitest';
import {
  formatSubscriptionMeta, splitFallbackOptions, providerSwitchReasonLabel,
  providerAvailabilityFromBalance, splitByAvailability, nearestReturn,
  providersPlural, fmtReturnTime, invalidateExhaustedVerdict,
} from '../providerLimit';
import type { ProviderBalanceInfo, ProviderFallbackOption } from '../../types';

const sub = (key: string, extra?: Partial<ProviderFallbackOption>): ProviderFallbackOption => ({
  key,
  displayName: key,
  model: 'claude-sonnet-5',
  kind: 'subscription',
  ...extra,
});
const prov = (key: string, extra?: Partial<ProviderFallbackOption>): ProviderFallbackOption => ({
  key,
  displayName: key,
  model: `${key}-1`,
  ...extra,
});

describe('splitFallbackOptions', () => {
  it('только сторонние провайдеры (старый контракт без kind) — блок подписок пуст', () => {
    const { subscriptions, providers } = splitFallbackOptions([prov('glm'), prov('deepseek')]);
    expect(subscriptions).toEqual([]);
    expect(providers.map(p => p.key)).toEqual(['glm', 'deepseek']);
  });

  it('только аккаунты пула — все попадают в подписки', () => {
    const { subscriptions, providers } = splitFallbackOptions([sub('acc-b'), sub('acc-c')]);
    expect(subscriptions.map(s => s.key)).toEqual(['acc-b', 'acc-c']);
    expect(providers).toEqual([]);
  });

  it('смешанный список: подписки и провайдеры разводятся, порядок внутри секций сохраняется', () => {
    const { subscriptions, providers } = splitFallbackOptions([
      sub('acc-b'), prov('glm'), sub('acc-c'), prov('deepseek'),
    ]);
    expect(subscriptions.map(s => s.key)).toEqual(['acc-b', 'acc-c']);
    expect(providers.map(p => p.key)).toEqual(['glm', 'deepseek']);
  });

  it('пустой список — обе секции пустые', () => {
    expect(splitFallbackOptions([])).toEqual({ subscriptions: [], providers: [] });
  });
});

describe('formatSubscriptionMeta', () => {
  it('тариф + утилизация: «Max 5× · 41%»', () => {
    expect(formatSubscriptionMeta(sub('a', { tierLabel: 'Max 5×', utilization: 0.41 }))).toBe('Max 5× · 41%');
  });

  it('утилизация округляется до целых процентов', () => {
    expect(formatSubscriptionMeta(sub('a', { utilization: 0.425 }))).toBe('43%');
    expect(formatSubscriptionMeta(sub('a', { utilization: 0 }))).toBe('0%');
  });

  it('только тариф, если утилизации нет', () => {
    expect(formatSubscriptionMeta(sub('a', { tierLabel: 'Pro' }))).toBe('Pro');
  });

  it('пусто, если не задано ничего', () => {
    expect(formatSubscriptionMeta(sub('a'))).toBe('');
  });
});

describe('providerSwitchReasonLabel', () => {
  it('rate_limit и usage_limit сводятся к «Исчерпан лимит»', () => {
    expect(providerSwitchReasonLabel('rate_limit', 'сырой label')).toBe('Исчерпан лимит');
    expect(providerSwitchReasonLabel('usage_limit', 'сырой label')).toBe('Исчерпан лимит');
  });

  it('provider_error → «Провайдер выключен», unreachable → «Эндпоинт недоступен»', () => {
    expect(providerSwitchReasonLabel('provider_error', 'сырой label')).toBe('Провайдер выключен');
    expect(providerSwitchReasonLabel('unreachable', 'сырой label')).toBe('Эндпоинт недоступен');
  });

  it('нет reason или значение не опознано — фолбэк на сырой label маркера', () => {
    expect(providerSwitchReasonLabel(undefined, 'Автофолбэк: смена провайдера → «DeepSeek»'))
      .toBe('Автофолбэк: смена провайдера → «DeepSeek»');
    expect(providerSwitchReasonLabel('none', 'сырой label')).toBe('сырой label');
  });

  it('reason есть, но label тоже нет — undefined (подсказка не рисуется)', () => {
    expect(providerSwitchReasonLabel(undefined, undefined)).toBeUndefined();
  });
});

const NOW = Date.parse('2026-08-04T12:00:00Z');
const FUTURE = '2026-08-04T14:30:00Z';   // +2.5ч от NOW
const FUTURE_LATER = '2026-08-04T18:00:00Z';
const PAST = '2026-08-04T09:00:00Z';

const balance = (b: Partial<ProviderBalanceInfo>): ProviderBalanceInfo => ({
  available: true,
  currency: '%',
  totalBalance: '50',
  ...b,
});

describe('providerAvailabilityFromBalance', () => {
  it('нет баланса (не настроен / сбой запроса) — провайдер не скрывается', () => {
    expect(providerAvailabilityFromBalance(null, NOW)).toEqual({ available: true, resetsAt: null });
    expect(providerAvailabilityFromBalance(undefined, NOW)).toEqual({ available: true, resetsAt: null });
  });

  it('available=false от провайдера — недоступен, возврат из resetsAt', () => {
    expect(providerAvailabilityFromBalance(balance({ available: false, resetsAt: FUTURE }), NOW))
      .toEqual({ available: false, resetsAt: FUTURE });
  });

  it('available=false с resetsAt в прошлом — недоступен, время возврата неизвестно', () => {
    expect(providerAvailabilityFromBalance(balance({ available: false, resetsAt: PAST }), NOW))
      .toEqual({ available: false, resetsAt: null });
  });

  it('квотное окно с нулевым остатком и будущим сбросом — пауза до сброса', () => {
    const b = balance({
      totalBalance: '0',
      windows: [{ label: '5 часов', value: '0', resetsAt: FUTURE, unit: 'percent' }],
    });
    expect(providerAvailabilityFromBalance(b, NOW)).toEqual({ available: false, resetsAt: FUTURE });
  });

  it('нулевой остаток, но сброс уже прошёл — данные протухли, провайдер доступен', () => {
    const b = balance({
      totalBalance: '0',
      windows: [{ label: '5 часов', value: '0', resetsAt: PAST, unit: 'percent' }],
    });
    expect(providerAvailabilityFromBalance(b, NOW)).toEqual({ available: true, resetsAt: null });
  });

  it('нулевой остаток без времени сброса — пауза неизвестной длительности', () => {
    const b = balance({
      totalBalance: '0',
      windows: [{ label: '5 часов', value: '0', resetsAt: null, unit: 'percent' }],
    });
    expect(providerAvailabilityFromBalance(b, NOW)).toEqual({ available: false, resetsAt: null });
  });

  it('несколько исчерпанных окон — возврат по ближайшему сбросу', () => {
    const b = balance({
      totalBalance: '0',
      windows: [
        { label: 'Неделя', value: '0', resetsAt: FUTURE_LATER, unit: 'percent' },
        { label: '5 часов', value: '0', resetsAt: FUTURE, unit: 'percent' },
      ],
    });
    expect(providerAvailabilityFromBalance(b, NOW)).toEqual({ available: false, resetsAt: FUTURE });
  });

  it('остаток есть — доступен (процент и count-формат)', () => {
    const pct = balance({ windows: [{ label: '5 часов', value: '41.5', resetsAt: FUTURE, unit: 'percent' }] });
    expect(providerAvailabilityFromBalance(pct, NOW).available).toBe(true);
    const cnt = balance({ windows: [{ label: 'Интервал', value: '120/300', resetsAt: FUTURE, unit: 'count' }] });
    expect(providerAvailabilityFromBalance(cnt, NOW).available).toBe(true);
  });

  it('count-окно: нуль занятых — доступен (числитель = занято), выбор лимита — исчерпан', () => {
    // 0/300 — ни одной активной сессии: это не исчерпание. Нуль числителя означал бы
    // беду только у FreeLLM (0 живых платформ), но то закрыто флагом available выше окон
    const zero = balance({ windows: [{ label: 'Интервал', value: '0/300', resetsAt: FUTURE, unit: 'count' }] });
    expect(providerAvailabilityFromBalance(zero, NOW).available).toBe(true);
    const full = balance({ windows: [{ label: 'Интервал', value: '300/300', resetsAt: FUTURE, unit: 'count' }] });
    expect(providerAvailabilityFromBalance(full, NOW)).toEqual({ available: false, resetsAt: FUTURE });
  });

  it('FreeLLM alive-окно: живые платформы — не исчерпание, здоровый пул N=M доступен', () => {
    // Регрессия inverse-семантики: count-трактовка считала N/M «выбором лимита» и
    // прятала здоровый FreeLLM из карточки «Продолжить на …»
    const healthy = balance({
      available: true, currency: 'count', totalBalance: '6/6',
      windows: [{ label: 'Провайдеры', value: '6/6', resetsAt: null, unit: 'alive' }],
    });
    expect(providerAvailabilityFromBalance(healthy, NOW)).toEqual({ available: true, resetsAt: null });
    const partial = balance({
      available: true, currency: 'count', totalBalance: '4/6',
      windows: [{ label: 'Провайдеры', value: '4/6', resetsAt: null, unit: 'alive' }],
    });
    expect(providerAvailabilityFromBalance(partial, NOW).available).toBe(true);
  });

  it('FreeLLM alive-окно: пул без живых платформ закрыт флагом available, а не окном', () => {
    const dead = balance({
      available: false, currency: 'count', totalBalance: '0/6',
      windows: [{ label: 'Провайдеры', value: '0/6', resetsAt: null, unit: 'alive' }],
    });
    expect(providerAvailabilityFromBalance(dead, NOW)).toEqual({ available: false, resetsAt: null });
  });

  it('денежный провайдер без окон: нулевой баланс — недоступен, положительный — доступен', () => {
    expect(providerAvailabilityFromBalance(balance({ totalBalance: '0', windows: null }), NOW))
      .toEqual({ available: false, resetsAt: null });
    expect(providerAvailabilityFromBalance(balance({ totalBalance: '4.2' }), NOW).available).toBe(true);
    // нечисловой баланс проверкой не трогаем
    expect(providerAvailabilityFromBalance(balance({ totalBalance: '' }), NOW).available).toBe(true);
  });
});

describe('splitByAvailability', () => {
  it('разводит на доступные и скрытые, без вердикта — доступен', () => {
    const { available, hidden } = splitByAvailability(
      [prov('glm'), prov('kimi'), prov('deepseek')],
      { glm: { available: false, resetsAt: FUTURE }, kimi: { available: true, resetsAt: null } },
    );
    expect(available.map(p => p.key)).toEqual(['kimi', 'deepseek']);
    expect(hidden).toEqual([{ option: expect.objectContaining({ key: 'glm' }), resetsAt: FUTURE }]);
  });
});

describe('invalidateExhaustedVerdict', () => {
  it('перекрывает вердикт провайдера-источника плашки на недоступный, не трогая остальные', () => {
    const verdicts = { glm: { available: true, resetsAt: null }, kimi: { available: true, resetsAt: null } };
    const result = invalidateExhaustedVerdict(verdicts, 'glm');
    expect(result).toEqual({ glm: { available: false, resetsAt: null }, kimi: { available: true, resetsAt: null } });
    // исходная карта (устаревший «доступен» из кэша) не мутирована — сброс кэша через замену, не мутацию
    expect(verdicts.glm).toEqual({ available: true, resetsAt: null });
  });

  it('провайдер-источник плашки отсутствует в кнопках при «доступном» кэше и попадает в счётчик недоступных', () => {
    const providers = [prov('glm'), prov('kimi')];
    const cachedAvailable = { glm: { available: true, resetsAt: null }, kimi: { available: true, resetsAt: null } };
    // Без инвалидации — кэш ещё не протух, «источник» показывается доступным (баг, который чиним)
    expect(splitByAvailability(providers, cachedAvailable).available.map(p => p.key)).toEqual(['glm', 'kimi']);

    // Ответ об исчерпании лимита для glm инвалидирует именно его вердикт
    const verdicts = invalidateExhaustedVerdict(cachedAvailable, 'glm');
    const { available, hidden } = splitByAvailability(providers, verdicts);
    expect(available.map(p => p.key)).toEqual(['kimi']);
    expect(hidden.map(h => h.option.key)).toEqual(['glm']);
  });
});

describe('nearestReturn', () => {
  it('ближайший по времени, без времени — не в счёт', () => {
    const h = [
      { option: prov('kimi'), resetsAt: FUTURE_LATER },
      { option: prov('glm'), resetsAt: FUTURE },
      { option: prov('minimax'), resetsAt: null },
    ];
    expect(nearestReturn(h)?.option.key).toBe('glm');
  });

  it('пусто, когда ни у кого нет времени возврата', () => {
    expect(nearestReturn([{ option: prov('glm'), resetsAt: null }])).toBeNull();
    expect(nearestReturn([])).toBeNull();
  });
});

describe('providersPlural', () => {
  it('склоняется по-русски', () => {
    expect(providersPlural(1)).toBe('провайдер');
    expect(providersPlural(2)).toBe('провайдера');
    expect(providersPlural(5)).toBe('провайдеров');
    expect(providersPlural(11)).toBe('провайдеров');
    expect(providersPlural(21)).toBe('провайдер');
    expect(providersPlural(22)).toBe('провайдера');
    expect(providersPlural(104)).toBe('провайдера');
    expect(providersPlural(111)).toBe('провайдеров');
  });
});

describe('fmtReturnTime', () => {
  it('тот же день — «в ЧЧ:ММ»', () => {
    expect(fmtReturnTime(FUTURE, NOW)).toMatch(/^в \d{2}:\d{2}$/);
  });

  it('другой день — дата и время', () => {
    expect(fmtReturnTime('2026-08-12T14:30:00Z', NOW)).toMatch(/\d{1,2} .* в \d{2}:\d{2}$/);
  });

  it('некорректная дата — пустая строка', () => {
    expect(fmtReturnTime('не дата', NOW)).toBe('');
  });
});
