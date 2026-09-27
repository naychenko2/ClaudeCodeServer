import { describe, it, expect, beforeEach } from 'vitest';
import { defaultHubTabs, subsystemTabValue } from '../../components/hubTabsModel';
import type { SubsystemManifest } from '../subsystems/registryCore';
import { isSubsystemEnabled, setAllSubsystems, __resetSubsystems } from '../subsystems';

// Минимальные манифесты: defaultHubTabs смотрит только на `tab`, `key`, `order`.
// Полноценный `tab.component` в модели не нужен — важно лишь его НАЛИЧИЕ.
const withTab = (key: string, order: number): SubsystemManifest => ({
  key, title: key, order, tab: {} as unknown as SubsystemManifest['tab'],
});
const withoutTab = (key: string, order: number): SubsystemManifest => ({ key, title: key, order });

describe('defaultHubTabs: набор вкладок таббара из реестра', () => {
  beforeEach(() => { __resetSubsystems(); });

  it('пустой реестр — только фиксированные вкладки, без падения', () => {
    expect(defaultHubTabs([])).toEqual(['chats', 'projects', 'calendar', 'personas']);
  });

  it('подсистемная вкладка встаёт по order — 35 между calendar (30) и personas (50)', () => {
    const tabs = defaultHubTabs([withTab('notes', 35)]);
    expect(tabs).toEqual(['chats', 'projects', 'calendar', 'subsystem:notes', 'personas']);
    expect(tabs.indexOf(subsystemTabValue('notes'))).toBe(3);
  });

  it('подсистема без `tab` пилюли не даёт', () => {
    expect(defaultHubTabs([withoutTab('hidden', 35)])).toEqual(['chats', 'projects', 'calendar', 'personas']);
  });

  it('подсистема с noPill: true не попадает в набор пилюль/таббара', () => {
    const noPill: SubsystemManifest = {
      key: 'spend', title: 'Аналитика', order: 90, noPill: true,
      tab: {} as unknown as SubsystemManifest['tab'],
    };
    const tabs = defaultHubTabs([noPill]);
    expect(tabs).not.toContain(subsystemTabValue('spend'));
    expect(tabs).toEqual(['chats', 'projects', 'calendar', 'personas']);
  });

  it('подсистема БЕЗ noPill (аналог Notes) попадает в таббар', () => {
    const normal: SubsystemManifest = {
      key: 'notes', title: 'Заметки', order: 35,
      tab: {} as unknown as SubsystemManifest['tab'],
    };
    const tabs = defaultHubTabs([normal]);
    expect(tabs).toContain(subsystemTabValue('notes'));
    expect(tabs).toEqual(['chats', 'projects', 'calendar', 'subsystem:notes', 'personas']);
  });

  it('выключенная подсистема не проходит гейт isSubsystemEnabled', () => {
    const subs = [withTab('notes', 35)];
    // выключена (стор пуст — fail-closed): активных подсистем нет
    const off = defaultHubTabs(subs.filter(m => isSubsystemEnabled(m.key)));
    expect(off).not.toContain(subsystemTabValue('notes'));
    expect(off).toEqual(['chats', 'projects', 'calendar', 'personas']);
    // включена — вкладка появляется
    setAllSubsystems(['notes']);
    const on = defaultHubTabs(subs.filter(m => isSubsystemEnabled(m.key)));
    expect(on).toContain(subsystemTabValue('notes'));
  });
});
