import { describe, it, expect } from 'vitest';
import type { ChatItem } from '../../types';
import { computeTurnTree } from '../turnWorktree';

const started = (turnWorktree?: { path: string; name: string } | null): ChatItem =>
  ({ kind: 'session_started', model: 'claude', mode: 'default', turnWorktree });

const enterWorktree = (opts: { result?: string; isError?: boolean; input?: unknown } = {}): ChatItem =>
  ({ kind: 'tool_use', id: 't1', name: 'EnterWorktree', input: opts.input ?? {}, result: opts.result, isError: opts.isError });

describe('computeTurnTree', () => {
  it('пустая лента → null', () => {
    expect(computeTurnTree([])).toBeNull();
  });

  it('последний session_started без turnWorktree → null (ход в проекте)', () => {
    expect(computeTurnTree([started(null)])).toBeNull();
  });

  it('последний session_started с turnWorktree → берём его', () => {
    const tt = { path: 'C:\\repo\\.claude\\worktrees\\wt-a7c1e2', name: 'wt-a7c1e2' };
    expect(computeTurnTree([started(tt)])).toEqual(tt);
  });

  it('первый ход, режим «вход в существующее дерево» (path): результат «Entered worktree at …»', () => {
    const items = [
      started(null),
      enterWorktree({ result: 'Entered worktree at C:\\repo\\.claude\\worktrees\\wt-a7c1e2 on branch feature/x. The session is now working in the worktree.' }),
    ];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-a7c1e2', name: 'wt-a7c1e2' });
  });

  it('первый ход, режим «создание нового дерева» (name): результат «Created worktree at …» — реальный текст из живой проверки QA', () => {
    const items = [
      started(null),
      enterWorktree({
        result: 'Created worktree at C:\\Sources\\ClaudeCodeServer\\.claude\\worktrees\\probe-cwd\n'
          + 'on branch worktree-probe-cwd. The session is now working in the worktree.\n'
          + 'Use ExitWorktree to leave mid-session, or exit the session to be prompted.',
      }),
    ];
    expect(computeTurnTree(items)).toEqual({
      path: 'C:\\Sources\\ClaudeCodeServer\\.claude\\worktrees\\probe-cwd',
      name: 'probe-cwd',
    });
  });

  it('EnterWorktree без результата (ещё в процессе) — фолбэк на input.path', () => {
    const items = [
      started(null),
      enterWorktree({ input: { path: 'C:\\repo\\.claude\\worktrees\\wt-manual' } }),
    ];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-manual', name: 'wt-manual' });
  });

  it('EnterWorktree без результата и без input.path — данных нет, null', () => {
    const items = [started(null), enterWorktree()];
    expect(computeTurnTree(items)).toBeNull();
  });

  it('результат ПРИШЁЛ, но не распознан регэкспом — null, а не запрошенный input.path (реальный путь мог отличаться, напр. коллизия имени)', () => {
    const items = [
      started(null),
      enterWorktree({ result: 'Something unexpected happened', input: { path: 'C:\\repo\\.claude\\worktrees\\wt-requested' } }),
    ];
    expect(computeTurnTree(items)).toBeNull();
  });

  it('пустая строка результата (уже пришёл, просто без текста) — тоже не фолбэчится на input.path', () => {
    const items = [
      started(null),
      enterWorktree({ result: '', input: { path: 'C:\\repo\\.claude\\worktrees\\wt-requested' } }),
    ];
    expect(computeTurnTree(items)).toBeNull();
  });

  it('ошибочный EnterWorktree игнорируется', () => {
    const items = [
      started(null),
      enterWorktree({ result: 'Already in a worktree session', isError: true }),
    ];
    expect(computeTurnTree(items)).toBeNull();
  });

  it('следующий session_started со свежим turnWorktree перекрывает старый EnterWorktree', () => {
    const items = [
      enterWorktree({ result: 'Entered worktree at C:\\repo\\.claude\\worktrees\\wt-old on branch feature/x.' }),
      started({ path: 'C:\\repo\\.claude\\worktrees\\wt-old', name: 'wt-old' }),
      started(null), // ход вернулся в проект
    ];
    expect(computeTurnTree(items)).toBeNull();
  });
});
