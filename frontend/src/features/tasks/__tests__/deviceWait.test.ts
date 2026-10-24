// Состояния «ждёт устройство» / «не дождалась устройства» у задачи (ADR-016, вариант А):
// тексты словаря и плашки карточки. Карточку рендерим статикой через react-dom/server —
// DOM-тестов у карточек нет, проверяем само дерево
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { LucideIcon } from 'lucide-react';
import type { Task } from '../../../types';
import { deviceWaitDuration, executorStopVisual, isDeviceWaiting, isTerminalStop } from '../../../lib/tasks';
import { TaskCard } from '../TaskCard';
import { TaskDetailsPane } from '../TaskDetailsPane';

function task(over: Partial<Task> = {}): Task {
  return {
    id: 't1', title: 'Собрать отчёт', status: 'todo', priority: 'medium', assignee: 'claude',
    projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: 0,
    createdAt: '2026-09-24T00:00:00Z', updatedAt: '2026-09-24T00:00:00Z', ...over,
  } as Task;
}

const render = (t: Task, deviceName?: string) =>
  renderToStaticMarkup(createElement(TaskCard, { task: t, deviceName, onClick: () => {} }));

// Глиф lucide по иконке — общий helper проверок карточки и панели (хвост R-F7: был дубль
// внутри двух describe-блоков, вынесен на уровень файла)
const iconClass = (icon: LucideIcon) =>
  /lucide-[a-z0-9-]+/.exec(renderToStaticMarkup(createElement(icon, { size: 16 })))![0];

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
    expect(executorStopVisual('device_wait_expired', 'Ноутбук').text)
      .toBe('Устройство «Ноутбук» не вышло на связь за 24 часа — задача не запускалась');
    expect(executorStopVisual('device_wait_expired').text)
      .toBe('Устройство проекта не вышло на связь за 24 часа — задача не запускалась');
    expect(executorStopVisual('something_new').text).toBe('Исполнение прервано');
  });

  // W4-HOLD-J (R-HOLD-J п.5): терминальная причина «чат исполнителя исчез» переведена
  // на фронте так же, как на бэке, а не сводится к общему «Исполнение прервано».
  it('feature_held_expired — чат исчез, шаг перезапускают', () => {
    expect(executorStopVisual('feature_held_expired').text)
      .toBe('Чат исполнителя исчез за время паузы — продолжения нет, шаг нужно перезапустить');
  });

  // W4-HOLD-F4 (возврат R-F4): feature_held — штатная пауза фичи, не авария: своя
  // формулировка и предупреждающий тон, а не красная «Исполнение прервано».
  // Мутация: удалить кейс 'feature_held' в executorStopVisual — тест краснеет.
  it('feature_held — пауза названа паузой, тон warning, не danger', () => {
    expect(executorStopVisual('feature_held').text).toBe('Фичу удержали на паузе — шаг ждёт снятия паузы');
    expect(executorStopVisual('feature_held').tone).toBe('warning');
    // остальные терминальные остановки — настоящая беда
    expect(executorStopVisual('auth_failed').tone).toBe('danger');
    expect(executorStopVisual('device_wait_expired').tone).toBe('danger');
  });

  // Хвост R-F4 п.1: формулировка tool_call_leak_stuck была добавлена без теста.
  it('tool_call_leak_stuck — своя формулировка и терминальность', () => {
    expect(executorStopVisual('tool_call_leak_stuck').text)
      .toBe('Вызов инструмента раз за разом уходит в текст, повторить его штатно не удалось');
    expect(isTerminalStop('tool_call_leak_stuck')).toBe(true);
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

  // Хвост R-F4 п.3: текст причины device_wait_expired содержит фразу title старой
  // плашки — гейт на «плашка ровно одна», чтобы двойная плашка не пролезла.
  it('истёкшее ожидание — ровно одна плашка остановки', () => {
    const html = render(task({ executorStoppedAt: '2026-09-24T10:00:00Z', executorStopReason: 'device_wait_expired' }), 'Ноутбук');
    expect((html.match(/не вышло на связь/gi) || []).length).toBe(1);
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
    expect(isTerminalStop('feature_held')).toBe(true);
    expect(isTerminalStop('auth_failed')).toBe(true);
  });

  // Хвост R-F4 п.2: feature_held — самая частая причина на карточке в волне (пауза).
  // Мутация: удалить кейс 'feature_held' в executorStopVisual — тест краснеет.
  it('feature_held — карточка называет паузу, а не поломку', () => {
    const html = render(task({ executorStoppedAt: '2026-10-23T10:00:00Z', executorStopReason: 'feature_held' }));
    expect(html).toContain('Фичу удержали на паузе');
    expect(html).not.toContain('Исполнение прервано');
    expect(html).not.toContain('feature_held');
  });

  // W4-HOLD-F5 (хвост R-F4R): чип паузы размечен глифом Pause (тот же, что у кнопки
  // «Пауза» в шапке фичи), а не Unplug — «устройство отключено» читается как поломка.
  // Мутация: вернуть в StateChip иконку Unplug для feature_held — тест краснеет.
  it('feature_held — чип отмечен паузой, а не «отключённым устройством»', () => {
    const html = render(task({ executorStopReason: 'feature_held' }));
    expect(html).toContain('lucide-pause');
    expect(html).not.toContain('lucide-unplug');
  });

  // W4-HOLD-F5 (п.2 R-F5): карточка не собирает чип сама — текст, тон и глиф приходят
  // из одного триплета executorStopVisual. Мутация: зафиксировать что-то из трёх
  // параллельно в карточке — тест краснеет.
  it('единый источник: карточка повторяет текст, тон и глиф функции', () => {
    for (const reason of ['feature_held', 'feature_held_expired', 'auth_failed', 'device_wait_expired']) {
      const v = executorStopVisual(reason);
      const html = render(task({ executorStopReason: reason }));
      expect(html).toContain(v.text);
      expect(html).toContain(iconClass(v.icon));
      // Тон чипа — цвет соответствующего Badge
      expect(html).toContain(v.tone === 'warning' ? 'var(--c-warning-bg)' : 'var(--c-danger-bg)');
    }
  });

  // W4-HOLD-F5 (п.1 R-F5): featureHeldExpired — про исчезнувший чат паузы, а не про
  // оборванную связь: свой глиф MessageSquareOff, а не Unplug.
  // Мутация: заменить MessageSquareOff на Unplug в executorStopVisual — тест краснеет.
  it('feature_held_expired — отмечен исчезнувшим чатом, а не «отключённым устройством»', () => {
    const html = render(task({ executorStopReason: 'feature_held_expired' }));
    expect(html).toContain('lucide-message-square-off');
    expect(html).not.toContain('lucide-unplug');
  });

  // Ожидание устройства — действительно «связь потеряна»: там Unplug остаётся.
  it('ожидание устройства — глиф Unplug, а не Pause', () => {
    const html = render(task({ deviceWaitSince: new Date(Date.now() - 60000).toISOString() }));
    expect(html).toContain('lucide-unplug');
    expect(html).not.toContain('lucide-pause');
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

  // Хвост R-F4 п.4: проверяется отсутствие ЛЮБОЙ формулировки остановки, а не
  // одной подстроки — плашка с другой фразой раньше прошла бы.
  it('завершённая задача плашки остановки не получает', () => {
    const html = render(task({ status: 'done', executorStopReason: 'feature_held_expired' }));
    expect(html).not.toContain(executorStopVisual('feature_held_expired').text);
    expect(html).not.toContain('Исполнение прервано');
  });
});

// W4-HOLD-F6 (хвост R-F6): панель деталей берёт текст, тон и глиф остановки из того же
// executorStopVisual, что и карточка (раньше тон и глиф в панели были жёсткими).
// Мутация: вернуть в панели tone/icon жёсткими — тест краснеет.
describe('панель деталей задачи', () => {
  const renderPane = (t: Task) =>
    renderToStaticMarkup(createElement(TaskDetailsPane, { task: t, project: null, onDeleted: () => {} }));
  // Notice — единственный элемент с role="status" в панели: его стиль и есть тон плашки
  const noticeStyle = (html: string) => /role="status" style="([^"]*)"/.exec(html)?.[1] ?? '';

  it('feature_held — панель отмечает паузу Pause и warning, а не Unplug и danger', () => {
    const html = renderPane(task({ executorStopReason: 'feature_held' }));
    expect(html).toContain('Фичу удержали на паузе');
    expect(html).toContain('lucide-pause');
    expect(html).not.toContain('lucide-unplug');
    expect(noticeStyle(html)).toContain('background:var(--c-warning-bg)');
  });

  // Единый источник: любой терминальной причине панель даёт тот же триплет, что и функция
  it('единый источник: панель повторяет текст, тон и глиф функции', () => {
    for (const reason of ['feature_held', 'feature_held_expired', 'auth_failed', 'device_wait_expired']) {
      const v = executorStopVisual(reason);
      const html = renderPane(task({ executorStopReason: reason }));
      expect(html).toContain(v.text);
      expect(html).toContain(iconClass(v.icon));
      expect(noticeStyle(html)).toContain(
        'background:' + (v.tone === 'warning' ? 'var(--c-warning-bg)' : 'var(--c-danger-bg)'));
    }
  });

  it('завершённая задача плашки остановки в панели не получает', () => {
    const html = renderPane(task({ status: 'done', executorStopReason: 'feature_held' }));
    expect(html).not.toContain('Фичу удержали на паузе');
    expect(noticeStyle(html)).toBe('');
  });

  // Хвост R-F7: обратный тест для панели — восстановимая причина (subagent_truncated)
  // не терминальная, плашки остановки быть не должно. Мутация: снять isTerminalStop
  // в гейте TaskDetailsPane.tsx:251 — тест краснеет (появляются «Исполнение прервано»
  // и Notice с role="status").
  it('восстановимый обрыв — панель без плашки остановки и без role="status"', () => {
    const html = renderPane(task({ executorStopReason: 'subagent_truncated' }));
    expect(html).not.toContain('Исполнение прервано');
    expect(noticeStyle(html)).toBe('');
  });

  // R-F7: бэк FeatureProjector.IsTurnRunning считает шаг с executorStoppedAt stopped,
  // а панель по старому предикату («startedAt есть, результата нет») всё равно рисовала
  // спиннер «AI работает» рядом с плашкой паузы. Мутация: убрать !task.executorStoppedAt
  // из предиката TaskDetailsPane.tsx:254 — этот тест краснеет.
  it('остановленный паузой фичи исполнитель — без спиннера «AI работает»', () => {
    const html = renderPane(task({
      status: 'inProgress',
      claudeStartedAt: '2026-10-23T10:00:00Z',
      executorStoppedAt: '2026-10-23T10:05:00Z',
      executorStopReason: 'feature_held',
    }));
    expect(html).not.toContain('AI работает над задачей');
    expect(html).toContain('Фичу удержали на паузе');
  });

  // Тот же предикат, что бэк: явное ожидание человека — не идущий ход.
  it('ждущий человека исполнитель — без спиннера «AI работает»', () => {
    const html = renderPane(task({
      status: 'inProgress',
      claudeStartedAt: '2026-10-23T10:00:00Z',
      awaitingHumanSince: '2026-10-23T10:05:00Z',
    }));
    expect(html).not.toContain('AI работает над задачей');
  });

  // Против стороны: ход, который действительно идёт, спиннер сохраняет.
  it('идущий ход — спиннер «AI работает» есть', () => {
    const html = renderPane(task({ status: 'inProgress', claudeStartedAt: '2026-10-23T10:00:00Z' }));
    expect(html).toContain('AI работает над задачей');
  });
});
