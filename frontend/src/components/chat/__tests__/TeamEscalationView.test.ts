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
    // Пункты состава — настоящий markdown-список, а не одна простыня
    expect(html.match(/<li/g)?.length).toBe(2);
    // Абзац под составом остался абзацем, а не уехал ленивым продолжением в последний пункт
    expect(html.split('</ul>')[1]).toContain('Работа уже идёт');
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

  // Волна 3: карточка погашена — chosenActionId и/или resolutionNote определяют,
  // какой текст показать. До правки все три варианта показывали «Решение: <label>»
  // (или пустой фолбэк), и в ленте результат «снято штабом» ничем не отличался от
  // «ответил кнопкой». Теперь каждое исчисление имеет свой текст и сохраняет
  // смысл «кто и чем закрыл карточку»
  describe('погашенная карточка показывает исход правильно', () => {
    it('chosenActionId="resolvedByStaff" с resolutionNote — подпись «Снят штабом: <note>»', () => {
      const html = render(card({
        kind: 'blocker', title: 'Исполнитель застрял', details: '',
        resolved: true, chosenActionId: 'resolvedByStaff',
        resolutionNote: 'доступ к реестру ограничен — обошёл через ручной ввод',
      }));
      expect(html).toContain('Снят штабом: доступ к реестру ограничен — обошёл через ручной ввод');
      expect(html).not.toContain('Решение:');
      // Никаких кнопок на погашенной карточке
      expect(html).not.toContain('<button');
    });

    it('chosenActionId="resolvedByStaff" без resolutionNote — короткая форма без тела', () => {
      // Поле опциональное: старый бэкенд и ненулевой chosenActionId без подробностей
      // рисуем «Снят штабом», без двоеточия и без «undefined»
      const html = render(card({
        kind: 'blocker', title: 'Блокер №2', details: '',
        resolved: true, chosenActionId: 'resolvedByStaff',
        resolutionNote: null,
      }));
      expect(html).toContain('Снят штабом');
      expect(html).not.toContain('Снят штабом:');
      expect(html).not.toContain('undefined');
      expect(html).not.toContain('Решение:');
    });

    it('chosenActionId="message" — человек ответил обычным сообщением', () => {
      // chosenActionId === 'message' теперь читается как «Ответ сообщением» —
      // раньше попадало в общую ветку и показывало «Решение: <label>» с пустой подписью,
      // что для человека выглядело как «ничего не произошло»
      const html = render(card({
        kind: 'blocker', title: 'Исполнитель застрял', details: '',
        actions: [{ id: 'answer', label: 'Ответить' }],
        resolved: true, chosenActionId: 'message',
      }));
      expect(html).toContain('Ответ сообщением');
      expect(html).not.toContain('Решение:');
    });

    it('прочие chosenActionId — старая ветка «Решение: <label>»', () => {
      // chosenActionId="skip" у taskFailed (пропустить), решили по кнопке — текст
      // подписи кнопки должен отображаться как раньше. Это даёт совместимость
      // с уже сохранённой историей
      const html = render(card({
        kind: 'taskFailed', title: 'Задача провалилась', details: '',
        actions: [
          { id: 'retry', label: 'Перезапустить задачу' },
          { id: 'skip', label: 'Пропустить и продолжить' },
        ],
        resolved: true, chosenActionId: 'skip',
      }));
      expect(html).toContain('Решение: Пропустить и продолжить');
    });

    it('chosenActionId="drop" с подписью штаба — единая ветка «Снят штабом» (обратная совместимость)', () => {
      // chosenActionId="drop" идёт через старую ветку (как «skip»): карточка показывает
      // подпись нажатой кнопки. Это «Снять задачу у исполнителя» — на погашенной карточке
      // напоминает человек о действии без подробностей
      const html = render(card({
        kind: 'blocker', title: 'Исполнитель «Катя» уперлась в типизацию',
        details: '', actions: [{ id: 'drop', label: 'Снять задачу у исполнителя' }],
        resolved: true, chosenActionId: 'drop',
      }));
      expect(html).toContain('Решение: Снять задачу у исполнителя');
    });
  });
});
