// Логика экранов сфер: удаление, «убрать из сферы», полки памяти, ошибки страницы.
// Окружение vitest — node, поэтому тестируем чистую логику и статический рендер полок.
import { describe, it, expect, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { SphereMemoryEntry, SphereMemoryResponse } from '../../types';
import {
  deleteDialogMode, deleteFailure, describeLoadError, memoryAdded, memoryAdopted, memoryRemoved, startRemoveFlow,
} from './sphereLogic';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 360, innerHeight: 740,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
const { SphereMemoryShelves } = await import('./SphereMemory');

const httpError = (status: number, message = 'err', body?: unknown) =>
  Object.assign(new Error(message), { status, body });

describe('удаление сферы', () => {
  it('без персон — подтверждение, с персонами — блокировка, до ответа — загрузка', () => {
    expect(deleteDialogMode({ memory: null, personas: 0, error: '' })).toBe('loading');
    expect(deleteDialogMode({ memory: 3, personas: 0, error: '' })).toBe('confirm');
    expect(deleteDialogMode({ memory: 3, personas: 2, error: '' })).toBe('blocked');
    expect(deleteDialogMode({ memory: 3, personas: 0, error: 'сбой' })).toBe('error');
  });

  it('409 даёт число персон из тела, а не текст ошибки', () => {
    expect(deleteFailure(httpError(409, 'conflict', { personas: 2 }))).toEqual({ personas: 2 });
    expect(deleteFailure(httpError(409, 'conflict'))).toEqual({ personas: 1 });
  });

  it('прочая ошибка — текст причины', () => {
    expect(deleteFailure(httpError(500, 'упало'))).toEqual({ error: 'упало' });
    expect(deleteFailure(new Error(''))).toEqual({ error: 'Не удалось удалить сферу' });
  });
});

describe('«убрать из сферы»', () => {
  const member = (name: string) => ({ id: name, name, handle: name, role: null });

  it('без команды проект убирается сразу, без подтверждения', async () => {
    const remove = vi.fn().mockResolvedValue(undefined);
    const r = await startRemoveFlow({ loadTeam: async () => [], remove });
    expect(r).toEqual({ kind: 'done' });
    expect(remove).toHaveBeenCalledTimes(1);
  });

  it('с командой — просит подтверждение и ничего не убирает', async () => {
    const remove = vi.fn();
    const r = await startRemoveFlow({ loadTeam: async () => [member('Алексей'), member('Ирина')], remove });
    expect(r).toEqual({ kind: 'confirm', team: ['Алексей', 'Ирина'] });
    expect(remove).not.toHaveBeenCalled();
  });

  it('сбой загрузки или удаления — ошибка с текстом', async () => {
    const r = await startRemoveFlow({ loadTeam: async () => { throw new Error('нет сети'); }, remove: async () => {} });
    expect(r).toEqual({ kind: 'error', error: 'нет сети' });
    const r2 = await startRemoveFlow({ loadTeam: async () => [], remove: async () => { throw new Error('отказ'); } });
    expect(r2).toEqual({ kind: 'error', error: 'отказ' });
  });
});

describe('страница сферы: ошибка загрузки', () => {
  it('404 — «сферы нет», без повтора', () => {
    expect(describeLoadError(httpError(404, 'Not Found')).kind).toBe('notFound');
  });
  it('сбой сети — повторяемая ошибка с текстом', () => {
    expect(describeLoadError(new TypeError('Failed to fetch'))).toEqual({ kind: 'network', text: 'Failed to fetch' });
    expect(describeLoadError('что-то').text).toBe('Не удалось загрузить сферу');
  });
});

const sphereEntry = (id: string, over: Partial<SphereMemoryEntry> = {}): SphereMemoryEntry => ({
  id, scopeId: 's1', text: `Запись ${id}`, type: 'fact', salience: 1, source: 'manual',
  createdAt: '2026-10-04T10:00:00Z', promotedFrom: null, ...over,
});
const base = (): SphereMemoryResponse => ({
  sphere: [sphereEntry('a')],
  projects: [
    { projectId: 'p1', projectName: 'ВФЛА-ПСБ', entries: [
      { id: 'e1', projectId: 'p1', text: 'Реестр пересчитывается ночью', type: 'fact', salience: 1, source: 'manual', createdAt: '2026-10-04T10:00:00Z' },
      { id: 'e2', projectId: 'p1', text: 'Релизы по пятницам', type: 'convention', salience: 1, source: 'manual', createdAt: '2026-10-03T10:00:00Z' },
    ] },
    { projectId: 'p2', projectName: 'Миграция', entries: [] },
  ],
  maxEntries: 200,
});

describe('память сферы', () => {
  it('adopt убирает запись с полки проекта и кладёт её в полку сферы', () => {
    const next = memoryAdopted(base(), 'p1', 'e1', sphereEntry('n1', { text: 'Реестр пересчитывается ночью', promotedFrom: { projectId: 'p1', entryId: 'e1', at: '' } }));
    expect(next.projects[0].entries.map(e => e.id)).toEqual(['e2']);
    expect(next.sphere.map(e => e.id)).toEqual(['n1', 'a']);
    expect(next.projects[1]).toEqual(base().projects[1]);
  });

  it('добавление и удаление правят только полку сферы', () => {
    const added = memoryAdded(base(), sphereEntry('b'));
    expect(added.sphere.map(e => e.id)).toEqual(['b', 'a']);
    expect(memoryRemoved(added, 'a').sphere.map(e => e.id)).toEqual(['b']);
    expect(memoryRemoved(added, 'a').projects).toEqual(added.projects);
  });

  const render = (data: SphereMemoryResponse) => renderToStaticMarkup(createElement(SphereMemoryShelves, {
    data, busyId: null, isMobile: true, onAdopt: () => {}, onRemove: () => {},
  }));

  it('полки: записи проектов с кнопкой переноса, записи сферы — с удалением', () => {
    const html = render(base());
    expect(html).toContain('Память сферы');
    expect(html).toContain('Память проектов сферы');
    expect(html).toContain('Запись a');
    expect(html).toContain('Релизы по пятницам');
    expect((html.match(/Перенести в сферу/g) ?? []).length).toBe(2); // по кнопке на каждую из двух записей проекта
    expect(html).toContain('Удалить запись');
  });

  it('пустые состояния обеих полок', () => {
    const html = render({ sphere: [], projects: [{ projectId: 'p1', projectName: 'X', entries: [] }], maxEntries: 200 });
    expect(html).toContain('Здесь соберутся решения и договорённости');
    expect(html).toContain('нечего переносить');
    expect(html).not.toContain('Перенести в сферу');
  });
});
