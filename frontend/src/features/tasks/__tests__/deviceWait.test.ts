// Состояния «ждёт устройство» / «не дождалась устройства» у задачи (ADR-016, вариант А):
// тексты словаря и плашки карточки. Карточку рендерим статикой через react-dom/server —
// DOM-тестов у карточек нет, проверяем само дерево
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { Task } from '../../../types';
import { deviceWaitDuration, executorStopText, isDeviceWaiting, isTerminalStop } from '../../../lib/tasks';
import { TaskCard } from '../TaskCard';

function task(over: Partial<Task> = {}): Task {
  return {
    id: 't1', title: 'Собрать отчёт', status: 'todo', priority: 'medium', assignee: 'claude',
    projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: 0,
    createdAt: '2026-09-24T00:00:00Z', updatedAt: '2026-09-24T00:00:00Z', ...over,
  } as Task;
}

const render = (t: Task, deviceName?: string) =>
  renderToStaticMarkup(createElement(TaskCard, { task: t, deviceName, onClick: () => {} }));

describe('словарь ожидания устройства', () => {
  it('ждёт только незакрытая задача с отметкой', () => {
    expect(isDeviceWaiting({ deviceWaitSince: '2026-09-24T10:00:00Z', status: 'todo' })).toBe(true);
    expect(isDeviceWaiting({ deviceWaitSince: '2026-09-24T10:00:00Z', status: 'done' })).toBe(false);
    expect(isDeviceWaiting({ deviceWaitSince: null, status: 'todo' })).toBe(false);
  });

  it('длительность ожидания — минуты и часы', () => {
    const since = '2026-09-24T10:00:00Z';
    const at = (min: number) => new Date(since).getTime() + min * 60000;
    expect(deviceWaitDuration(since, at(0))).toBe('меньше минуты');
    expect(deviceWaitDuration(since, at(42))).toBe('42 мин');
    expect(deviceWaitDuration(since, at(120))).toBe('2 ч');
    expect(deviceWaitDuration(since, at(200))).toBe('3 ч 20 мин');
  });

  it('device_wait_expired — человеческий текст с именем устройства, не код', () => {
    expect(executorStopText('device_wait_expired', 'Ноутбук'))
      .toBe('Устройство «Ноутбук» не вышло на связь за 24 часа — задача не запускалась');
    expect(executorStopText('device_wait_expired'))
      .toBe('Устройство проекта не вышло на связь за 24 часа — задача не запускалась');
    expect(executorStopText('something_new')).toBe('Исполнение прервано');
  });

  // W4-HOLD-J (R-HOLD-J п.5): терминальная причина «чат исполнителя исчез» переведена
  // на фронте так же, как на бэке, а не сводится к общему «Исполнение прервано».
  it('feature_held_expired — чат исчез, шаг перезапускают', () => {
    expect(executorStopText('feature_held_expired'))
      .toBe('Чат исполнителя исчез за время паузы — продолжения нет, шаг нужно перезапустить');
  });
});

describe('карточка задачи', () => {
  it('в ожидании показывает устройство и сколько ждёт', () => {
    const since = new Date(Date.now() - 90 * 60000).toISOString();
    const html = render(task({ deviceWaitSince: since, deviceWaitReason: 'Устройство офлайн' }), 'Ноутбук');
    expect(html).toContain('Ждёт устройство «Ноутбук» · 1 ч 30 мин');
    expect(html).toContain('title="Устройство офлайн"');
  });

  it('истёкшее ожидание — плашка с текстом причины, а не кодом', () => {
    const html = render(task({ executorStoppedAt: '2026-09-24T10:00:00Z', executorStopReason: 'device_wait_expired' }), 'Ноутбук');
    expect(html).toContain('Не запускалась: устройство не в сети');
    expect(html).toContain('Устройство «Ноутбук» не вышло на связь за 24 часа');
    expect(html).not.toContain('device_wait_expired');
  });

  it('обычная задача плашек ожидания не получает', () => {
    const html = render(task());
    expect(html).not.toContain('Ждёт');
    expect(html).not.toContain('Не запускалась');
  });
});

// W4-HOLD-F4 (R-HOLD-J2): причина ЛЮБОЙ терминальной остановки — на карточке,
// а не только в уведомлении бэка. Мутация: удалить строку причины в словаре —
// тест краснеет (текст уходит в общую формулировку).
describe('терминальная остановка исполнителя', () => {
  it('зеркало классификатора: восстановимые обрывы — не терминальные', () => {
    expect(isTerminalStop(null)).toBe(false);
    expect(isTerminalStop('subagent_truncated')).toBe(false);
    expect(isTerminalStop('tool_call_leaked')).toBe(false);
    expect(isTerminalStop('feature_held_expired')).toBe(true);
    expect(isTerminalStop('auth_failed')).toBe(true);
  });

  it('feature_held_expired — причина на карточке человеческим текстом, не кодом', () => {
    const html = render(task({ executorStoppedAt: '2026-10-23T10:00:00Z', executorStopReason: 'feature_held_expired' }));
    expect(html).toContain('Чат исполнителя исчез за время паузы');
    expect(html).not.toContain('feature_held_expired');
  });

  it('auth_failed и subagent_stuck — своя формулировка, не общая', () => {
    expect(render(task({ executorStopReason: 'auth_failed' })))
      .toContain('Не удалось авторизоваться у провайдера модели');
    expect(render(task({ executorStopReason: 'subagent_stuck' })))
      .toContain('Сабагент раз за разом обрывается');
  });

  it('неизвестная причина — общая формулировка, служебный ключ не светится', () => {
    const html = render(task({ executorStopReason: 'something_new' }));
    expect(html).toContain('Исполнение прервано');
    expect(html).not.toContain('something_new');
  });

  it('восстановимый обрыв карточку не помечает', () => {
    const html = render(task({ executorStopReason: 'subagent_truncated' }));
    expect(html).not.toContain('Исполнение прервано');
  });

  it('завершённая задача плашки остановки не получает', () => {
    const html = render(task({ status: 'done', executorStopReason: 'feature_held_expired' }));
    expect(html).not.toContain('Чат исполнителя');
  });
});
