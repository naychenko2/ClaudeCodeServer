import { describe, expect, it } from 'vitest';
import { handsBadgeView, handsSectionView, handsStatusFeedLine, HandsChatState, HandsEndReason } from '../localHands';
import { featureReason, isFeatureAvailable } from '../projectCapabilities';
import { applyServerMessage, initialChatState } from '../chatReducer';
import { ProjectFeature, type Project, type ServerMessage } from '../../types';

const base = { handsEnabled: false, refusal: null, deviceOffline: false, noProviders: false };

// Решения владельца 2026-09-27: ни сеанса со сроком, ни белого списка, ни запрета терминала —
// ни одна строка веб-UI рук не должна их обещать
const FORBIDDEN = [/минут/i, /срок/i, /истёк/i, /разрешите/i, /в трее на/i, /добавьте программу/i, /нет терминала/i];

function assertNoLegacyWording(text: string) {
  for (const re of FORBIDDEN) expect(text, `«${text}» содержит ${re}`).not.toMatch(re);
}

describe('handsSectionView', () => {
  it('выключено — тумблер доступен, сводка «Выключены»', () => {
    const v = handsSectionView(base);
    expect(v).toMatchObject({ summary: 'Выключены', tone: 'neutral', canToggle: true, refusal: null });
  });

  it('включено, провайдеры есть — «Включены»', () => {
    const v = handsSectionView({ ...base, handsEnabled: true });
    expect(v).toMatchObject({ summary: 'Включены', tone: 'ok', noProvidersWarning: false });
  });

  it('включено без провайдеров — предупреждение', () => {
    const v = handsSectionView({ ...base, handsEnabled: true, noProviders: true });
    expect(v).toMatchObject({ summary: 'Нет провайдеров', tone: 'warning', noProvidersWarning: true });
  });

  it('список провайдеров ещё не загружен — не пугаем', () => {
    expect(handsSectionView({ ...base, handsEnabled: true, noProviders: null }).noProvidersWarning).toBe(false);
  });

  it('устройство не в сети — тумблер работает, строка про возвращение', () => {
    const v = handsSectionView({ ...base, deviceOffline: true });
    expect(v).toMatchObject({ canToggle: true, offlineNote: true });
  });

  it('отказ матрицы при выключенных руках — тумблер недоступен, текст сервера как есть', () => {
    const v = handsSectionView({ ...base, refusal: 'На устройстве не установлены руки' });
    expect(v).toMatchObject({ summary: 'Недоступно', canToggle: false, refusal: 'На устройстве не установлены руки' });
  });

  it('отказ матрицы при включённых руках — выключить всё равно можно', () => {
    const v = handsSectionView({ ...base, handsEnabled: true, refusal: 'Устройство проекта не найдено или отозвано' });
    expect(v).toMatchObject({ canToggle: true, tone: 'warning' });
  });
});

describe('handsBadgeView', () => {
  it('до первого события — нейтрально, без «Стоп», не «активен»', () => {
    const v = handsBadgeView(null, 'home-pc');
    expect(v).toMatchObject({ tone: 'neutral', canStop: false, text: 'Руки включены' });
  });

  it('руки в ходе — «Стоп» и имя устройства', () => {
    const v = handsBadgeView({ state: HandsChatState.Active, deviceName: 'home-pc' }, null);
    expect(v).toMatchObject({ tone: 'success', canStop: true, text: 'ИИ за компьютером home-pc', short: 'Руки' });
    expect(v.title).toContain('только окна, которые открыл сам');
  });

  it('провайдер не доверен — подсказка, где настроить', () => {
    const v = handsBadgeView({ state: HandsChatState.ProviderNotAllowed }, 'home-pc');
    expect(v).toMatchObject({ short: 'Нет рук', canStop: false });
    expect(v.title).toContain('Руки на устройствах');
  });

  it('no-session — руки недоступны, например заняты другим ходом', () => {
    const v = handsBadgeView({ state: HandsChatState.NoSession }, 'home-pc');
    expect(v.title).toContain('заняты другим ходом');
  });

  it('незнакомое состояние — нейтральный вид', () => {
    expect(handsBadgeView({ state: 'что-то-новое' }, null).tone).toBe('neutral');
  });

  it.each([
    HandsChatState.Active, HandsChatState.Allowed, HandsChatState.NoSession,
    HandsChatState.Stopped, HandsChatState.ProviderNotAllowed, 'initial',
  ])('%s — без сроков, «разрешите в трее» и белого списка', state => {
    for (const reason of Object.values(HandsEndReason)) {
      const v = handsBadgeView(state === 'initial' ? null : { state, reason, deviceName: 'pc' }, 'pc');
      assertNoLegacyWording(`${v.text} ${v.short} ${v.title}`);
    }
  });
});

describe('handsStatusFeedLine', () => {
  it('«Стоп» из трея — строка про закрытые окна', () => {
    expect(handsStatusFeedLine({ state: HandsChatState.Stopped, reason: HandsEndReason.StoppedFromTray, deviceName: 'home-pc' }))
      .toBe('Ход прерван на устройстве «home-pc»: руки выключены кнопкой «Стоп». Окна, открытые ходом, закрыты.');
  });

  it('активность и провайдер ленту не трогают', () => {
    expect(handsStatusFeedLine({ state: HandsChatState.Active })).toBeNull();
    expect(handsStatusFeedLine({ state: HandsChatState.ProviderNotAllowed })).toBeNull();
  });

  it.each(Object.values(HandsEndReason))('остановка %s — без сроков', reason => {
    const line = handsStatusFeedLine({ state: HandsChatState.Stopped, reason, deviceName: 'pc' });
    expect(line).not.toBeNull();
    assertNoLegacyWording(line!);
  });
});

describe('руки в ленте чата', () => {
  const feed = (msg: Partial<ServerMessage> & { type: string }) =>
    applyServerMessage(initialChatState(), { sessionId: 's1', ...msg } as ServerMessage);

  it('hands_notice — нейтральная строка с текстом сервера', () => {
    const s = feed({ type: 'hands_notice', text: 'В чате с руками режим «Без ограничений» не действует' });
    expect(s.items.at(-1)).toEqual({ kind: 'hands_notice', text: 'В чате с руками режим «Без ограничений» не действует', tone: 'neutral' });
  });

  it('hands_status stopped — янтарная строка', () => {
    const s = feed({ type: 'hands_status', state: 'stopped', reason: 'tray-stop', deviceName: 'pc' });
    expect(s.items.at(-1)).toMatchObject({ kind: 'hands_notice', tone: 'warning' });
  });

  it('hands_status active — лента не меняется', () => {
    expect(feed({ type: 'hands_status', state: 'active' }).items).toHaveLength(0);
  });
});

describe('возможность Hands в матрице', () => {
  const project = (extra: Partial<Project>) => ({ id: 'p', name: 'p', rootPath: '/', createdAt: '', updatedAt: '', ...extra }) as Project;

  it('handsRefusal=null — доступно', () => {
    expect(isFeatureAvailable(project({ handsRefusal: null }), ProjectFeature.Hands)).toBe(true);
    expect(featureReason(project({ handsRefusal: null }), ProjectFeature.Hands)).toBeNull();
  });

  it('отказ — текст сервера', () => {
    const p = project({ handsRefusal: 'Руки есть только у локального проекта' });
    expect(isFeatureAvailable(p, ProjectFeature.Hands)).toBe(false);
    expect(featureReason(p, ProjectFeature.Hands)).toBe('Руки есть только у локального проекта');
  });

  it('старый бэк без поля — недоступно', () => {
    expect(isFeatureAvailable(project({}), ProjectFeature.Hands)).toBe(false);
  });
});
