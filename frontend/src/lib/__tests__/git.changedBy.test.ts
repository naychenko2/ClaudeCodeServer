import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { GitStatus, ChangedBySession } from '../../types';

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

const entry = (sessionId: string, name: string, external = false): ChangedBySession =>
  ({ sessionId, name, external });

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

// --- myChangedPaths: пути активного чата для фильтра «только файлы чата» ---
// Источник — тот же серверный changed-by (SessionChangedPaths.Extract: история чата
// МИНУС зафиксированное в git, Session.CommittedFilePaths); фронтовый дубль по живой
// ленте (computeChangedPaths) снесён. Расхождение поверхностей фиксируется здесь:
// - changedBy (бейдж/«Также меняли») — только external=false и без активного чата;
// - myChangedPaths — все записи активного чата, ВКЛЮЧАЯ external=true
//   (Bash/скрипты модели за время хода — решение прошлой итерации фичи).
// Backend-зеркало значений External — SessionChangedPathsTests (Extract_*External*).
describe('lib/git — myChangedPaths (фильтр «только файлы чата»)', () => {
  beforeEach(() => {
    statusMock.mockReset();
    changedByMock.mockReset();
    gitSessionCtxMock.mockReset();
    gitSessionCtxMock.mockReturnValue(null);
  });

  it('пути активного чата попадают в myChangedPaths (lowercase), включая external=true', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    statusMock.mockResolvedValue(status(['SRC/A.ts', 'src/b.ts']));
    changedByMock.mockResolvedValue({
      files: {
        'SRC/A.ts': [entry('active-chat', 'Этот чат')],
        'src/b.ts': [entry('active-chat', 'Этот чат', true)], // правка Bash за время хода
      },
    });

    await loadGitStatus(projectId);

    const my = getGitState(projectId).myChangedPaths;
    expect(my).toEqual(new Set(['src/a.ts', 'src/b.ts']));
  });

  it('external=true НЕ попадает в changedBy (бейдж), но попадает в myChangedPaths активного чата', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({
      files: { 'src/a.ts': [entry('other-chat', 'Чужой чат', true), entry('active-chat', 'Этот чат', true)] },
    });

    await loadGitStatus(projectId);

    const st = getGitState(projectId);
    expect(st.changedBy.has('src/a.ts')).toBe(false);
    expect(st.myChangedPaths?.has('src/a.ts')).toBe(true);
  });

  it('чат правил файл и Edit-ом, и Bash-ом (external=false после слияния) — бейдж чужого чата остаётся', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({
      files: { 'src/a.ts': [entry('other-chat', 'Чужой чат', false)] },
    });

    await loadGitStatus(projectId);

    const st = getGitState(projectId);
    expect(st.changedBy.get('src/a.ts')).toEqual([entry('other-chat', 'Чужой чат')]);
    expect(st.myChangedPaths?.has('src/a.ts')).toBe(false);
  });

  it('без активного чата myChangedPaths === undefined (фильтр недоступен, тумблер скрыт)', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({ files: { 'src/a.ts': [entry('s1', 'Чат 1')] } });

    await loadGitStatus(projectId);

    expect(getGitState(projectId).myChangedPaths).toBeUndefined();
  });

  it('активный чат есть, но файлов чата в статусе нет — пустой Set (чат ничего не менял)', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({ files: { 'src/a.ts': [entry('other-chat', 'Чужой чат')] } });

    await loadGitStatus(projectId);

    expect(getGitState(projectId).myChangedPaths).toEqual(new Set());
  });

  it('пустой git status при активном чате — пустой Set, без чата — undefined', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    statusMock.mockResolvedValue(status([]));
    await loadGitStatus(projectId);
    expect(getGitState(projectId).myChangedPaths).toEqual(new Set());

    const projectId2 = freshProjectId();
    statusMock.mockResolvedValue(status([]));
    await loadGitStatus(projectId2);
    expect(getGitState(projectId2).myChangedPaths).toBeUndefined();
  });

  it('worktree-контекст панели — undefined (пути статуса из чужого дерева)', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    gitSessionCtxMock.mockReturnValue({ projectId, sessionId: 'active-chat' });
    statusMock.mockResolvedValue(status(['src/a.ts']));

    await loadGitStatus(projectId);

    expect(getGitState(projectId).myChangedPaths).toBeUndefined();
  });

  it('ошибка запроса changed-by — undefined (фильтр недоступен, а не пустой)', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockRejectedValue(new Error('network'));

    await loadGitStatus(projectId);

    expect(getGitState(projectId).myChangedPaths).toBeUndefined();
  });

  it('переключение активного чата перефильтровывает myChangedPaths без нового запроса', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'chat-a');
    statusMock.mockResolvedValue(status(['src/a.ts', 'src/b.ts']));
    changedByMock.mockResolvedValue({
      files: {
        'src/a.ts': [entry('chat-a', 'Чат A')],
        'src/b.ts': [entry('chat-b', 'Чат B')],
      },
    });

    await loadGitStatus(projectId);
    expect(getGitState(projectId).myChangedPaths).toEqual(new Set(['src/a.ts']));

    changedByMock.mockClear();
    setActiveSessionForChangedBy(projectId, 'chat-b');

    expect(changedByMock).not.toHaveBeenCalled();
    expect(getGitState(projectId).myChangedPaths).toEqual(new Set(['src/b.ts']));
  });
});

// --- dirtySessionIds: значок «правки чата не зафиксированы в git» в списке чатов ---
// Третья поверхность того же серверного changed-by. external=true отбрасывается, как и
// в changedBy: пока идёт ход, вотчер пишет в историю чата ЧУЖИЕ правки репы (человек в
// IDE, соседний чат) — на боевых данных это давало вчетверо больше помеченных чатов, чем
// правда. Отличие от changedBy ровно одно: активный чат не исключается. Трёхзначность —
// как у myChangedPaths, но опорой служит не наличие активного чата, а достоверность
// данных: пустой git status = знаем точно «незакоммиченного нет», недоступные данные
// (worktree-контекст, ошибка запроса) = undefined, значки не рисуются.
describe('lib/git — dirtySessionIds (значок «не зафиксировано» в списке чатов)', () => {
  beforeEach(() => {
    statusMock.mockReset();
    changedByMock.mockReset();
    gitSessionCtxMock.mockReset();
    gitSessionCtxMock.mockReturnValue(null);
  });

  it('external=true не помечает чат: вотчер пишет ему чужие правки репы за время хода', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status(['src/a.ts', 'src/b.ts']));
    changedByMock.mockResolvedValue({
      files: {
        'src/a.ts': [entry('chat-a', 'Чат A')],
        'src/b.ts': [entry('chat-b', 'Чат B', true)],
      },
    });

    await loadGitStatus(projectId);

    expect(getGitState(projectId).dirtySessionIds).toEqual(new Set(['chat-a']));
  });

  it('чат с обеими записями на файл (external и своя) помечается — false побеждает', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status(['src/a.ts', 'src/b.ts']));
    changedByMock.mockResolvedValue({
      files: {
        'src/a.ts': [entry('chat-a', 'Чат A', true)],
        'src/b.ts': [entry('chat-a', 'Чат A')],
      },
    });

    await loadGitStatus(projectId);

    expect(getGitState(projectId).dirtySessionIds).toEqual(new Set(['chat-a']));
  });

  it('активный чат попадает в dirtySessionIds — в отличие от changedBy, откуда он исключён', async () => {
    const projectId = freshProjectId();
    setActiveSessionForChangedBy(projectId, 'active-chat');
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({ files: { 'src/a.ts': [entry('active-chat', 'Этот чат')] } });

    await loadGitStatus(projectId);

    const st = getGitState(projectId);
    expect(st.dirtySessionIds).toEqual(new Set(['active-chat']));
    expect(st.changedBy.has('src/a.ts')).toBe(false);
  });

  it('чистый git status — пустой Set (достоверное «ни у кого»), а не undefined', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status([]));

    await loadGitStatus(projectId);

    expect(getGitState(projectId).dirtySessionIds).toEqual(new Set());
  });

  it('worktree-контекст активного чата — undefined: пути чужого дерева индексу не знакомы', async () => {
    const projectId = freshProjectId();
    gitSessionCtxMock.mockReturnValue({ projectId, sessionId: 'wt-chat' });
    statusMock.mockResolvedValue(status(['src/a.ts']));

    await loadGitStatus(projectId);

    expect(getGitState(projectId).dirtySessionIds).toBeUndefined();
  });

  it('ошибка запроса changed-by — undefined, значки не рисуем по недостоверным данным', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockRejectedValue(new Error('network'));

    await loadGitStatus(projectId);

    expect(getGitState(projectId).dirtySessionIds).toBeUndefined();
  });

  it('падение самого git status не гасит значки — держим последнее известное', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(status(['src/a.ts']));
    changedByMock.mockResolvedValue({ files: { 'src/a.ts': [entry('chat-a', 'Чат A')] } });
    await loadGitStatus(projectId);

    statusMock.mockRejectedValue(new Error('offline'));
    await loadGitStatus(projectId);

    expect(getGitState(projectId).dirtySessionIds).toEqual(new Set(['chat-a']));
  });
});
