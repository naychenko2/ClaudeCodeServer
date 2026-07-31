import { describe, it, expect, vi } from 'vitest';
import type { ChatItem } from '../../types';
import { computeTurnTree } from '../turnWorktree';

const started = (turnWorktree?: { path: string; name: string } | null): ChatItem =>
  ({ kind: 'session_started', model: 'claude', mode: 'default', turnWorktree });

const enterWorktree = (opts: { id?: string; result?: string; isError?: boolean; input?: unknown; parentToolUseId?: string } = {}): ChatItem =>
  ({ kind: 'tool_use', id: opts.id ?? 't1', name: 'EnterWorktree', input: opts.input ?? {}, result: opts.result, isError: opts.isError, parentToolUseId: opts.parentToolUseId });

const fileEdit = (filePath: string, opts: { tool?: string; parentToolUseId?: string } = {}): ChatItem =>
  ({ kind: 'tool_use', id: 't2', name: opts.tool ?? 'Edit', input: { file_path: filePath }, parentToolUseId: opts.parentToolUseId });

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

  it('session_started(wt-A) + более поздний EnterWorktree(wt-B) того же хода → wt-B (агент перешёл в другое дерево внутри хода)', () => {
    const items = [
      started({ path: 'C:\\repo\\.claude\\worktrees\\wt-a', name: 'wt-a' }),
      enterWorktree({ result: 'Entered worktree at C:\\repo\\.claude\\worktrees\\wt-b on branch feature/y.' }),
    ];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-b', name: 'wt-b' });
  });

  it('session_started(wt-A) + edit в wt-C → wt-A: файловый кандидат истину бэка НЕ перебивает', () => {
    const tt = { path: 'C:\\repo\\.claude\\worktrees\\wt-a', name: 'wt-a' };
    const items = [started(tt), fileEdit('C:\\repo\\.claude\\worktrees\\wt-c\\f.ts')];
    expect(computeTurnTree(items)).toEqual(tt);
  });

  it('сабагентский EnterWorktree (parentToolUseId) исключается из фолбэка', () => {
    const items = [
      started(null),
      enterWorktree({ result: 'Entered worktree at C:\\repo\\.claude\\worktrees\\wt-sub on branch feature/x.', parentToolUseId: 'parent-1' }),
    ];
    expect(computeTurnTree(items)).toBeNull();
  });

  it('лента вообще без session_started доходит до финального return: EnterWorktree', () => {
    const items = [enterWorktree({ result: 'Entered worktree at C:\\repo\\.claude\\worktrees\\wt-lonely on branch feature/x.' })];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-lonely', name: 'wt-lonely' });
  });

  it('лента вообще без session_started: только эвристика по файлам', () => {
    const items = [fileEdit('C:\\repo\\.claude\\worktrees\\wt-f\\a.ts')];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-f', name: 'wt-f' });
  });

  it('console.warn: однократно на нераспознанный результат, молчит на повторе и на пустой строке', () => {
    const spy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    try {
      const items = [started(null), enterWorktree({ id: 'tw-bad', result: 'Something unexpected happened' })];
      computeTurnTree(items);
      computeTurnTree(items); // повторный пересчёт (как на text_delta) — повторного warn нет
      expect(spy).toHaveBeenCalledTimes(1);
      computeTurnTree([started(null), enterWorktree({ id: 'tw-empty', result: '' })]);
      expect(spy).toHaveBeenCalledTimes(1);
    } finally {
      spy.mockRestore();
    }
  });
});

describe('эвристика по путям изменённых файлов (третий приоритет)', () => {
  it('Edit с file_path в .claude/worktrees/<имя>/ после session_started(null) — уход через Bash', () => {
    const items = [
      started(null),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-x\\src\\a.ts'),
    ];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-x', name: 'wt-x' });
  });

  it('прямые слэши: C:/repo/.claude/worktrees/wt-x/src/a.ts', () => {
    const items = [started(null), fileEdit('C:/repo/.claude/worktrees/wt-x/src/a.ts')];
    expect(computeTurnTree(items)).toEqual({ path: 'C:/repo/.claude/worktrees/wt-x', name: 'wt-x' });
  });

  it('смешанные разделители', () => {
    const items = [started(null), fileEdit('C:\\repo/.claude\\worktrees/wt-x\\a.ts')];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo/.claude\\worktrees/wt-x', name: 'wt-x' });
  });

  it('Write и NotebookEdit тоже матчатся', () => {
    expect(computeTurnTree([started(null), fileEdit('/home/u/repo/.claude/worktrees/wt-w/a.md', { tool: 'Write' })]))
      .toEqual({ path: '/home/u/repo/.claude/worktrees/wt-w', name: 'wt-w' });
    expect(computeTurnTree([started(null), fileEdit('C:\\repo\\.claude\\worktrees\\wt-n\\n.ipynb', { tool: 'NotebookEdit' })]))
      .toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-n', name: 'wt-n' });
  });

  it('имя дерева с дефисами и точками извлекается целиком', () => {
    const items = [started(null), fileEdit('C:\\repo\\.claude\\worktrees\\feat-x.1\\src\\a.ts')];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\feat-x.1', name: 'feat-x.1' });
  });

  it('session_started с turnWorktree сильнее эвристики (истина бэка)', () => {
    const tt = { path: 'C:\\repo\\.claude\\worktrees\\wt-server', name: 'wt-server' };
    const items = [
      started(tt),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-other\\a.ts'),
    ];
    expect(computeTurnTree(items)).toEqual(tt);
  });

  it('распарсенный EnterWorktree сильнее эвристики, даже если правки файлов идут позже в ленте', () => {
    const items = [
      started(null),
      enterWorktree({ result: 'Entered worktree at C:\\repo\\.claude\\worktrees\\wt-real on branch feature/x.' }),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-real\\src\\a.ts'),
    ];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-real', name: 'wt-real' });
  });

  it('EnterWorktree сильнее эвристики даже при РАЗНЫХ путях (эвристика ниже по рангу)', () => {
    const items = [
      started(null),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-file\\a.ts'),
      enterWorktree({ result: 'Entered worktree at C:\\repo\\.claude\\worktrees\\wt-tool on branch feature/x.' }),
    ];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-tool', name: 'wt-tool' });
  });

  it('сабагентский Edit (parentToolUseId) игнорируется — своё изолированное дерево', () => {
    const items = [
      started(null),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-sub\\a.ts', { parentToolUseId: 'parent-1' }),
    ];
    expect(computeTurnTree(items)).toBeNull();
  });

  it('пути вне паттерна .claude/worktrees/<имя>/ не матчатся', () => {
    const outside = [
      'D:\\other\\project\\file.ts',            // чужой проект / скретчпад
      'C:\\repo\\worktrees\\wt\\a.ts',           // без .claude
      'C:\\repo\\.claude\\other\\a.ts',          // .claude, но не worktrees
      'C:\\repo\\.claude\\worktrees\\wt-x',      // путь К дереву, не файл в нём
    ];
    for (const p of outside) {
      expect(computeTurnTree([started(null), fileEdit(p)])).toBeNull();
    }
  });

  it('не-файловые инструменты (Bash с путём дерева в команде) не матчатся', () => {
    const bash: ChatItem = { kind: 'tool_use', id: 't3', name: 'Bash', input: { command: 'git worktree add C:\\repo\\.claude\\worktrees\\wt-b' } };
    expect(computeTurnTree([started(null), bash])).toBeNull();
  });

  it('следующий session_started(null) сбрасывает эвристику (ход вернулся в проект)', () => {
    const items = [
      started(null),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-x\\a.ts'),
      started(null),
    ];
    expect(computeTurnTree(items)).toBeNull();
  });

  it('эвристика прошлого хода не течёт в новый: session_started обрывает накопление', () => {
    const items = [
      started(null),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-x\\a.ts'),
      started(null),
      fileEdit('C:\\repo\\src\\b.ts'), // текущий ход правит проект
    ];
    expect(computeTurnTree(items)).toBeNull();
  });

  it('session_started с turnWorktree после эвристики перекрывает её', () => {
    const tt = { path: 'C:\\repo\\.claude\\worktrees\\wt-y', name: 'wt-y' };
    const items = [
      started(null),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-x\\a.ts'),
      started(tt),
    ];
    expect(computeTurnTree(items)).toEqual(tt);
  });

  it('берётся самый свежий (ближний к концу ленты) файл в дереве', () => {
    const items = [
      started(null),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-old\\a.ts'),
      fileEdit('C:\\repo\\.claude\\worktrees\\wt-new\\b.ts'),
    ];
    expect(computeTurnTree(items)).toEqual({ path: 'C:\\repo\\.claude\\worktrees\\wt-new', name: 'wt-new' });
  });
});
