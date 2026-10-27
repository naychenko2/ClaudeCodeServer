import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { Task } from '../../../types';
import { TASKS_LOAD_INITIAL, settleTasks, heroCount } from '../tasksLoad';

vi.mock('../../../lib/api', () => ({ api: {} }));
vi.mock('../../../lib/signalr', () => ({ onMessage: vi.fn(), onReconnected: vi.fn(), joinUser: vi.fn() }));

import { TasksSection } from '../../../components/PersonaContextTab';

const task = (id: string, status = 'todo'): Task => ({ id, title: `Задача ${id}`, status } as Task);

describe('TeamCommandCenter — состояние задач при сбое', () => {
  it('сбой на первой загрузке: список остаётся null, hero — прочерк, а не «0»', () => {
    const s = settleTasks(TASKS_LOAD_INITIAL, { err: new Error('offline') });
    expect(s).toEqual({ tasks: null, failed: true });
    expect(heroCount(s.tasks, 0)).toBe('—');
  });

  it('сбой после успеха: прежний список не затирается, пометка failed', () => {
    const ok = settleTasks(TASKS_LOAD_INITIAL, { ok: [task('a'), task('b')] });
    const s = settleTasks(ok, { err: new Error('offline') });
    expect(s.tasks).toHaveLength(2);
    expect(s.failed).toBe(true);
    expect(heroCount(s.tasks, 2)).toBe('2');
  });

  it('успешный повтор снимает пометку', () => {
    const failed = settleTasks(TASKS_LOAD_INITIAL, { err: 1 });
    expect(settleTasks(failed, { ok: [] })).toEqual({ tasks: [], failed: false });
  });

  it('честный пустой список — «0», не прочерк', () => {
    expect(heroCount([], 0)).toBe('0');
  });
});

describe('PersonaContextTab — секция «Задачи» при сбое', () => {
  const render = (props: { tasks: Task[] | null; failed: boolean }) =>
    renderToStaticMarkup(createElement(TasksSection, { ...props, onRetry: () => {} }));

  it('сбой: причина и «Повторить», без «Нет активных задач.»', () => {
    const html = render({ tasks: null, failed: true });
    expect(html).toContain('Нет связи — показать не удалось.');
    expect(html).toContain('Повторить');
    expect(html).not.toContain('Нет активных задач.');
  });

  it('пустой успешный ответ по-прежнему говорит «Нет активных задач.»', () => {
    expect(render({ tasks: [], failed: false })).toContain('Нет активных задач.');
  });

  it('есть задачи — рисуются', () => {
    expect(render({ tasks: [task('x')], failed: false })).toContain('Задача x');
  });
});
