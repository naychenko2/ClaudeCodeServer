// Карточка эскалации в ленте: рендер без падений на всех видах, включая незнакомый
// с бэка токен (виды заводятся в бэкенде раньше, чем про них узнаёт фронт — Э5 добавил
// waveAdded). Рендерим статикой через react-dom/server: DOM-тестов у ленты нет,
// а проверить надо само дерево — JSX здесь не нужен, вызовы идут через createElement
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem, TeamEscalation, TeamEscalationKind } from '../../../types';
import { TeamEscalationView } from '../TeamEscalationView';

type EscalationItem = Extract<ChatItem, { kind: 'team_escalation' }>;

const WAVE_ADDED_DETAILS = [
  'Под-задач: 2 · волн: 1 · исполнителей: 2.',
  '— Экспорт в XLSX (волна 1)',
  '— Кнопка выгрузки (волна 1)',
  'Работа уже идёт: авто-волны включены, и подтверждения она не ждёт. Нажмите «Остановить», если это лишнее.',
].join('\n');

function card(escalation: Partial<TeamEscalation> & { kind: TeamEscalationKind }): EscalationItem {
  return {
    kind: 'team_escalation',
    escalationId: 'e1',
    escalation: {
      id: 'e1', title: 'Заголовок', details: '', actions: [], taskId: null,
      wave: 1, resolved: false, chosenActionId: null, ...escalation,
    },
  };
}

const render = (item: EscalationItem) =>
  renderToStaticMarkup(createElement(TeamEscalationView, { item, online: true }));

describe('TeamEscalationView', () => {
  it('waveAdded: состав под-задач идёт списком, кнопка одна — «Остановить»', () => {
    const html = render(card({
      kind: 'waveAdded',
      title: 'Новая вводная в работе: экспорт трат',
      details: WAVE_ADDED_DETAILS,
      actions: [{ id: 'stop', label: 'Остановить' }],
    }));
    expect(html).toContain('Новая вводная в работе: экспорт трат');
    expect(html).toContain('Экспорт в XLSX (волна 1)');
    expect(html).toContain('Кнопка выгрузки (волна 1)');
    // Пункты состава — отдельные строки с маркером, а не одна простыня
    expect(html.match(/•/g)?.length).toBe(2);
    expect(html.match(/<button/g)?.length).toBe(1);
    expect(html).toContain('Остановить');
    // Информационная карточка не зовёт нажимать: accent-кнопки главного действия нет
    expect(html).not.toContain('background:var(--c-accent)');
  });

  it('waveAdded остановленная: карточка гаснет с отметкой решения', () => {
    const html = render(card({
      kind: 'waveAdded',
      title: 'Новая вводная в работе: экспорт трат',
      details: WAVE_ADDED_DETAILS,
      actions: [{ id: 'stop', label: 'Остановить' }],
      resolved: true, chosenActionId: 'stop',
    }));
    expect(html).toContain('Решение: Остановить');
    expect(html).not.toContain('<button');
  });

  it('незнакомый с бэка kind рисуется дефолтной карточкой, а не роняет ленту', () => {
    const html = render(card({
      kind: 'somethingNewFromBackend' as TeamEscalationKind,
      title: 'Что-то новое',
      details: 'Подробности одной строкой',
      actions: [{ id: 'ok', label: 'Понятно' }],
    }));
    expect(html).toContain('Что-то новое');
    expect(html).toContain('Подробности одной строкой');
    expect(html).toContain('Понятно');
  });

  it('штатные виды по-прежнему на месте', () => {
    expect(render(card({ kind: 'blocker', title: 'Исполнитель застрял', details: 'нет доступа' })))
      .toContain('Исполнитель застрял');
    expect(render(card({
      kind: 'waveGate', title: 'Волна 1 закрыта. Запустить волну 2?',
      actions: [{ id: 'runNext', label: 'Запустить' }],
    }))).toContain('Запустить');
  });
});
