import { describe, it, expect, beforeEach } from 'vitest';
import {
  registerSubsystem, getSlotContributions, getSlotItem, getSlotAction,
  subscribeRegistry, getRegistryVersion, getSubsystem,
} from '../subsystems/registryCore';
import type { SubsystemManifest } from '../subsystems/registryCore';
import { setAllSubsystems, __resetSubsystems } from '../subsystems';

// Реестр не умеет снимать регистрацию (в проде она статична), поэтому тестовый
// манифест регистрируем один раз на модуль, а между кейсами сбрасываем лишь
// состояние тумблеров. Ключ синтетический — чтобы тест не тянул фичу Notes и её
// браузерные зависимости (проверяем сам механизм реестра).
const manifest: SubsystemManifest = {
  key: 'test-sub',
  title: 'Тестовая подсистема',
  order: 1,
  slots: {
    'demo-render': [{ name: 'x', order: 1, render: () => 'ok' }],
    'demo-action': [{ name: 'a', order: 1, action: { ping: () => 'pong' } }],
  },
};
registerSubsystem(manifest);

describe('реестр подсистем: read-time гейт по включённости', () => {
  beforeEach(() => { __resetSubsystems(); });

  it('подсистема зарегистрирована, но выключена — вкладов нет', () => {
    expect(getSubsystem('test-sub')).toBe(manifest);
    expect(getSlotContributions('demo-render')).toHaveLength(0);
    expect(getSlotItem('demo-action', 'a')).toBeUndefined();
    expect(getSlotAction('demo-action', 'a')).toBeUndefined();
  });

  it('после setAllSubsystems([...]) вклады появляются (render и action)', () => {
    setAllSubsystems(['test-sub']);
    expect(getSlotContributions('demo-render')).toHaveLength(1);
    const api = getSlotAction<{ ping: () => string }>('demo-action', 'a');
    expect(api?.ping()).toBe('pong');
  });

  it('смена тумблера поднимает версию и оповещает подписчика (база useSlot)', () => {
    let calls = 0;
    const unsub = subscribeRegistry(() => { calls += 1; });
    const before = getRegistryVersion();
    setAllSubsystems(['test-sub']);
    expect(calls).toBeGreaterThan(0);
    expect(getRegistryVersion()).toBeGreaterThan(before);
    // Выключение — та же реакция: вклады снова пусты, подписчик оповещён.
    setAllSubsystems([]);
    expect(getSlotContributions('demo-render')).toHaveLength(0);
    unsub();
  });
});
