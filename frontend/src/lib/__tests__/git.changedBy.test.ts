import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { GitStatus, ChangedBySession, ChatItem } from '../../types';
import { computeChangedPaths } from '../../hooks/useSessionArtifacts';

// lib/git тянет realtime-обвязку (api, signalr) — глушим тяжёлые модули заглушками,
// как в notesByFile.test.ts. getGitSessionContext — отдельный именованный экспорт api.ts
// (не часть объекта api), мокается своим vi.fn() для управления гейтом worktree-контекста.
const { statusMock, changedByMock, gitSessionCtxMock } = vi.hoisted(() => ({
  statusMock: vi.fn(),
  changedByMock: vi.fn(),
  gitSessionCtxMock: vi.fn<() => { projectId: string; sessionId: string } | null>(() => null),
}));
vi.mock('../api', () => ({
  api: { git: { status: statusMock }, files: { changedBy: changedByMock } },
  getGitSessionContext: gitSessionCtxMock,
}));
vi.mock('../signalr', () => ({
  joinUser: vi.fn(), onFilesChanged: vi.fn(), onGitStatusChanged: vi.fn(), onReconnected: vi.fn(),
}));

import { loadGitStatus, setActiveSessionForChangedBy, getGitState } from '../git';

function status(paths: string[]): GitStatus {
  return {
    isRepo: true, branch: 'main', upstream: null, ahead: 0, behind: 0, detached: false,
    staged: [], unstaged: paths.map(path => ({ path, status: 'M' })), untracked: [], isWorktree: false,
  };
}

const entry = (sessionId: string, name: string): ChangedBySession => ({ sessionId, name });

let seq = 0;
const freshProjectId = () => `proj-${++seq}`;

describe('lib/git — changedBy (панель «Изменения»: чужие чаты, менявшие файл)', () => {
  beforeEach(() => {
    statusMock.mockReset();
    changedByMock.mockReset();
    gitSessionCtxMock.mockReset();
    gitSessionCtxMock.mockReturnValue(null);
  });

  it('после loadGitStatus подтягивает changedBy по путям статуса той же цепочкой и кладёт в стор', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({ files: { 'src/a.ts': [entry('s1', 'Другой чат')] } });

    await loadGitStatus(projectId);

    expect(changedByMock).toHaveBeenCalledWith(projectId, ['src/a.ts']);
    expect(getGitState(projectId).changedBy.get('src/a.ts')).toEqual([entry('s1', 'Другой чат')]);
  });

  it('пустой git status (нет изменённых файлов) — changedBy не запрашивается и пуст', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status([]));

    await loadGitStatus(projectId);

    expect(changedByMock).not.toHaveBeenCalled();
    expect(getGitState(projectId).changedBy.size).toBe(0);
  });

  // Гейт: панель смотрит git worktree активного чата (getGitSessionContext непуст для
  // этого проекта) — changedBy не грузим, индекс всё равно про корень проекта, не про дерево
  it('worktree-контекст активного чата — changedBy не запрашивается и стор гасится', async () => {
    const projectId = freshProjectId();
    gitSessionCtxMock.mockReturnValue({ projectId, sessionId: 'active-chat' });
    statusMock.mockResolvedValue(status(['src/a.ts']));

    await loadGitStatus(projectId);

    expect(changedByMock).not.toHaveBeenCalled();
    expect(getGitState(projectId).changedBy.size).toBe(0);
  });

  // Worktree-контекст ДРУГОГО проекта не должен глушить текущий — гейт per-project
  it('worktree-контекст другого проекта не мешает changedBy текущего', async () => {
    const projectId = freshProjectId();
    gitSessionCtxMock.mockReturnValue({ projectId: 'some-other-project', sessionId: 'active-chat' });
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({ files: { 'src/a.ts': [entry('s1', 'Другой чат')] } });

    await loadGitStatus(projectId);

    expect(changedByMock).toHaveBeenCalled();
  });

  // Фильтр активного чата: сам открытый чат не должен появляться в собственном списке
  // «другие чаты» — вычищается при укладке ответа в стор; файл, где остался только он,
  // из карты пропадает целиком (а не остаётся с пустым списком)
  it('setActiveSessionForChangedBy — активный чат вычищается из результата', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    statusMock.mockResolvedValue(status(['src/a.ts', 'src/b.ts']));
    changedByMock.mockResolvedValue({
      files: {
        'src/a.ts': [entry('active-chat', 'Этот чат'), entry('other-chat', 'Другой чат')],
        'src/b.ts': [entry('active-chat', 'Этот чат')],
      },
    });

    await loadGitStatus(projectId);

    const changedBy = getGitState(projectId).changedBy;
    expect(changedBy.get('src/a.ts')).toEqual([entry('other-chat', 'Другой чат')]);
    expect(changedBy.has('src/b.ts')).toBe(false);
  });

  // Переключение чата ПОСЛЕ загрузки: загрузка при активном A → переключение на B —
  // карта обязана перефильтроваться без нового сетевого запроса (сырой ответ уже в
  // памяти). Раньше фильтр применялся только в момент загрузки: до следующего git status
  // «Также меняли» показывал бы сам чат B и терял A
  it('переключение активного чата после загрузки перефильтровывает карту без нового запроса', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'chat-a');
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({ files: { 'src/a.ts': [entry('chat-a', 'Чат A'), entry('chat-b', 'Чат B')] } });

    await loadGitStatus(projectId);
    expect(getGitState(projectId).changedBy.get('src/a.ts')).toEqual([entry('chat-b', 'Чат B')]);

    changedByMock.mockClear();
    setActiveSessionForChangedBy(projectId, 'chat-b');

    expect(changedByMock).not.toHaveBeenCalled();
    const changedBy = getGitState(projectId).changedBy.get('src/a.ts');
    expect(changedBy).toEqual([entry('chat-a', 'Чат A')]);
    expect(changedBy?.some(e => e.sessionId === 'chat-b')).toBe(false);
  });

  // Без активного чата (null) фильтр не режет ничего
  it('без активного чата (null) все записи остаются', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, null);
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({ files: { 'src/a.ts': [entry('s1', 'Чат 1'), entry('s2', 'Чат 2')] } });

    await loadGitStatus(projectId);

    expect(getGitState(projectId).changedBy.get('src/a.ts')).toHaveLength(2);
  });

  it('ошибка запроса changedBy — стор гасится в пустую карту, а не падает', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockRejectedValue(new Error('network'));

    await loadGitStatus(projectId);

    expect(getGitState(projectId).changedBy.size).toBe(0);
  });
});

// --- Зеркало кейсов извлечения путей из истории чата ---
// Индекс «файл → другие чаты» (backend/…/SessionFileIndex.cs, SessionChangedPaths.Extract)
// и фронтовый computeChangedPaths (hooks/useSessionArtifacts.ts — считает файлы АКТИВНОГО
// чата для тогла «только файлы чата») решают ту же по сути задачу — обход истории,
// file_changed + tool_use из WRITE_TOOLS — но с НАМЕРЕННЫМИ расхождениями. Полные наборы
// нормализации/извлечения — в своих тестах (useSessionArtifacts.test.ts describe
// 'computeChangedPaths' и backend/ClaudeHomeServer.Tests/Services/SessionChangedPathsTests.cs,
// [общий с фронтом]/[намеренное расхождение]); здесь — только сами расхождения,
// зафиксированные вместе, чтобы будущая правка одной стороны не потеряла контраст с другой.
const ROOT = 'C:\\Sources\\MyProject';
const fileChangedItem = (path: string, external = false): ChatItem =>
  ({ kind: 'file_changed', path, added: 1, removed: 0, external });

describe('computeChangedPaths — намеренные расхождения с SessionChangedPaths.Extract (backend)', () => {
  // [намеренное расхождение] backend SessionChangedPaths.Extract исключает External=true
  // (см. Extract_FileChangedExternal_Исключается) — фронт включает: тогл «файлы чата»
  // должен показывать и вненовые правки Bash/скриптов модели за время хода
  it('external=true ВКЛЮЧАЕТСЯ (в отличие от серверного индекса)', () => {
    const set = computeChangedPaths([fileChangedItem('src/external.ts', true)], ROOT);
    expect(set.has('src/external.ts')).toBe(true);
  });

  // [намеренное расхождение] backend исключает пути хода в чужом worktree
  // (см. Extract_ХодВЧужомWorktree_ПутиИсключены) — у фронтовой ленты этого понятия нет:
  // computeChangedPaths видит только file_changed/tool_use текущего чата без разбора,
  // в каком дереве шёл конкретный ход
  it('file_changed из любого хода (в т.ч. worktree) учитывается — фронт не различает деревья', () => {
    const set = computeChangedPaths([fileChangedItem('src/in-worktree.ts')], ROOT);
    expect(set.has('src/in-worktree.ts')).toBe(true);
  });
});
