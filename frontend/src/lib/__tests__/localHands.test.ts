import { describe, expect, it } from 'vitest';
import {
  HANDS_BADGE_LOADING, handsBadgeStatus, handsBadgeView, handsEventReceived, handsInitialFailed, handsInitialLoaded,
  handsProviderLabel, handsProviderVision, handsSectionView, handsStatusFeedLine, handsStripSummary, handsStripView,
  HandsChatState, HandsEndReason,
} from '../localHands';
import { featureReason, isFeatureAvailable } from '../projectCapabilities';
import { applyServerMessage, initialChatState } from '../chatReducer';
import { ProjectFeature, type Project, type ServerMessage } from '../../types';

const base = { handsEnabled: false, refusal: null, deviceOffline: false };

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

  it('включено — «Включены» без условий про провайдеров (решение владельца 2026-09-27)', () => {
    const v = handsSectionView({ ...base, handsEnabled: true });
    expect(v).toMatchObject({ summary: 'Включены', tone: 'ok' });
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

describe('состояние бейджа: GET, затем события', () => {
  const view = (state: string | null, reason: string | null = null) => ({ state, reason, deviceName: 'home-pc' });

  it('пока GET грузится — нейтральный «Руки включены», не «активен»', () => {
    const v = handsBadgeView(handsBadgeStatus(HANDS_BADGE_LOADING), 'home-pc');
    expect(v).toMatchObject({ tone: 'neutral', canStop: false, text: 'Руки включены' });
  });

  it('запрос упал — тот же нейтральный вид', () => {
    const s = handsInitialFailed(HANDS_BADGE_LOADING);
    expect(s.kind).toBe('failed');
    expect(handsBadgeView(handsBadgeStatus(s), 'home-pc').text).toBe('Руки включены');
  });

  it('первая отрисовка берёт состояние из ответа GET', () => {
    const s = handsInitialLoaded(HANDS_BADGE_LOADING, view(HandsChatState.Unavailable));
    expect(handsBadgeView(handsBadgeStatus(s), null).text).toBe('Руки недоступны на устройстве');
  });

  it('GET вернул state=null — у чата рук нет, бейдж не рисуется', () => {
    expect(handsInitialLoaded(HANDS_BADGE_LOADING, view(null)).kind).toBe('none');
  });

  it('событие, пришедшее раньше ответа GET, свежее — ответ его не перетирает', () => {
    const afterEvent = handsEventReceived({ state: HandsChatState.Active, deviceName: 'home-pc' });
    const s = handsInitialLoaded(afterEvent, view(HandsChatState.Allowed));
    expect(handsBadgeStatus(s)?.state).toBe(HandsChatState.Active);
    expect(handsInitialFailed(afterEvent)).toBe(afterEvent);
  });

  it('событие после GET меняет вид', () => {
    const s = handsEventReceived({ state: HandsChatState.Stopped, reason: HandsEndReason.StoppedFromTray });
    expect(handsBadgeView(handsBadgeStatus(s), null)).toMatchObject({ tone: 'warning', short: 'Стоп' });
  });
});

describe('handsBadgeView', () => {
  it('руки в ходе — «Стоп» и имя устройства', () => {
    const v = handsBadgeView({ state: HandsChatState.Active, deviceName: 'home-pc' }, null);
    expect(v).toMatchObject({ tone: 'success', canStop: true, text: 'ИИ за компьютером home-pc', short: 'Руки' });
    expect(v.title).toContain('любые окна на этом компьютере, в том числе снимать экран');
  });

  it('unavailable — устройство не на связи или руки не установлены, без «Стоп»', () => {
    const v = handsBadgeView({ state: HandsChatState.Unavailable }, 'home-pc');
    expect(v).toMatchObject({ tone: 'warning', short: 'Нет рук', canStop: false, text: 'Руки недоступны на устройстве' });
    expect(v.title).toContain('не на связи');
    // «заняты» — отказ хода агентом, а не состояние рук: его текст приходит ошибкой хода
    expect(v.title).not.toContain('заняты');
  });

  it('unavailable с причиной busy — руки заняты другим ходом, без «Стоп»', () => {
    const v = handsBadgeView({ state: HandsChatState.Unavailable, reason: HandsEndReason.Busy }, 'home-pc');
    expect(v).toMatchObject({ tone: 'warning', canStop: false, text: 'Руки заняты другим ходом на этом устройстве' });
    expect(v.title).not.toContain('не на связи');
  });

  it('allowed — руки готовы, без «Стоп»', () => {
    expect(handsBadgeView({ state: HandsChatState.Allowed }, 'pc')).toMatchObject({ text: 'Руки готовы', canStop: false });
  });

  it('незнакомое состояние — нейтральный вид', () => {
    expect(handsBadgeView({ state: 'что-то-новое' }, null).tone).toBe('neutral');
  });

  it.each([
    HandsChatState.Active, HandsChatState.Allowed, HandsChatState.Unavailable,
    HandsChatState.Stopped, 'initial',
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

describe('handsProviderLabel', () => {
  const providers = [
    { key: 'claude', caps: { displayName: 'Claude', supportsImages: true } },
    { key: 'glm', caps: { displayName: 'GLM', supportsImages: false } },
    { key: 'deepseek', caps: { displayName: 'DeepSeek', supportsImages: true } },
  ];

  it('провайдер со зрением — видит снимки окон', () => {
    expect(handsProviderLabel('deepseek', providers)).toBe('DeepSeek · видит снимки окон');
  });

  it('провайдер без зрения — видит только текст окон', () => {
    expect(handsProviderLabel('glm', providers)).toBe('GLM · видит только текст окон');
  });

  it('подписка пула Claude (свой ключ) и пустой провайдер — это Claude', () => {
    expect(handsProviderLabel('acc-a', providers)).toBe('Claude · видит снимки окон');
    expect(handsProviderLabel(null, providers)).toBe('Claude · видит снимки окон');
  });
});

describe('полоса «Руки» — одна строка', () => {
  const claude = { name: 'Claude', vision: true };
  const glm = { name: 'GLM', vision: false };
  const st = (state: string, reason?: string) => ({ state, reason });
  // Самая длинная короткая форма чипа должна влезать в телефон 390 рядом с «Стоп» и ⌃
  const SHORT_MAX = 12;

  // Пять состояний постановки плюс нейтральный вид до ответа сервера
  const cases: Array<[string, ReturnType<typeof st> | null, string, string, string, boolean]> = [
    ['ИИ управляет компьютером', st('active'), 'ИИ управляет компьютером', 'ИИ управляет', 'success', true],
    ['руки готовы', st('allowed'), 'Руки готовы', 'Готовы', 'neutral', false],
    ['заняты другим ходом', st('unavailable', 'busy'), 'Заняты другим ходом', 'Заняты', 'warning', false],
    ['остановлено', st('stopped', 'tray-stop'), 'Остановлено', 'Остановлено', 'warning', false],
    ['недоступны на устройстве', st('unavailable'), 'Недоступны на устройстве', 'Недоступны', 'warning', false],
    ['до ответа сервера', null, 'Руки включены', 'Включены', 'neutral', false],
  ];

  for (const [name, status, chip, short, tone, canStop] of cases) {
    it(`${name}: чип «${chip}», коротко «${short}», тон ${tone}`, () => {
      const v = handsStripView(status, 'Ноутбук');
      expect(v.chip).toBe(chip);
      expect(v.short).toBe(short);
      expect(v.tone).toBe(tone);
      expect(v.canStop).toBe(canStop);
      expect(v.short.length).toBeLessThanOrEqual(SHORT_MAX);
      for (const t of [v.text, v.chip, v.short, v.reason ?? '', v.detail ?? '']) assertNoLegacyWording(t);
    });
  }

  it('полный статус (меню переключателя, заголовок карточки) — как в постановке', () => {
    expect(handsStripView(st('unavailable', 'busy'), null).text).toBe('Руки заняты другим ходом на этом устройстве');
    expect(handsStripView(st('unavailable'), null).text).toBe('Руки недоступны на устройстве');
    expect(handsStripView(st('active'), null).text).toBe('ИИ управляет компьютером');
  });

  it('причина остановки: коротко в сводке, полностью в карточке', () => {
    const v = handsStripView(st('stopped', 'tray-stop'), null);
    expect(v.reason).toBe('Нажат «Стоп» у компьютера');
    expect(v.detail).toMatch(/окна, открытые ходом, закрыты/);
    expect(handsStripView(st('stopped', 'disabled'), null).reason).toBe('Руки выключены на устройстве');
    expect(handsStripView(st('stopped', 'agent-stopping'), null).reason).toBe('Агент устройства остановился');
    expect(handsStripView(st('active'), null).reason).toBeNull();
  });

  it('сводка: устройство раньше зрения — многоточие съедает зрение, а не устройство', () => {
    expect(handsStripSummary(st('active'), 'Ноутбук', claude)).toBe('Ноутбук · Claude · видит снимки окон');
    expect(handsStripSummary(st('allowed'), 'Ноутбук', glm)).toBe('Ноутбук · GLM · только текст окон');
  });

  it('сводка по состояниям: занято, остановлено, недоступно, до ответа', () => {
    expect(handsStripSummary(st('unavailable', 'busy'), 'Ноутбук', claude)).toBe('Ноутбук · идёт другой ход');
    expect(handsStripSummary(st('stopped', 'tray-stop'), 'Ноутбук', claude)).toBe('Нажат «Стоп» у компьютера · Ноутбук');
    expect(handsStripSummary(st('unavailable'), 'Ноутбук', claude)).toBe('Ноутбук');
    expect(handsStripSummary(null, 'Ноутбук', claude)).toBe('Ноутбук');
  });

  it('свёрнутая строка: только устройство, у остановки — короткая причина', () => {
    expect(handsStripSummary(st('active'), 'Ноутбук', claude, true)).toBe('Ноутбук');
    expect(handsStripSummary(st('stopped', 'disabled'), 'Ноутбук', claude, true)).toBe('Руки выключены на устройстве · Ноутбук');
  });

  it('о провайдере в полосе только зрение — ни выбора, ни доверия', () => {
    for (const s of [st('active'), st('allowed')]) {
      const text = handsStripSummary(s, 'Ноутбук', glm);
      expect(text).not.toMatch(/довер|разреш|выбер|провайдер/i);
    }
    expect(handsProviderVision('glm', [{ key: 'glm', caps: { displayName: 'GLM', supportsImages: false } }]))
      .toEqual({ name: 'GLM', vision: false });
  });
});
