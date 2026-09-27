// Фазы агентной сборки на клиенте — та же формула «блокирует», что у сервера
// (ArchitectureAgentLauncherAdapter.FindBlocking), плюс видимость ошибки пересборки.
import { describe, expect, it, vi } from 'vitest';
import type { Task } from '../../../types';

vi.mock('aihome_shell/kit', () => ({}));
const { resolveAgentBuild, agentBlockedHint, rebuildErrorOf } = await import('../ArchitectureBuild');

const P = 'proj-a';
const task = (patch: Partial<Task>): Task => ({
  id: 't1', projectId: P, title: 'Сборка', description: '', status: 'todo', priority: 'medium',
  labels: ['arch-build'], ...patch,
} as Task);

describe('resolveAgentBuild: фазы', () => {
  it('стартовавшая задача без итога — running', () => {
    expect(resolveAgentBuild([task({ claudeStartedAt: 't' })], P, null, false).phase).toBe('running');
    expect(resolveAgentBuild([task({ status: 'inProgress' })], P, null, false).phase).toBe('running');
  });

  it('не стартовавшая незакрытая задача — stalled, агент заблокирован с подсказкой', () => {
    const r = resolveAgentBuild([task({})], P, null, false);
    expect(r.phase).toBe('stalled');
    expect(r.taskId).toBe('t1');
    expect(agentBlockedHint(r.phase)).toMatch(/не закрыта/);
  });

  it('остановленная исполнителем или с ошибкой, но не закрытая — stalled', () => {
    expect(resolveAgentBuild([task({ claudeStartedAt: 't', executorStoppedAt: 't' })], P, null, false).phase).toBe('stalled');
    expect(resolveAgentBuild([task({ status: 'inProgress', claudeResult: 'error' })], P, null, false).phase).toBe('stalled');
  });

  it('метка в другом регистре тоже блокирует; закрытая или чужого проекта — нет', () => {
    expect(resolveAgentBuild([task({ labels: ['Arch-Build'] })], P, null, false).phase).toBe('stalled');
    expect(resolveAgentBuild([task({ status: 'done' })], P, null, false).phase).toBeNull();
    expect(resolveAgentBuild([task({ projectId: 'other' })], P, null, false).phase).toBeNull();
  });

  it('известная задача: закрыта — done, закрыта с ошибкой — failed', () => {
    expect(resolveAgentBuild([task({ status: 'done', claudeResult: 'success' })], P, 't1', true).phase).toBe('done');
    expect(resolveAgentBuild([task({ status: 'done', claudeResult: 'error' })], P, 't1', true).phase).toBe('failed');
  });

  it('удалённая задача: до первого появления — оптимистичный running, после — null', () => {
    expect(resolveAgentBuild([], P, 't1', false).phase).toBe('running');
    const gone = resolveAgentBuild([], P, 't1', true);
    expect(gone.phase).toBeNull();
    expect(gone.taskId).toBeNull();
    expect(agentBlockedHint(gone.phase)).toBeNull();
  });
});

describe('rebuildErrorOf: ошибка пересборки готовой модели', () => {
  it('у готовой модели ошибка видна в плашке', () => {
    expect(rebuildErrorOf({ status: 'ready', corrupt: false, generateError: 'Граф кода не построился' })).toBe('Граф кода не построился');
  });
  it('у пустой и повреждённой — нет (их ведут свои экраны)', () => {
    expect(rebuildErrorOf({ status: 'missing', corrupt: false, generateError: 'x' })).toBeNull();
    expect(rebuildErrorOf({ status: 'ready', corrupt: true, generateError: 'x' })).toBeNull();
  });
});
