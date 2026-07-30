import { describe, expect, it } from 'vitest';
import { formatSubscriptionMeta, splitFallbackOptions } from '../providerLimit';
import type { ProviderFallbackOption } from '../../types';

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
