// Панель «Изменения» рельсы проекта (workspace-cc-panels) — Source Control.
// Две зоны: вверху файлы активного скоупа (текущие изменения ИЛИ выбранный коммит),
// внизу селектор скоупов (строка «Не зафиксировано» + стек незапушенных коммитов).
// Данные/мутации — через стор lib/git.ts; форма фиксации и диалог промпта — локально.
// Отдельный компонент (панель «Файлы»/GitChangesPanel не трогаем).

import { useEffect, useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import {
  GitBranch, GitCommit, ChevronDown, ChevronRight, RefreshCw, ArrowDownToLine,
  Settings, Sparkles, Undo2, Pencil, CloudUpload, X, List, ListTree, Wand2, FolderClosed,
  ListChecks, CheckCheck, FoldVertical, UnfoldVertical, MessageSquarePlus, MessageSquare,
  Check, Plus, Archive, ArchiveRestore, Trash2,
} from 'lucide-react';
import type { Project, GitFileChange, GitLogEntry, GitStashEntry } from '../types';
import { api } from '../lib/api';
import { C, R, FONT, MODAL_W } from '../lib/design';
import {
  useGitState, ensureGit, loadUnpushedLog, loadGitLog, loadGitRemote, loadGitBranches, loadGitStash,
  gitStage, gitUnstage, gitDiscard, gitDiscardAll, gitCommit, gitPush, gitFetch, gitPull, gitCheckout, gitCreateBranch,
  gitStashPush, gitStashPop, gitStashDrop, clearGitError,
} from '../lib/git';
import { splitPath, relTime } from './GitPanel';
import { getExtMeta } from './FileExplorer';
import { Modal, ModalActions, TextArea, TextField, IconButton, Button, Menu, MenuItem } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

const COMMIT_SUMMARY_MAX = 72;
const VIEW_KEY = 'cc_git_changes_view';
const SCOPE_H_KEY = 'cc_git_scope_h';   // высота стека коммитов зоны скоупов (ресайз)
const SCOPE_H_DEFAULT = 4 * 34;         // по умолчанию видно ~4 скоупа

// Светлая компактная кнопка делегирования фиксации чату (в форме)
const chatBtnStyle: CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 4, height: 32, padding: '0 9px',
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
  cursor: 'pointer', color: C.textSecondary, fontSize: 11.5, fontFamily: FONT.sans, whiteSpace: 'nowrap',
};

interface Props {
  project: Project;
  onOpenDiff: (path: string, staged: boolean) => void;
  onOpenFile: (path: string) => void;
  onOpenCommit: (sha: string, filePath?: string) => void;
  activeFilePath?: string | null;  // подсветка открытого файла в дереве/списке
  activeCommitSha?: string | null; // подсветка открытого коммита в истории ветки
  onCommit?: (where: 'chat' | 'newChat') => void;  // делегировать фиксацию чату / новому чату
  onScopeChange?: () => void;  // сменили скоуп/коммит — центральную область сбросить к чату
}

// Строка файла активного скоупа после объединения групп статуса
interface RowFile {
  path: string;
  status: string;   // односимвольный код git (M/A/D/R/?)
  staged: boolean;  // есть ли в индексе (для приведения индекса при выборочном коммите)
  added: number | null;
  deleted: number | null;
}

// Цвет имени файла по статусу (вместо квадратного бейджа — как просил дизайн)
function nameColor(status: string): string {
  switch (status) {
    case 'A': case '?': return C.successText;
    case 'D': return C.danger;
    case 'R': return C.info;
    default: return C.textHeading;
  }
}

// Объединить staged+unstaged+untracked в единый набор по пути (файл может быть в двух группах)
function mergeWorking(staged: GitFileChange[], unstaged: GitFileChange[], untracked: GitFileChange[]): RowFile[] {
  const map = new Map<string, RowFile>();
  const stat = (f: GitFileChange) => ({ added: f.added ?? null, deleted: f.deleted ?? null });
  for (const f of staged)
    map.set(f.path, { path: f.path, status: f.status, staged: true, ...stat(f) });
  for (const f of unstaged) {
    const ex = map.get(f.path);
    if (ex) { ex.status = f.status; ex.added ??= f.added ?? null; ex.deleted ??= f.deleted ?? null; }
    else map.set(f.path, { path: f.path, status: f.status, staged: false, ...stat(f) });
  }
  for (const f of untracked)
    if (!map.has(f.path)) map.set(f.path, { path: f.path, status: '?', staged: false, ...stat(f) });
  return [...map.values()].sort((a, b) => a.path.localeCompare(b.path));
}

// Узел дерева: папка (children) или файл (file)
interface TreeNode {
  name: string;
  path: string;       // для папки — путь папки, для файла — путь файла
  file?: RowFile;
  children: TreeNode[];
}

// Собрать дерево из плоского списка файлов
function buildTree(files: RowFile[]): TreeNode[] {
  const root: TreeNode = { name: '', path: '', children: [] };
  for (const f of files) {
    const parts = f.path.replace(/\\/g, '/').split('/');
    let node = root;
    for (let i = 0; i < parts.length; i++) {
      const isLeaf = i === parts.length - 1;
      const seg = parts[i];
      const segPath = parts.slice(0, i + 1).join('/');
      if (isLeaf) {
        node.children.push({ name: seg, path: f.path, file: f, children: [] });
      } else {
        let child = node.children.find(c => !c.file && c.name === seg);
        if (!child) { child = { name: seg, path: segPath, children: [] }; node.children.push(child); }
        node = child;
      }
    }
  }
  // Схлопнуть цепочки папок с единственным ребёнком-папкой (a/b/c → «a/b/c»)
  const collapse = (nodes: TreeNode[]): TreeNode[] => nodes.map(n => {
    if (n.file) return n;
    let cur = n;
    while (cur.children.length === 1 && !cur.children[0].file) {
      const only = cur.children[0];
      cur = { name: `${cur.name}/${only.name}`, path: only.path, children: only.children };
    }
    return { ...cur, children: collapse(cur.children) };
  });
  const sortRec = (nodes: TreeNode[]): TreeNode[] =>
    nodes.map(n => ({ ...n, children: sortRec(n.children) }))
      .sort((a, b) => (a.file ? 1 : 0) - (b.file ? 1 : 0) || a.name.localeCompare(b.name));
  return sortRec(collapse(root.children));
}

export function GitChangesRail({ project, onOpenDiff, onOpenFile, onOpenCommit, activeFilePath, activeCommitSha, onCommit, onScopeChange }: Props) {
  const st = useGitState(project.id);
  const status = st.status;

  const [activeScope, setActiveScope] = useState<'working' | string>('working'); // string = sha коммита
  const [mode, setMode] = useState<'list' | 'commit'>('list');
  const [viewMode, setViewMode] = useState<'list' | 'tree'>(
    () => (localStorage.getItem(VIEW_KEY) === 'list' ? 'list' : 'tree'));
  const [collapsedDirs, setCollapsedDirs] = useState<Set<string>>(new Set());
  const [selectMode, setSelectMode] = useState(false);                 // режим выбора файлов (чекбоксы)
  const [unchecked, setUnchecked] = useState<Set<string>>(new Set()); // снятые файлы (в режиме выбора)
  const [summary, setSummary] = useState('');
  const [aiBusy, setAiBusy] = useState(false);
  const [discardPath, setDiscardPath] = useState<string | null>(null);
  const [discardAllConfirm, setDiscardAllConfirm] = useState(false);  // отмена всех изменений
  const [hoveredRow, setHoveredRow] = useState<string | null>(null);
  const [commitFiles, setCommitFiles] = useState<GitFileChange[]>([]);
  const [promptOpen, setPromptOpen] = useState(false);
  const [publishConfirm, setPublishConfirm] = useState(false);   // подтверждение публикации (push)
  const [scopeH, setScopeH] = useState<number>(() => {           // высота зоны скоупов (ресайз)
    try { const n = Number(localStorage.getItem(SCOPE_H_KEY)); return Number.isFinite(n) && n >= 60 ? n : SCOPE_H_DEFAULT; }
    catch { return SCOPE_H_DEFAULT; }
  });
  const [branchMenu, setBranchMenu] = useState(false);           // меню выбора ветки
  const [newBranchOpen, setNewBranchOpen] = useState(false);     // диалог новой ветки
  const [newBranchName, setNewBranchName] = useState('');
  const [pendingCheckout, setPendingCheckout] = useState<string | null>(null); // ветка, ждущая подтверждения (грязное дерево)
  const [stashDropConfirm, setStashDropConfirm] = useState<GitStashEntry | null>(null); // стэш, ждущий подтверждения удаления

  // Загрузка стора + стека незапушенных + remote при монтировании панели
  useEffect(() => {
    ensureGit(project.id);
    void loadUnpushedLog(project.id);
    void loadGitRemote(project.id);
    void loadGitStash(project.id);
  }, [project.id]);

  // Git-бар чата просит показать текущие изменения: сбрасываем скоуп на «Не
  // зафиксировано» (если панель была открыта на коммите/стэше/ветке). При закрытой
  // панели событие безвредно теряется — при монтировании скоуп и так 'working'.
  useEffect(() => {
    const onOpenWorking = () => { setActiveScope('working'); setMode('list'); };
    window.addEventListener('cc-git-open-working', onOpenWorking);
    return () => window.removeEventListener('cc-git-open-working', onOpenWorking);
  }, []);

  const setView = (v: 'list' | 'tree') => { setViewMode(v); try { localStorage.setItem(VIEW_KEY, v); } catch { /* квота */ } };
  const toggleDir = (p: string) =>
    setCollapsedDirs(prev => { const n = new Set(prev); n.has(p) ? n.delete(p) : n.add(p); return n; });

  const workingFiles = useMemo(
    () => status ? mergeWorking(status.staged, status.unstaged, status.untracked) : [],
    [status]);

  // Полная история ветки — грузим лениво при выборе скоупа ветки (потом держится
  // свежей через realtime: onGitStatusChanged перечитывает лог, если он был загружен)
  useEffect(() => {
    if (activeScope === 'branch') void loadGitLog(project.id);
  }, [activeScope, project.id]);

  // Скоуп ветки показывает уже опубликованные коммиты: незапушенные живут в нижнем
  // списке отдельными скоупами — не дублируем их здесь
  const pushedCommits = useMemo(() => {
    const un = new Set(st.unpushed.map(c => c.sha));
    return st.log.filter(c => !un.has(c.sha));
  }, [st.log, st.unpushed]);

  // Файлы выбранного скоупа (read-only) — коммит или стэш; грузим при активации
  useEffect(() => {
    if (activeScope === 'working' || activeScope === 'branch') { setCommitFiles([]); return; }
    let alive = true;
    const stashIdx = activeScope.startsWith('stash:') ? Number(activeScope.slice(6)) : null;
    const load = stashIdx != null
      ? api.git.stashShow(project.id, stashIdx)
      : api.git.commitDetail(project.id, activeScope);
    void load
      .then(d => { if (alive) setCommitFiles(d.files); })
      .catch(() => { if (alive) setCommitFiles([]); });
    return () => { alive = false; };
  }, [activeScope, project.id]);

  const selectScope = (scope: 'working' | string) => {
    if (scope !== activeScope) onScopeChange?.();  // смена скоупа — сбросить центр к чату
    setActiveScope(scope);
    setMode('list');
  };

  // Ветки: список грузим лениво при открытии меню, чекаут — только на другую ветку
  const openBranchMenu = () => { if (!branchMenu) void loadGitBranches(project.id); setBranchMenu(v => !v); };
  const doCheckout = (name: string) => { onScopeChange?.(); void gitCheckout(project.id, name); };
  const handleCheckout = (name: string) => {
    setBranchMenu(false);
    if (name === status?.branch) return;
    // Грязное дерево — сначала спросим (git иначе перенесёт правки или откажет при конфликте)
    if (workingFiles.length > 0) { setPendingCheckout(name); return; }
    doCheckout(name);
  };
  const handleCreateBranch = async () => {
    const name = newBranchName.trim();
    if (!name) return;
    setNewBranchOpen(false); setNewBranchName('');
    await gitCreateBranch(project.id, name);
  };

  // Ресайз высоты зоны скоупов: тянем хендл вверх — стек коммитов выше, файлы ниже
  const handleScopeResize = (e: React.PointerEvent) => {
    e.preventDefault();
    const startY = e.clientY;
    const startH = scopeH;
    let latest = startH;
    const onMove = (ev: PointerEvent) => { latest = Math.max(60, Math.min(600, startH - (ev.clientY - startY))); setScopeH(latest); };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      try { localStorage.setItem(SCOPE_H_KEY, String(Math.round(latest))); } catch { /* квота */ }
    };
    document.body.style.cursor = 'row-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

  // Файлы активного скоупа как RowFile (для коммита — из commitFiles, чекбоксов нет)
  const commitRows: RowFile[] = useMemo(
    () => commitFiles.map(f => ({ path: f.path, status: f.status, staged: false, added: f.added ?? null, deleted: f.deleted ?? null })),
    [commitFiles]);
  const isWorking = activeScope === 'working';
  const isBranch = activeScope === 'branch';
  const isStash = activeScope.startsWith('stash:');
  const rows = isWorking ? workingFiles : commitRows;
  const tree = useMemo(() => buildTree(rows), [rows]);

  // Глубины всех папок дерева — для кнопок «свернуть/развернуть на уровень»
  const dirDepths = useMemo(() => {
    const m = new Map<string, number>();
    const walk = (nodes: TreeNode[], d: number) => nodes.forEach(n => { if (!n.file) { m.set(n.path, d); walk(n.children, d + 1); } });
    walk(tree, 0);
    return m;
  }, [tree]);
  // Свернуть самый глубокий раскрытый уровень папок (каждое нажатие — на 1 уровень внутрь)
  const collapseOne = () => {
    let maxOpen = -1;
    for (const [p, d] of dirDepths) if (!collapsedDirs.has(p) && d > maxOpen) maxOpen = d;
    if (maxOpen < 0) return;
    setCollapsedDirs(prev => { const n = new Set(prev); for (const [p, d] of dirDepths) if (d === maxOpen) n.add(p); return n; });
  };
  // Развернуть самый мелкий свёрнутый уровень (каждое нажатие — на 1 уровень наружу)
  const expandOne = () => {
    let minClosed = Infinity;
    for (const [p, d] of dirDepths) if (collapsedDirs.has(p) && d < minClosed) minClosed = d;
    if (!isFinite(minClosed)) return;
    setCollapsedDirs(prev => { const n = new Set(prev); for (const [p, d] of dirDepths) if (d === minClosed) n.delete(p); return n; });
  };

  // Выбор в коммит: вне режима выбора — все файлы; в режиме — все кроме снятых
  const isSelected = (path: string) => !selectMode || !unchecked.has(path);
  const selectedCount = selectMode ? workingFiles.filter(f => !unchecked.has(f.path)).length : workingFiles.length;
  const allSelected = workingFiles.length > 0 && workingFiles.every(f => !unchecked.has(f.path));
  const toggleAll = () => setUnchecked(allSelected ? new Set(workingFiles.map(f => f.path)) : new Set());
  const canCommit = selectedCount > 0 && !!summary.trim() && !st.busy;

  // Высота поля фиксации ≈ фактическая высота списка скоупов (сколько их реально),
  // ограниченная заданной scopeH, но не меньше дозволенного минимума (76 ≈ 2 строки).
  // Так форма замещает список тем же размером, а не распахивается на весь scopeH.
  const scopeRowsCount = (workingFiles.length > 0 ? 1 : 0) + st.stashes.length + st.unpushed.length;
  const commitBodyH = Math.max(Math.min(scopeRowsCount * 32, scopeH), 76);

  // ✨ AI-описание по staged-диффу с кастомным промптом (проектный/глобальный)
  const handleAi = async () => {
    setAiBusy(true);
    try { setSummary((await api.git.aiCommitMessage(project.id)).summary.slice(0, COMMIT_SUMMARY_MAX)); }
    catch { /* нет staged/таймаут — поле как есть */ }
    finally { setAiBusy(false); }
  };

  // Привести git-индекс к текущему выбору (выбранные → stage, снятые → unstage)
  const syncIndexToSelection = async () => {
    for (const f of workingFiles) {
      if (isSelected(f.path)) { if (!f.staged) await gitStage(project.id, f.path); }
      else if (f.staged) await gitUnstage(project.id, f.path);
    }
  };

  // Открыть форму: показать сверху фиксируемые файлы (а не историю ветки/чужой скоуп)
  // и заранее проиндексировать выбранные, чтобы ✨ видела дифф индекса
  const openCommitForm = async () => {
    setActiveScope('working');
    setMode('commit');
    await syncIndexToSelection();
  };

  // Зафиксировать: индекс уже приведён к выбору — сверяем ещё раз и коммитим
  const doCommit = async () => {
    const msg = summary.trim();
    if (!msg || selectedCount === 0) return;
    await syncIndexToSelection();
    const ok = await gitCommit(project.id, msg, false);
    if (ok) { setSummary(''); setUnchecked(new Set()); setSelectMode(false); setMode('list'); void loadUnpushedLog(project.id); }
  };

  const toggleCheck = (path: string) =>
    setUnchecked(prev => { const n = new Set(prev); n.has(path) ? n.delete(path) : n.add(path); return n; });

  const ahead = status?.ahead ?? 0;
  // Кнопку «Опубликовать» показываем при незапушенных коммитах ИЛИ непустом стеке
  // (upstream мог не резолвиться, но локальные коммиты видны) — push сам разберётся с remote
  const canPublish = ahead > 0 || st.unpushed.length > 0;

  // === Рендер строки файла (дерево и список используют один рендер) ===
  const renderFileRow = (f: RowFile, depth: number, showParent: boolean) => {
    const [parent, name] = splitPath(f.path);
    const rowKey = `${activeScope}:${f.path}`;
    const hovered = hoveredRow === rowKey;
    const isActiveFile = !!activeFilePath && f.path === activeFilePath;
    const open = () => {
      // Файл стэша → просто открыть файл (вью diff у стэша нет)
      if (isStash) { onOpenFile(f.path); return; }
      // Файл коммита → открыть просмотр коммита сразу на diff этого файла
      if (!isWorking) { onOpenCommit(activeScope, f.path); return; }
      if (f.status === '?') onOpenFile(f.path); else onOpenDiff(f.path, f.staged);
    };
    return (
      <div
        key={rowKey}
        onClick={open}
        onMouseEnter={() => setHoveredRow(rowKey)}
        onMouseLeave={() => setHoveredRow(null)}
        style={{
          // Список (showParent) — двухстрочная строка (имя + путь): даём высоту и
          // вертикальные отступы, чтобы не было тесно. Дерево — одна строка, как было.
          display: 'flex', alignItems: 'center', gap: 7, position: 'relative',
          minHeight: showParent ? 42 : 30, padding: showParent ? '6px 6px' : '0 6px', paddingLeft: 8 + depth * 14,
          borderRadius: 8, cursor: 'pointer',
          background: isActiveFile ? C.accentLight : hovered ? C.bgSelected : 'transparent', transition: 'background 0.1s',
        }}
      >
        {isWorking && selectMode && (
          <span
            onClick={e => { e.stopPropagation(); toggleCheck(f.path); }}
            title={unchecked.has(f.path) ? 'Включить в коммит' : 'Исключить из коммита'}
            style={{
              width: 15, height: 15, borderRadius: 4, flexShrink: 0, cursor: 'pointer',
              border: `1.5px solid ${unchecked.has(f.path) ? C.textMuted : C.accent}`,
              background: unchecked.has(f.path) ? 'transparent' : C.accent,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}
          >
            {!unchecked.has(f.path) && <span style={{ color: C.onAccent, fontSize: 10, fontWeight: 700, lineHeight: 1 }}>✓</span>}
          </span>
        )}
        {/* Без чекбоксов — тег расширения (как в панели «Файлы») */}
        {!(isWorking && selectMode) && (() => {
          const em = getExtMeta(name);
          return (
            <span style={{
              width: 20, height: 20, borderRadius: 5, flexShrink: 0,
              background: em.bg, color: em.fg, fontFamily: FONT.mono, fontSize: 8, fontWeight: 700,
              display: 'flex', alignItems: 'center', justifyContent: 'center', letterSpacing: '-0.02em',
            }}>{em.label}</span>
          );
        })()}
        {/* Имя файла — статус кодируется цветом */}
        <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
          <span title={f.path} style={{
            fontFamily: FONT.mono, fontSize: 12.5, fontWeight: 500, color: nameColor(f.status),
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>{name}</span>
          {showParent && parent && (
            <span title={f.path} style={{
              fontFamily: FONT.mono, fontSize: 10, color: C.textMuted,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}>{parent}</span>
          )}
        </span>
        {/* numstat +N/−M — скрываем под кнопкой отмены при ховере (та absolute поверх) */}
        {(f.added != null || f.deleted != null) && (
          <span style={{ display: 'flex', gap: 5, flexShrink: 0, fontFamily: FONT.mono, fontSize: 10.5, opacity: isWorking && hovered ? 0 : 1 }}>
            {f.added != null && f.added > 0 && <span style={{ color: C.diffAddText }}>+{f.added}</span>}
            {f.deleted != null && f.deleted > 0 && <span style={{ color: C.diffRemText }}>−{f.deleted}</span>}
          </span>
        )}
        {/* Откат — absolute (не занимает место в покое), появляется по ховеру поверх numstat */}
        {isWorking && hovered && !st.busy && (
          <IconButton size="xs" tone="danger" color={C.danger} title="Отменить изменения"
            style={{ position: 'absolute', right: 4, top: '50%', transform: 'translateY(-50%)' }}
            onClick={e => { e.stopPropagation(); setDiscardPath(f.path); }}>
            <Undo2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
      </div>
    );
  };

  // Строка коммита истории ветки (скоуп «ветка») — клик открывает экран коммита в центре
  const renderCommitRow = (c: GitLogEntry) => {
    const active = !!activeCommitSha && c.sha === activeCommitSha;
    const rowKey = `log:${c.sha}`;
    const hovered = hoveredRow === rowKey;
    return (
      <div
        key={c.sha}
        onClick={() => onOpenCommit(c.sha)}
        onMouseEnter={() => setHoveredRow(rowKey)}
        onMouseLeave={() => setHoveredRow(null)}
        style={{
          display: 'flex', alignItems: 'center', gap: 7, minHeight: 30, padding: '4px 8px',
          borderRadius: 8, cursor: 'pointer', background: active ? C.accentLight : hovered ? C.bgSelected : 'transparent',
        }}
      >
        <GitCommit size={13} strokeWidth={ICON_STROKE} color={active ? C.accent : C.textSecondary} style={{ flexShrink: 0 }} />
        <span title={c.subject} style={{ flex: 1, minWidth: 0, fontSize: 12, color: active ? C.accent : C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{c.subject}</span>
        <span title={relTime(c.date)} style={{ fontFamily: FONT.mono, fontSize: 10, color: active ? C.accent : C.textMuted, flexShrink: 0 }}>{c.shortSha}</span>
      </div>
    );
  };

  // Рекурсивный рендер дерева
  const renderTree = (nodes: TreeNode[], depth: number): React.ReactNode =>
    nodes.map(n => {
      if (n.file) return renderFileRow(n.file, depth, false);
      const collapsed = collapsedDirs.has(n.path);
      return (
        <div key={`d:${n.path}`}>
          <div
            onClick={() => toggleDir(n.path)}
            style={{
              display: 'flex', alignItems: 'center', gap: 5, minHeight: 26, cursor: 'pointer',
              padding: '3px 6px', paddingLeft: 8 + depth * 14, userSelect: 'none',
            }}
          >
            {collapsed ? <ChevronRight size={13} color={C.textMuted} /> : <ChevronDown size={13} color={C.textMuted} />}
            <FolderClosed size={14} color={C.textSecondary} strokeWidth={ICON_STROKE} />
            <span style={{ fontFamily: FONT.mono, fontSize: 11.5, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{n.name}</span>
          </div>
          {!collapsed && renderTree(n.children, depth + 1)}
        </div>
      );
    });

  const scopeLabel = isWorking ? 'не зафиксировано'
    : isStash ? (st.stashes.find(s => `stash:${s.index}` === activeScope)?.message || 'отложенное')
    : (st.unpushed.find(c => c.sha === activeScope)?.subject ?? 'коммит');

  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      {st.error && (
        <div onClick={() => clearGitError(project.id)} title="Скрыть"
          style={{ margin: '8px 12px', fontSize: 12, color: C.dangerText, fontFamily: FONT.sans, lineHeight: 1.4, cursor: 'pointer' }}>
          {st.error}
        </div>
      )}

      {/* === Верхняя зона: файлы активного скоупа (видны всегда, даже при фиксации) === */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        {/* Шапка зоны файлов: подпись скоупа + выбор + свернуть/развернуть + toggle список/дерево */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 4, padding: '10px 10px 6px' }}>
          <span style={{ flex: 1, fontSize: 10, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.04em', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {isBranch ? `Коммиты · ${status?.branch ?? '—'}` : `Файлы · ${scopeLabel}`}
          </span>
          {/* Управление списком файлов — только для файловых скоупов (не для истории ветки) */}
          {!isBranch && (
          <>
          {/* Режим выбора файлов (чекбоксы) — только для текущих изменений */}
          {isWorking && rows.length > 0 && (
            <>
              <IconButton size="sm" title={selectMode ? 'Скрыть выбор файлов' : 'Выбрать файлы для коммита'}
                active={selectMode} onClick={() => setSelectMode(v => !v)}>
                <ListChecks size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
              {selectMode && (
                <IconButton size="sm" title={allSelected ? 'Снять все' : 'Выбрать все'}
                  tone="accent" onClick={toggleAll}>
                  {allSelected
                    ? <span style={{ fontSize: 13, fontWeight: 700 }}>—</span>
                    : <CheckCheck size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                </IconButton>
              )}
            </>
          )}
          {/* Свернуть/развернуть на уровень — только в дереве */}
          {viewMode === 'tree' && dirDepths.size > 0 && (
            <>
              <IconButton size="sm" title="Свернуть на уровень" onClick={collapseOne}>
                <FoldVertical size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
              <IconButton size="sm" title="Развернуть на уровень" onClick={expandOne}>
                <UnfoldVertical size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            </>
          )}
          <div style={{ display: 'flex', border: `1px solid ${C.border}`, borderRadius: R.md, overflow: 'hidden', flexShrink: 0 }}>
            <button onClick={() => setView('list')} title="Списком"
              style={{ display: 'flex', padding: '3px 7px', border: 'none', cursor: 'pointer', background: viewMode === 'list' ? C.accentLight : 'transparent' }}>
              <List size={14} strokeWidth={ICON_STROKE} color={viewMode === 'list' ? C.accent : C.textMuted} />
            </button>
            <button onClick={() => setView('tree')} title="Деревом"
              style={{ display: 'flex', padding: '3px 7px', border: 'none', cursor: 'pointer', background: viewMode === 'tree' ? C.accentLight : 'transparent' }}>
              <ListTree size={14} strokeWidth={ICON_STROKE} color={viewMode === 'tree' ? C.accent : C.textMuted} />
            </button>
          </div>
          </>
          )}
        </div>
        {/* Тело: история ветки (скоуп «ветка») ИЛИ список/дерево файлов */}
        <div style={{ flex: 1, overflowY: 'auto', padding: '0 8px 6px' }}>
          {isBranch ? (
            pushedCommits.length === 0 ? (
              <div style={{ padding: '20px 12px', textAlign: 'center', fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>
                {st.logLoaded ? 'Нет опубликованных коммитов' : 'Загрузка…'}
              </div>
            ) : pushedCommits.map(renderCommitRow)
          ) : rows.length === 0 ? (
            <div style={{ padding: '20px 12px', textAlign: 'center', fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>
              {isWorking ? 'Рабочее дерево чистое' : 'Нет файлов'}
            </div>
          ) : viewMode === 'tree'
            ? renderTree(tree, 0)
            : rows.map(f => renderFileRow(f, 0, true))}
        </div>
      </div>

      {/* === Нижняя зона: форма фиксации ИЛИ селектор скоупов === */}
      {mode === 'commit' ? (
        <div style={{ borderTop: `1px solid ${C.border}`, background: C.bgInset, padding: '8px 10px 10px', flexShrink: 0, display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <Pencil size={14} strokeWidth={ICON_STROKE} color={C.accent} />
            <span style={{ flex: 1, fontSize: 12, fontWeight: 600, color: C.textSecondary }}>Фиксация · {selectedCount} файл(ов)</span>
            <IconButton size="xs" title="Закрыть" onClick={() => setMode('list')}>
              <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
          {/* Двухстрочное поле с автоувеличением + ✨ в углу */}
          <div style={{ position: 'relative' }}>
            <TextArea
              value={summary}
              onChange={setSummary}
              placeholder="Опишите изменения"
              autoFocus
              autoGrow
              minHeight={commitBodyH}
              style={{ paddingRight: 34 }}
            />
            <span
              onClick={() => { if (!aiBusy) void handleAi(); }}
              title="Сгенерировать описание"
              style={{ position: 'absolute', right: 9, top: 9, cursor: 'pointer', display: 'flex' }}
            >
              {aiBusy
                ? <span style={{ width: 13, height: 13, borderRadius: '50%', border: `2px solid ${C.track}`, borderTopColor: C.accent, animation: 'cc-spin 0.6s linear infinite' }} />
                : <Sparkles size={15} strokeWidth={ICON_STROKE} color={C.accent} />}
            </span>
          </div>
          {/* Действия: настройки слева; делегирование чату + «Зафиксировать» справа
              в один ряд. nowrap + primary flexShrink:0 — «Зафиксировать» всегда стоит
              на своём месте справа и не перескакивает на другую строку; при нехватке
              места ужимаются вторичные кнопки, а не главная */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <IconButton size="sm" title="Настройки промпта коммита" onClick={() => setPromptOpen(true)}>
              <Settings size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
            <div style={{ marginLeft: 'auto', minWidth: 0, display: 'flex', alignItems: 'center', justifyContent: 'flex-end', flexWrap: 'nowrap', gap: 6, overflow: 'hidden' }}>
              <button onClick={() => onCommit?.('chat')} title="Зафиксировать в текущем чате"
                style={{ ...chatBtnStyle, minWidth: 0, overflow: 'hidden' }}>
                <MessageSquare size={13} strokeWidth={ICON_STROKE} color={C.accent} /> В чате
              </button>
              <button onClick={() => onCommit?.('newChat')} title="Зафиксировать в новом чате"
                style={{ ...chatBtnStyle, minWidth: 0, overflow: 'hidden' }}>
                <MessageSquarePlus size={13} strokeWidth={ICON_STROKE} color={C.accent} /> В новом
              </button>
              <span style={{ flexShrink: 0, display: 'flex' }}>
                <Button variant="primary" size="sm" disabled={!canCommit} loading={st.busy} onClick={() => void doCommit()}>
                  Зафиксировать
                </Button>
              </span>
            </div>
          </div>
        </div>
      ) : (
        <div style={{ borderTop: `1px solid ${C.border}`, background: C.bgInset, padding: '6px 8px', flexShrink: 0 }}>
          {/* Хендл ресайза высоты зоны скоупов — когда есть что скроллить (стэши/коммиты) */}
          {(st.stashes.length > 0 || st.unpushed.length > 0) && (
            <div
              onPointerDown={handleScopeResize}
              title="Потяните, чтобы изменить высоту зоны"
              style={{ height: 9, margin: '-6px -8px 2px', cursor: 'row-resize', touchAction: 'none', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
            >
              <div style={{ width: 30, height: 3, borderRadius: 2, background: C.border }} />
            </div>
          )}
          {/* Скролл скоупов: «Не зафиксировано» → стэши → коммиты; высота — хендлом */}
          <div style={{ maxHeight: scopeH, overflowY: 'auto' }}>
              {/* «Не зафиксировано» — первым элементом списка, показываем всегда
                  (даже при чистом дереве); при пустом дереве — без кнопок и счётчика */}
              <div
                onClick={() => selectScope('working')}
                style={{
                  display: 'flex', alignItems: 'center', gap: 7, minHeight: 30, padding: '4px 8px',
                  borderRadius: 8, cursor: 'pointer',
                  background: isWorking ? C.accentLight : 'transparent',
                }}
              >
                <Pencil size={13} strokeWidth={ICON_STROKE} color={isWorking ? C.accent : C.textSecondary} style={{ flexShrink: 0 }} />
                <span style={{ flex: 1, minWidth: 0, fontSize: 12, color: isWorking ? C.accent : C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>Не зафиксировано</span>
                {/* Есть изменения и скоуп активен: «Зафиксировать» + отмена всех;
                    иначе счётчик файлов (при чистом дереве — 0, без кнопок) */}
                {workingFiles.length > 0 && isWorking ? (
                  <>
                    <button
                      onClick={e => { e.stopPropagation(); void openCommitForm(); }}
                      title="Зафиксировать изменения"
                      style={{ flexShrink: 0, fontSize: 11, fontWeight: 600, color: C.onAccent, background: C.accent, border: 'none', borderRadius: 6, padding: '3px 10px', cursor: 'pointer', whiteSpace: 'nowrap' }}
                    >Зафиксировать</button>
                    <IconButton size="xs" tone="danger" color={C.danger} title="Отменить все изменения"
                      onClick={e => { e.stopPropagation(); setDiscardAllConfirm(true); }}>
                      <Undo2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                    </IconButton>
                  </>
                ) : (
                  <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, minWidth: 14, textAlign: 'right' }}>{workingFiles.length}</span>
                )}
              </div>

              {/* Отложенное (stash): кнопки pop/drop появляются на месте времени по наведению */}
              {st.stashes.map((s: GitStashEntry) => {
                const rowKey = `stash:${s.index}`;
                const hovered = hoveredRow === rowKey;
                const active = activeScope === rowKey;
                return (
                  <div
                    key={rowKey}
                    onClick={() => selectScope(rowKey)}
                    onMouseEnter={() => setHoveredRow(rowKey)}
                    onMouseLeave={() => setHoveredRow(null)}
                    style={{ display: 'flex', alignItems: 'center', gap: 7, minHeight: 30, position: 'relative', padding: '4px 8px', borderRadius: 8, cursor: 'pointer', background: active ? C.accentLight : hovered ? C.bgSelected : 'transparent' }}
                  >
                    <Archive size={13} strokeWidth={ICON_STROKE} color={active ? C.accent : C.textSecondary} style={{ flexShrink: 0 }} />
                    <span title={s.message} style={{ flex: 1, minWidth: 0, fontSize: 12, color: active ? C.accent : C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {s.message || `stash@{${s.index}}`}
                    </span>
                    {/* Время — всегда в потоке (держит высоту строки); под кнопками при ховере прячем */}
                    <span title={relTime(s.date)} style={{ fontFamily: FONT.mono, fontSize: 10, color: C.textMuted, flexShrink: 0, opacity: hovered && !st.busy ? 0 : 1 }}>{relTime(s.date)}</span>
                    {/* Кнопки — absolute поверх времени, не двигают layout */}
                    {hovered && !st.busy && (
                      <span style={{ position: 'absolute', right: 4, top: '50%', transform: 'translateY(-50%)', display: 'flex', alignItems: 'center', gap: 2 }}>
                        <IconButton size="xs" tone="accent" title="Вернуть изменения (pop)"
                          onClick={e => { e.stopPropagation(); if (active) setActiveScope('working'); void gitStashPop(project.id, s.index); }}>
                          <ArchiveRestore size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                        </IconButton>
                        <IconButton size="xs" tone="danger" color={C.danger} title="Удалить стэш"
                          onClick={e => { e.stopPropagation(); setStashDropConfirm(s); }}>
                          <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                        </IconButton>
                      </span>
                    )}
                  </div>
                );
              })}
              {st.unpushed.map((c: GitLogEntry) => {
                const active = activeScope === c.sha;
                return (
                  <div
                    key={c.sha}
                    onClick={() => selectScope(c.sha)}
                    style={{
                      display: 'flex', alignItems: 'center', gap: 7, minHeight: 30, padding: '4px 8px',
                      borderRadius: 8, cursor: 'pointer', background: active ? C.accentLight : 'transparent',
                    }}
                  >
                    <GitCommit size={13} strokeWidth={ICON_STROKE} color={active ? C.accent : C.textSecondary} style={{ flexShrink: 0 }} />
                    <span title={c.subject} style={{ flex: 1, minWidth: 0, fontSize: 12, color: active ? C.accent : C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{c.subject}</span>
                    <span title={relTime(c.date)} style={{ fontFamily: FONT.mono, fontSize: 10, color: C.accent, flexShrink: 0 }}>{c.shortSha}</span>
                  </div>
                );
              })}
          </div>
        </div>
      )}

      {/* === Ветка: строка в стиле скоупа (селектор ветки + fetch/pull); в режиме фиксации скрыта === */}
      {mode === 'list' && (
      <div style={{ background: C.bgInset, padding: '0 8px 6px', flexShrink: 0 }}>
        {/* Весь ряд ветки = один скоуп: подсветка на всю длину, включая fetch/pull */}
        <div
          onMouseEnter={() => setHoveredRow('branch')}
          onMouseLeave={() => setHoveredRow(null)}
          style={{
            display: 'flex', alignItems: 'center', gap: 6, borderRadius: 8, padding: '0 2px 0 0',
            background: (isBranch || branchMenu) ? C.accentLight : hoveredRow === 'branch' ? C.bgSelected : 'transparent',
          }}
        >
          <div style={{ position: 'relative', flex: 1, minWidth: 0, display: 'flex' }}>
            {/* Тело строки = выбор скоупа «ветка» (история в верхней зоне);
                стрелка вниз — отдельная кнопка, открывает меню выбора/создания ветки */}
            <div
              onClick={() => selectScope('branch')}
              title={status?.branch ?? undefined}
              style={{
                flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 7, minHeight: 30,
                padding: '4px 4px 4px 8px', borderRadius: 8, cursor: 'pointer', textAlign: 'left',
                background: 'transparent',
              }}
            >
              {st.busy
                ? <span style={{ width: 13, height: 13, flexShrink: 0, borderRadius: '50%', border: `2px solid ${C.track}`, borderTopColor: C.accent, animation: 'cc-spin 0.6s linear infinite' }} />
                : <GitBranch size={13} strokeWidth={ICON_STROKE} color={isBranch ? C.accent : C.textSecondary} style={{ flexShrink: 0 }} />}
              <span style={{ flex: 1, minWidth: 0, fontSize: 12, color: isBranch ? C.accent : C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {status?.detached ? `${status.branch ?? 'HEAD'} (detached)` : (status?.branch ?? '—')}
              </span>
              {(ahead > 0 || (status?.behind ?? 0) > 0) && (
                <span style={{ fontFamily: FONT.mono, fontSize: 10, color: isBranch ? C.accent : C.textMuted, flexShrink: 0, display: 'flex', gap: 3 }}>
                  {ahead > 0 && <span>↑{ahead}</span>}
                  {(status?.behind ?? 0) > 0 && <span>↓{status?.behind}</span>}
                </span>
              )}
              <button
                onClick={e => { e.stopPropagation(); openBranchMenu(); }}
                disabled={st.busy}
                title="Выбрать ветку"
                style={{
                  display: 'flex', alignItems: 'center', flexShrink: 0, padding: '3px 4px', margin: '-3px 0',
                  border: 'none', background: 'transparent', borderRadius: 6, cursor: st.busy ? 'default' : 'pointer',
                }}
              >
                <ChevronDown size={12} strokeWidth={ICON_STROKE} color={branchMenu ? C.accent : C.textMuted} />
              </button>
            </div>
            {branchMenu && (
              <Menu onClose={() => setBranchMenu(false)} align="left" bottom={40} minWidth={220}>
                {st.branches.map(b => (
                  <MenuItem
                    key={b.name}
                    icon={b.current ? <Check size={15} strokeWidth={2} /> : <></>}
                    label={<span style={{ fontFamily: FONT.mono, fontSize: 12.5 }}>{b.name}</span>}
                    onClick={() => handleCheckout(b.name)}
                  />
                ))}
                {st.branches.length === 0 && (
                  <div style={{ padding: '8px 10px', fontSize: 12, color: C.textMuted }}>Загрузка…</div>
                )}
                <div style={{ height: 1, background: C.border, margin: '4px 6px' }} />
                <MenuItem icon={<Plus size={15} strokeWidth={ICON_STROKE} />} label="Новая ветка…"
                  onClick={() => { setBranchMenu(false); setNewBranchOpen(true); }} />
              </Menu>
            )}
          </div>
          <IconButton size="sm" title="Забрать и слить (pull)" disabled={st.busy} onClick={() => void gitPull(project.id)}>
            <ArrowDownToLine size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
          <IconButton size="sm" title="Проверить обновления (fetch)" disabled={st.busy} onClick={() => void gitFetch(project.id)}>
            <RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        </div>
      </div>
      )}

      {/* Опубликовать — вне режима фиксации; когда публиковать нечего — дизейблим, не скрываем */}
      {mode === 'list' && (
        <div style={{ borderTop: `1px solid ${C.border}`, padding: '9px 10px', flexShrink: 0 }}>
          <Button variant="primary" fullWidth size="sm" loading={st.busy} disabled={!canPublish}
            leftIcon={<CloudUpload size={14} strokeWidth={ICON_STROKE} />}
            onClick={() => setPublishConfirm(true)}>
            {canPublish ? `Опубликовать ${ahead || st.unpushed.length}` : 'Опубликовать'}
          </Button>
        </div>
      )}

      {/* === Подтверждение отмены изменений === */}
      {discardPath && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => setDiscardPath(null)}
          title="Отменить изменения"
          subtitle={<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{discardPath}</span>}
          footer={
            <ModalActions
              confirmLabel="Отменить изменения"
              confirmVariant="danger"
              onConfirm={() => { const p = discardPath; setDiscardPath(null); void gitDiscard(project.id, p); }}
              onCancel={() => setDiscardPath(null)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Правки в <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{splitPath(discardPath)[1]}</span> будут потеряны безвозвратно.
          </div>
        </Modal>
      )}

      {/* === Подтверждение отмены ВСЕХ изменений === */}
      {discardAllConfirm && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => setDiscardAllConfirm(false)}
          title="Отменить все изменения"
          subtitle={<span>{workingFiles.length} файл(ов) в рабочем дереве</span>}
          footer={
            <ModalActions
              confirmLabel="Отменить все"
              confirmVariant="danger"
              onConfirm={() => { setDiscardAllConfirm(false); setActiveScope('working'); void gitDiscardAll(project.id); }}
              onCancel={() => setDiscardAllConfirm(false)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Все незафиксированные правки будут сброшены к последнему коммиту, а новые (неотслеживаемые) файлы — удалены безвозвратно. Игнорируемые файлы (сборки, артефакты) не тронем.
          </div>
        </Modal>
      )}

      {/* === Подтверждение публикации (push) === */}
      {publishConfirm && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => setPublishConfirm(false)}
          title="Опубликовать изменения"
          subtitle={<span>Отправить {ahead || st.unpushed.length} коммит(ов) на сервер</span>}
          footer={
            <ModalActions
              confirmLabel="Опубликовать"
              onConfirm={() => { setPublishConfirm(false); void gitPush(project.id); }}
              onCancel={() => setPublishConfirm(false)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Локальные коммиты ветки <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{status?.branch}</span> будут отправлены в удалённый репозиторий (git push).
          </div>
        </Modal>
      )}

      {/* === Подтверждение удаления стэша === */}
      {stashDropConfirm && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => setStashDropConfirm(null)}
          title="Удалить стэш"
          subtitle={<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{stashDropConfirm.message || `stash@{${stashDropConfirm.index}}`}</span>}
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={() => { const i = stashDropConfirm.index; if (activeScope === `stash:${i}`) setActiveScope('working'); setStashDropConfirm(null); void gitStashDrop(project.id, i); }}
              onCancel={() => setStashDropConfirm(null)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Отложенные изменения будут удалены безвозвратно.
          </div>
        </Modal>
      )}

      {/* === Guard: переключение ветки при незафиксированных изменениях === */}
      {pendingCheckout && (
        <Modal
          width={500}
          onClose={() => setPendingCheckout(null)}
          title="Переключить ветку"
          subtitle={<>На <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{pendingCheckout}</span></>}
          footer={
            <div style={{ display: 'flex', gap: 8, width: '100%' }}>
              <div style={{ flex: 0.8 }}>
                <Button variant="secondary" size="md" fullWidth onClick={() => setPendingCheckout(null)}>Отмена</Button>
              </div>
              <div style={{ flex: 1.3 }}>
                <Button variant="secondary" size="md" fullWidth
                  onClick={async () => { const b = pendingCheckout; setPendingCheckout(null); if (await gitStashPush(project.id)) doCheckout(b); }}>
                  Отложить в стэш
                </Button>
              </div>
              <div style={{ flex: 1.2 }}>
                <Button variant="primary" size="md" fullWidth
                  onClick={() => { const b = pendingCheckout; setPendingCheckout(null); doCheckout(b); }}>
                  Переключиться
                </Button>
              </div>
            </div>
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            В рабочем дереве есть незафиксированные изменения ({workingFiles.length}). При переключении Git перенесёт их на выбранную ветку, а при конфликте — откажет. Можно сначала отложить их в стэш — они появятся в разделе «Отложено» этой панели, вернёте позже кнопкой ↩.
          </div>
        </Modal>
      )}

      {/* === Диалог новой ветки === */}
      {newBranchOpen && (
        <Modal
          width={MODAL_W.form}
          onClose={() => { setNewBranchOpen(false); setNewBranchName(''); }}
          title="Новая ветка"
          subtitle={status?.branch ? <>От <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{status.branch}</span></> : undefined}
          footer={
            <ModalActions
              confirmLabel="Создать"
              onConfirm={handleCreateBranch}
              confirmDisabled={!newBranchName.trim()}
              onCancel={() => { setNewBranchOpen(false); setNewBranchName(''); }}
            />
          }
        >
          <TextField value={newBranchName} onChange={setNewBranchName} placeholder="feature/my-branch" mono autoFocus onEnter={handleCreateBranch} />
        </Modal>
      )}

      {promptOpen && <CommitPromptDialog project={project} onClose={() => setPromptOpen(false)} />}
    </div>
  );
}

// === Диалог настройки промпта AI-описания коммита ===
// Два уровня редактируются в одном окне: «Общий» (per-user) и «Промпт этого проекта».
// Тог выбирает активный уровень (какой применяется к ✨-генерации) и что редактируется.
function CommitPromptDialog({ project, onClose }: { project: Project; onClose: () => void }) {
  const [globalText, setGlobalText] = useState('');
  const [projectText, setProjectText] = useState('');
  const [level, setLevel] = useState<'global' | 'project'>('global');
  const [busy, setBusy] = useState(false);
  const [detecting, setDetecting] = useState(false);

  useEffect(() => {
    let alive = true;
    void api.git.getCommitPrompt(project.id).then(i => {
      if (!alive) return;
      setGlobalText(i.global ?? '');
      setProjectText(i.projectOverride ?? '');
      setLevel(i.useProject ? 'project' : 'global');
    }).catch(() => {});
    return () => { alive = false; };
  }, [project.id]);

  const isProject = level === 'project';
  const text = isProject ? projectText : globalText;
  const setText = isProject ? setProjectText : setGlobalText;

  const detect = async () => {
    setDetecting(true);
    try { setText((await api.git.detectCommitStyle(project.id)).prompt); }
    catch { /* мало истории/ошибка — оставляем поле */ }
    finally { setDetecting(false); }
  };

  const save = async () => {
    setBusy(true);
    // Global пишем всегда; project override — только когда активен проектный уровень
    try { await api.git.setCommitPrompt(project.id, globalText.trim(), projectText.trim(), isProject); onClose(); }
    catch { setBusy(false); }
  };

  const seg = (val: 'global' | 'project', label: string) => (
    <button
      onClick={() => setLevel(val)}
      style={{
        flex: 1, padding: '7px 12px', fontSize: 12.5, fontWeight: 600, cursor: 'pointer', border: 'none',
        fontFamily: FONT.sans, background: level === val ? C.accent : 'transparent',
        color: level === val ? C.onAccent : C.textSecondary, transition: 'background 0.12s',
      }}
    >{label}</button>
  );

  return (
    <Modal
      width={560}
      onClose={onClose}
      title="Промпт коммита"
      subtitle="Правила стиля для ✨-генерации сообщения. Активен выбранный уровень."
      footer={<ModalActions confirmLabel="Сохранить" onConfirm={save} loading={busy} onCancel={onClose} />}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {/* Два тога: какой уровень редактируем и применяем */}
        <div style={{ display: 'flex', border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden' }}>
          {seg('global', 'Общий промпт')}
          <div style={{ width: 1, background: C.border }} />
          {seg('project', 'Промпт этого проекта')}
        </div>
        <TextArea
          value={text}
          onChange={setText}
          placeholder={isProject
            ? 'Пусто — для этого проекта используется общий промпт'
            : 'Пусто — сообщения в стиле по умолчанию (Conventional Commits на русском). Опишите свои правила стиля…'}
          minHeight={180}
          maxHeight={340}
          autoGrow
        />
        <button
          onClick={() => { if (!detecting) void detect(); }}
          disabled={detecting}
          style={{ alignSelf: 'flex-start', display: 'flex', alignItems: 'center', gap: 6, padding: '6px 12px', borderRadius: R.md, cursor: 'pointer', border: `1.5px dashed ${C.dashed}`, background: 'none', color: C.accent, fontSize: 12.5, fontFamily: FONT.sans }}
        >
          <Wand2 size={14} strokeWidth={ICON_STROKE} />
          {detecting ? 'Анализирую историю…' : 'Определить стиль AI'}
        </button>
      </div>
    </Modal>
  );
}
