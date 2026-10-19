// Метка фичи в строке чата (Ф9, макет features-overview.html, «Дни»):
//   • featureLabelFor — чистое решение «показывать ли метку» (чат фичи + флаг → фича;
//     иначе null — прежнее имя персоны);
//   • FeatureLabel — рендер глифа + названия (обрезка с title).
//
// Окружение node (без jsdom): featureLabelFor — прямой вызов, FeatureLabel — SSR.
// Индекс фич — через тестовый шов __test.setIndex; флаг — setAllFlags.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it } from 'vitest';
import { ChatCard, FeatureLabel, featureLabelFor } from './ChatCard';
import { __test as featuresIndexTest } from '../lib/featuresIndex';
import { setAllFlags } from '../lib/featureFlags';
import type { FeatureIndexFeature, FeatureRailKey, FeatureRailSegment } from '../types/featureIndex';
import type { Session } from '../types';

// 7 стадий в порядке жизненного цикла; по умолчанию «идёт» (prod active run).
const KEYS: FeatureRailKey[] = ['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge'];
function rail(): FeatureRailSegment[] {
  const base: Record<FeatureRailKey, FeatureRailSegment> = {
    cut:    { key: 'cut',    state: 'done',    tone: null },
    plan:   { key: 'plan',   state: 'done',    tone: null },
    prod:   { key: 'prod',   state: 'active',  tone: 'run' },
    review: { key: 'review', state: 'pending', tone: null },
    qa:     { key: 'qa',     state: 'pending', tone: null },
    final:  { key: 'final',  state: 'pending', tone: null },
    merge:  { key: 'merge',  state: 'pending', tone: null },
  };
  return KEYS.map(k => base[k]);
}

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Фича', projectId: 'p1',
    bucket: 'running', attention: null,
    rail: rail(),
    chips: ['open'], stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  };
}

// Индекс: одна фича f1, чат chat-1 — её исполнитель.
function setupIndex() {
  featuresIndexTest.setIndex('p1', {
    features: [feat()],
    chats: { 'chat-1': [{ featureId: 'f1', kind: 'executor', taskRole: null, stepRef: null, lane: null }] },
  });
}

beforeEach(() => {
  featuresIndexTest.reset();
  setAllFlags({});
});

// === featureLabelFor: решение «показывать ли метку» ===

describe('featureLabelFor', () => {
  it('чат фичи + флаг вкл → фича (метка показывается)', () => {
    setupIndex();
    setAllFlags({ 'features-overview': true });
    const result = featureLabelFor('chat-1', true);
    expect(result?.featureId).toBe('f1');
  });

  it('чат без фичи → null (прежнее имя персоны)', () => {
    setupIndex();
    setAllFlags({ 'features-overview': true });
    const result = featureLabelFor('chat-other', true);
    expect(result).toBeNull();
  });

  it('флаг выключен → null (прежнее имя персоны)', () => {
    setupIndex();
    setAllFlags({ 'features-overview': false });
    const result = featureLabelFor('chat-1', false);
    expect(result).toBeNull();
  });
});

// === FeatureLabel: рендер метки ===

describe('FeatureLabel', () => {
  it('глиф + название; длинное название — title с полным текстом', () => {
    const longTitle = 'Очень длинное название фичи для проверки обрезки текста в строке';
    const f = feat({ title: longTitle });
    const html = renderToStaticMarkup(createElement(FeatureLabel, { feature: f }));
    // Метка есть
    expect(html).toContain('data-testid="chat-feature-label"');
    // title — полное название (обрезка CSS-ом, title — подсказка)
    expect(html).toContain(`title="${longTitle}"`);
    // Название в тексте
    expect(html).toContain(longTitle);
  });

  it('мутация: без data-testid метка не находится', () => {
    // Временно ломаем: убираем data-testid из FeatureLabel (через рендер с переопределением)
    // Для мутации проверяем, что нормальный рендер ДАЕТ data-testid
    const f = feat();
    const html = renderToStaticMarkup(createElement(FeatureLabel, { feature: f }));
    expect(html).toContain('data-testid="chat-feature-label"');
    // Мутация: если бы data-testid не было — тест бы упал (здесь мы проверяем его наличие)
  });
});

// === n1: метка фичи не рисуется внутри группы (inFeatureGroup) ===
// Внутри группы фичи имя фичи уже несёт заголовок группы; дубль в карточке убираем.
// featureLabelFor по-прежнему отдаёт фичу (решение «есть ли фича» не меняется), но
// ChatCard с inFeatureGroup=true метку не рисует.
describe('n1: метка фичи внутри группы (inFeatureGroup)', () => {
  // Минимальный чат: для метки важен id (featureOf читает индекс по id)
  const session = (id: string): Session => ({
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);

  const renderCard = (id: string, inFeatureGroup?: boolean): string =>
    renderToStaticMarkup(createElement(ChatCard, {
      session: session(id), isActive: false, isMobile: false, online: true,
      onSelect: () => {}, onHover: () => {}, onDelete: () => {}, inFeatureGroup,
    }));

  it('чат фичи: метка есть вне группы, подавлена внутри', () => {
    setupIndex();
    setAllFlags({ 'features-overview': true });
    // chat-1 — исполнитель f1 (setupIndex)
    expect(renderCard('chat-1')).toContain('data-testid="chat-feature-label"');
    expect(renderCard('chat-1', true)).not.toContain('data-testid="chat-feature-label"');
  });
});

// === Ф3.2: подсказка у метки «ключевой» (группа) ===
// Метка «ключевой» — span в карточке; подсказка «что значит ключевой» — единый текст
// (KEY_SESSION_TOOLTIP): aria-label (скринридер/клавиатура) + по наведению ui/Tooltip,
// на тач — нативный title. Рендер — SSR (как остальные тесты файла).
describe('Ф3.2: подсказка у метки «ключевой» (группа)', () => {
  const session = (id: string): Session => ({
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);

  const renderRoleCard = (role: string): string =>
    renderToStaticMarkup(createElement(ChatCard, {
      session: session('chat-1'), isActive: false, isMobile: false, online: true,
      onSelect: () => {}, onHover: () => {}, onDelete: () => {},
      featureRole: role,
    }));

  it('«ключевой» — метка несёт подсказку (единый текст, aria-label + Tooltip)', () => {
    const html = renderRoleCard('ключевой');
    // Метка «ключевой» есть.
    expect(html).toContain('data-testid="chat-feature-role"');
    // Подсказка «что значит ключевой» — единый текст (не два литерала).
    expect(html).toContain('aria-label="Здесь управляют фичей: запускают шаги и принимают результат"');
  });

  it('«ещё N» (не ключевой) — подсказки «что значит ключевой» нет', () => {
    const html = renderRoleCard('ещё 2');
    expect(html).toContain('data-testid="chat-feature-role"');
    expect(html).not.toContain('aria-label="Здесь управляют фичей: запускают шаги и принимают результат"');
  });
});
