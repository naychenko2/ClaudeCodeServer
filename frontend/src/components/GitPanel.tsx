// Git в разделе «Файлы»: панели «Изменения» (staged/unstaged/untracked + коммит)
// и «История» (лента коммитов). Рендерятся внутри сайдбара FileExplorer вместо дерева.
// Данные и мутации — через стор lib/git.ts (realtime git_status_changed).

import { useEffect, useState } from 'react';
import { GitBranch, ChevronDown, RefreshCw, ArrowDown, ArrowUp, Plus, Minus, Undo2, Check, ExternalLink, Settings, ArchiveRestore, Trash2 } from 'lucide-react';
import type { Project, GitFileChange, GitStashEntry } from '../types';
import { C, R, FONT, MODAL_W, GROUP_COLORS } from '../lib/design';
import {
  useGitState, loadGitLog, loadGitBranches, loadGitStash, loadGitRemote,
  gitStage, gitUnstage, gitStageAll, gitDiscard, gitCommit,
  gitCheckout, gitCreateBranch, gitFetch, gitPull, gitPush, clearGitError,
  gitStashPush, gitStashPop, gitStashDrop, gitSetAutoCommit,
} from '../lib/git';
import { Modal, ModalActions, TextField, TextArea, IconButton, Button, Menu, MenuItem, Toggle } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

// Цвета статус-бейджа по односимвольному коду git
function statusBadge(status: string): { fg: string; bg: string } {
  switch (status) {
    case 'M': return { fg: C.accent, bg: C.accentLight };
    case 'A': return { fg: C.successText, bg: C.successBg };
    case 'D': return { fg: C.danger, bg: C.dangerBg };
    case 'R': return { fg: C.info, bg: C.infoBg };
    case 'U': return { fg: C.warningText, bg: C.warningBg };
    case '?': return { fg: C.textMuted, bg: C.bgSelected };
    default: return { fg: C.textMuted, bg: C.bgSelected };
  }
}

// [папка-родитель, имя файла] из относительного пути
function splitPath(p: string): [string, string] {
  const norm = p.replace(/\\/g, '/').replace(/\/+$/, '');
  const i = norm.lastIndexOf('/');
  return i < 0 ? ['', norm] : [norm.slice(0, i), norm.slice(i + 1)];
}

// Детерминированный цвет из палитры групп по имени автора
function colorFor(name: string): string {
  let h = 0;
  for (let i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) | 0;
  return GROUP_COLORS[Math.abs(h) % GROUP_COLORS.length];
}

// Относительная дата: «2 ч назад», «5 мин назад», дальше — обычная дата
// (экспорт — переиспользуется во вкладке «Авторы» FileViewer)
export function relTime(iso: string): string {
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  const diffMin = Math.floor((Date.now() - t) / 60_000);
  if (diffMin < 1) return 'только что';
  if (diffMin < 60) return `${diffMin} мин назад`;
  const h = Math.floor(diffMin / 60);
  if (h < 24) return `${h} ч назад`;
  const d = Math.floor(h / 24);
  if (d < 30) return `${d} дн назад`;
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' });
}

const COMMIT_SUMMARY_MAX = 72;

type SectionKey = 'staged' | 'unstaged' | 'untracked' | 'stash';

interface ChangesProps {
  project: Project;
  // Открыть diff файла в центральной области (staged — дифф индекса)
  onOpenDiff: (path: string, staged: boolean) => void;
  // Открыть содержимое файла (новые файлы — диффа против HEAD нет)
  onOpenFile: (path: string) => void;
}

export function GitChangesPanel({ project, onOpenDiff, onOpenFile }: ChangesProps) {
  const st = useGitState(project.id);
  const status = st.status;

  const [collapsed, setCollapsed] = useState<Set<SectionKey>>(new Set());
  const [branchMenu, setBranchMenu] = useState(false);
  const [newBranchOpen, setNewBranchOpen] = useState(false);
  const [newBranchName, setNewBranchName] = useState('');
  const [discardConfirm, setDiscardConfirm] = useState<GitFileChange | null>(null);
  const [hoveredRow, setHoveredRow] = useState<string | null>(null);

  // Настройки (авто-коммит) + стэш
  const [settingsMenu, setSettingsMenu] = useState(false);
  const [stashOpen, setStashOpen] = useState(false);
  const [stashName, setStashName] = useState('');
  const [stashDropConfirm, setStashDropConfirm] = useState<GitStashEntry | null>(null);

  // Форма коммита
  const [summary, setSummary] = useState('');
  const [description, setDescription] = useState('');
  const [amend, setAmend] = useState(false);

  // Стэши и remote-инфо (Forgejo + авто-коммит) — при открытии панели
  useEffect(() => {
    void loadGitStash(project.id);
    void loadGitRemote(project.id);
  }, [project.id]);

  const toggleSection = (key: SectionKey) =>
    setCollapsed(prev => { const n = new Set(prev); n.has(key) ? n.delete(key) : n.add(key); return n; });

  const openBranchMenu = () => {
    if (!branchMenu) void loadGitBranches(project.id);
    setBranchMenu(v => !v);
  };

  const handleCheckout = (name: string) => {
    setBranchMenu(false);
    if (name !== status?.branch) void gitCheckout(project.id, name);
  };

  const handleCreateBranch = async () => {
    const name = newBranchName.trim();
    if (!name) return;
    setNewBranchOpen(false);
    setNewBranchName('');
    await gitCreateBranch(project.id, name);
  };

  const handleCommit = async () => {
    const msg = description.trim()
      ? `${summary.trim()}\n\n${description.trim()}`
      : summary.trim();
    const ok = await gitCommit(project.id, msg, amend);
    if (ok) { setSummary(''); setDescription(''); setAmend(false); }
  };

  const handleDiscard = async () => {
    if (!discardConfirm) return;
    const path = discardConfirm.path;
    setDiscardConfirm(null);
    await gitDiscard(project.id, path);
  };

  const handleStashPush = async () => {
    const msg = stashName.trim();
    setStashOpen(false);
    setStashName('');
    await gitStashPush(project.id, msg || undefined);
  };

  const handleStashDrop = async () => {
    if (!stashDropConfirm) return;
    const index = stashDropConfirm.index;
    setStashDropConfirm(null);
    await gitStashDrop(project.id, index);
  };

  const remote = st.remote;
  const handleToggleAutoCommit = () => {
    if (!remote) return;
    // Выключение авто-коммита выключает и авто-пуш (он без коммитов бессмыслен)
    void gitSetAutoCommit(project.id, !remote.autoCommit, !remote.autoCommit ? remote.autoPush : false);
  };
  const handleToggleAutoPush = () => {
    if (!remote?.autoCommit) return;
    void gitSetAutoCommit(project.id, true, !remote.autoPush);
  };

  // Все staged убрать из индекса — unstage каждого по очереди
  const handleUnstageAll = async () => {
    for (const f of status?.staged ?? []) {
      const ok = await gitUnstage(project.id, f.path);
      if (!ok) break;
    }
  };

  const stagedCount = status?.staged.length ?? 0;
  const canCommit = stagedCount > 0 && !!summary.trim() && !st.busy;

  const renderRow = (file: GitFileChange, section: SectionKey) => {
    const [parent, name] = splitPath(file.path);
    const badge = statusBadge(section === 'untracked' ? '?' : file.status);
    const rowKey = `${section}:${file.path}`;
    const hovered = hoveredRow === rowKey;
    const handleOpen = () => {
      // У новых (untracked) файлов диффа против HEAD нет — открываем содержимое
      if (section === 'untracked') onOpenFile(file.path);
      else onOpenDiff(file.path, section === 'staged');
    };
    return (
      <div
        key={rowKey}
        onClick={handleOpen}
        onMouseEnter={() => setHoveredRow(rowKey)}
        onMouseLeave={() => setHoveredRow(null)}
        style={{
          display: 'flex', alignItems: 'center', gap: 7,
          padding: '5px 6px 5px 8px', minHeight: 34,
          borderRadius: 8, cursor: 'pointer',
          background: hovered ? C.bgSelected : 'transparent',
          transition: 'background 0.1s',
        }}
      >
        <span style={{
          width: 16, height: 16, borderRadius: 4, flexShrink: 0,
          background: badge.bg, color: badge.fg,
          fontSize: 9, fontWeight: 700, fontFamily: FONT.mono,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>{section === 'untracked' ? '?' : file.status}</span>
        <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
          <span title={file.path} style={{
            fontFamily: FONT.mono, fontSize: 13, fontWeight: 500, color: C.textHeading,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>{name}</span>
          {parent && (
            <span title={file.path} style={{
              fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}>{parent}</span>
          )}
        </span>
        {hovered && !st.busy && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 2, flexShrink: 0 }}>
            {section === 'staged' ? (
              <IconButton size="xs" title="Убрать из индекса"
                onClick={e => { e.stopPropagation(); void gitUnstage(project.id, file.path); }}>
                <Minus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            ) : (
              <>
                <IconButton size="xs" tone="accent" title="Проиндексировать"
                  onClick={e => { e.stopPropagation(); void gitStage(project.id, file.path); }}>
                  <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
                <IconButton size="xs" tone="danger" color={C.danger} title="Отменить изменения"
                  onClick={e => { e.stopPropagation(); setDiscardConfirm(file); }}>
                  <Undo2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
              </>
            )}
          </span>
        )}
      </div>
    );
  };

  const renderSection = (key: SectionKey, title: string, items: GitFileChange[]) => {
    const isCollapsed = collapsed.has(key);
    return (
      <div key={key} style={{ marginBottom: 4 }}>
        <div
          onClick={() => toggleSection(key)}
          style={{
            display: 'flex', alignItems: 'center', gap: 5,
            padding: '6px 6px 6px 4px', cursor: 'pointer', userSelect: 'none',
            borderRadius: 8, minHeight: 30,
          }}
        >
          <span style={{ width: 12, textAlign: 'center', color: C.textMuted, fontSize: 9, lineHeight: 1, flexShrink: 0 }}>
            {isCollapsed ? '▸' : '▾'}
          </span>
          <span style={{ fontSize: 11.5, fontWeight: 700, color: C.textSecondary, letterSpacing: '0.03em', textTransform: 'uppercase' }}>
            {title}
          </span>
          <span style={{
            fontSize: 10.5, fontWeight: 700, fontFamily: FONT.mono, color: C.textMuted,
            background: C.bgSelected, borderRadius: 8, padding: '1px 6px', minWidth: 14, textAlign: 'center',
          }}>{items.length}</span>
          <span style={{ flex: 1 }} />
          {items.length > 0 && !st.busy && (
            key === 'staged' ? (
              <IconButton size="xs" title="Убрать всё из индекса"
                onClick={e => { e.stopPropagation(); void handleUnstageAll(); }}>
                <Minus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            ) : (
              <IconButton size="xs" tone="accent" title="Добавить всё в индекс"
                onClick={e => { e.stopPropagation(); void gitStageAll(project.id); }}>
                <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            )
          )}
        </div>
        {!isCollapsed && (
          items.length === 0
            ? <div style={{ padding: '2px 8px 6px 21px', fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans }}>Пусто</div>
            : items.map(f => renderRow(f, key))
        )}
      </div>
    );
  };

  // === Секция «Отложено» (stash) ===

  const renderStashRow = (s: GitStashEntry) => {
    const rowKey = `stash:${s.index}`;
    const hovered = hoveredRow === rowKey;
    return (
      <div
        key={rowKey}
        onMouseEnter={() => setHoveredRow(rowKey)}
        onMouseLeave={() => setHoveredRow(null)}
        style={{
          display: 'flex', alignItems: 'center', gap: 7,
          padding: '5px 6px 5px 21px', minHeight: 34, borderRadius: 8,
          background: hovered ? C.bgSelected : 'transparent',
          transition: 'background 0.1s',
        }}
      >
        <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
          <span title={s.message} style={{
            fontSize: 13, fontWeight: 500, color: C.textHeading, fontFamily: FONT.sans,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>{s.message || `stash@{${s.index}}`}</span>
          <span style={{ fontSize: 10.5, color: C.textMuted, fontFamily: FONT.sans }}>{relTime(s.date)}</span>
        </span>
        {hovered && !st.busy && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 2, flexShrink: 0 }}>
            <IconButton size="xs" tone="accent" title="Вернуть изменения (pop)"
              onClick={e => { e.stopPropagation(); void gitStashPop(project.id, s.index); }}>
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
  };

  const renderStashSection = () => {
    const isCollapsed = collapsed.has('stash');
    const hasChanges = !!status && (status.staged.length + status.unstaged.length + status.untracked.length) > 0;
    return (
      <div style={{ marginBottom: 4 }}>
        <div
          onClick={() => toggleSection('stash')}
          style={{
            display: 'flex', alignItems: 'center', gap: 5,
            padding: '6px 6px 6px 4px', cursor: 'pointer', userSelect: 'none',
            borderRadius: 8, minHeight: 30,
          }}
        >
          <span style={{ width: 12, textAlign: 'center', color: C.textMuted, fontSize: 9, lineHeight: 1, flexShrink: 0 }}>
            {isCollapsed ? '▸' : '▾'}
          </span>
          <span style={{ fontSize: 11.5, fontWeight: 700, color: C.textSecondary, letterSpacing: '0.03em', textTransform: 'uppercase' }}>
            Отложено
          </span>
          <span style={{
            fontSize: 10.5, fontWeight: 700, fontFamily: FONT.mono, color: C.textMuted,
            background: C.bgSelected, borderRadius: 8, padding: '1px 6px', minWidth: 14, textAlign: 'center',
          }}>{st.stashes.length}</span>
          <span style={{ flex: 1 }} />
          {hasChanges && !st.busy && (
            <button
              title="Отложить текущие изменения в стэш"
              onClick={e => { e.stopPropagation(); setStashOpen(true); }}
              style={{
                background: 'none', border: 'none', cursor: 'pointer', color: C.accent,
                fontSize: 11.5, fontWeight: 600, fontFamily: FONT.sans, padding: '2px 4px', whiteSpace: 'nowrap',
              }}
            >Отложить…</button>
          )}
        </div>
        {!isCollapsed && (
          st.stashes.length === 0
            ? <div style={{ padding: '2px 8px 6px 21px', fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans }}>Пусто</div>
            : st.stashes.map(renderStashRow)
        )}
      </div>
    );
  };

  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      {/* Шапка: ветка + счётчики ahead/behind + fetch/pull/push */}
      <div style={{ padding: '0 12px 8px', display: 'flex', alignItems: 'center', gap: 4 }}>
        <div style={{ position: 'relative', minWidth: 0, flex: 1 }}>
          <button
            onClick={openBranchMenu}
            title={status?.branch ?? undefined}
            style={{
              display: 'flex', alignItems: 'center', gap: 6, maxWidth: '100%',
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
              padding: '5px 9px', cursor: 'pointer', minHeight: 30,
            }}
          >
            <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.accent} style={{ flexShrink: 0 }} />
            <span style={{
              fontFamily: FONT.mono, fontSize: 12.5, fontWeight: 600, color: C.textHeading,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}>{status?.detached ? `${status.branch ?? 'HEAD'} (detached)` : (status?.branch ?? '—')}</span>
            <ChevronDown size={12} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
          </button>
          {branchMenu && (
            <Menu onClose={() => setBranchMenu(false)} align="left" top={34} minWidth={220}>
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
              <MenuItem
                icon={<Plus size={15} strokeWidth={ICON_STROKE} />}
                label="Новая ветка…"
                onClick={() => { setBranchMenu(false); setNewBranchOpen(true); }}
              />
            </Menu>
          )}
        </div>
        {status && (status.ahead > 0 || status.behind > 0) && (
          <span title="Впереди / позади upstream" style={{ fontFamily: FONT.mono, fontSize: 11, fontWeight: 600, color: C.textSecondary, flexShrink: 0, display: 'flex', gap: 3 }}>
            {status.ahead > 0 && <span>↑{status.ahead}</span>}
            {status.behind > 0 && <span>↓{status.behind}</span>}
          </span>
        )}
        <IconButton size="sm" title="Fetch — получить изменения" disabled={st.busy}
          onClick={() => void gitFetch(project.id)}>
          <RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        <IconButton size="sm" title="Pull — забрать и слить" disabled={st.busy}
          onClick={() => void gitPull(project.id)}>
          <ArrowDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        <IconButton size="sm" title="Push — отправить" disabled={st.busy}
          onClick={() => void gitPush(project.id)}>
          <ArrowUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        {remote?.htmlUrl && (
          <IconButton size="sm" title="Открыть в Forgejo"
            onClick={() => window.open(remote.htmlUrl!, '_blank', 'noopener')}>
            <ExternalLink size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
        <div style={{ position: 'relative', flexShrink: 0 }}>
          <IconButton size="sm" title="Настройки git" active={settingsMenu} disabled={!remote}
            onClick={() => setSettingsMenu(v => !v)}>
            <Settings size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
          {settingsMenu && remote && (
            <Menu onClose={() => setSettingsMenu(false)} align="right" top={34} minWidth={250}>
              <MenuItem
                icon={remote.autoCommit ? <Check size={15} strokeWidth={2} /> : <></>}
                label={
                  <span title="Каждый ход ИИ фиксируется коммитом. Для документов; не для кода с параллельной ручной работой">
                    Авто-сохранение после хода ИИ
                  </span>
                }
                onClick={handleToggleAutoCommit}
              />
              <MenuItem
                disabled={!remote.autoCommit}
                icon={remote.autoCommit && remote.autoPush ? <Check size={15} strokeWidth={2} /> : <></>}
                label="…и отправлять на сервер"
                onClick={handleToggleAutoPush}
              />
            </Menu>
          )}
        </div>
      </div>

      {/* Ошибка последней операции (409 { error }) — клик скрывает */}
      {st.error && (
        <div
          onClick={() => clearGitError(project.id)}
          title="Скрыть"
          style={{ margin: '0 12px 8px', fontSize: 12, color: C.dangerText, fontFamily: FONT.sans, lineHeight: 1.4, cursor: 'pointer' }}
        >{st.error}</div>
      )}

      {/* Секции изменений */}
      <div style={{ flex: 1, overflowY: 'auto', padding: '0 8px 8px' }}>
        {status && renderSection('staged', 'Проиндексировано', status.staged)}
        {status && renderSection('unstaged', 'Изменения', status.unstaged)}
        {status && renderSection('untracked', 'Новые файлы', status.untracked)}
        {status && status.staged.length === 0 && status.unstaged.length === 0 && status.untracked.length === 0 && (
          <div style={{ padding: '20px 12px', textAlign: 'center', fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>
            Рабочее дерево чистое
          </div>
        )}
        {status && renderStashSection()}
      </div>

      {/* Форма коммита — прижата к низу сайдбара */}
      <div style={{ borderTop: `1px solid ${C.border}`, padding: '10px 12px 12px', display: 'flex', flexDirection: 'column', gap: 7, flexShrink: 0 }}>
        <div style={{ position: 'relative' }}>
          <TextField
            value={summary}
            onChange={v => setSummary(v.slice(0, COMMIT_SUMMARY_MAX))}
            placeholder="Кратко опишите изменения"
            style={{ paddingRight: 52, fontSize: 13 }}
            onEnter={() => { if (canCommit) void handleCommit(); }}
          />
          <span style={{
            position: 'absolute', right: 9, top: '50%', transform: 'translateY(-50%)',
            fontSize: 10, fontFamily: FONT.mono, color: summary.length >= COMMIT_SUMMARY_MAX ? C.danger : C.textMuted,
            pointerEvents: 'none',
          }}>{summary.length}/{COMMIT_SUMMARY_MAX}</span>
        </div>
        <TextArea
          value={description}
          onChange={setDescription}
          placeholder="Подробнее (необязательно)"
          minHeight={52}
          maxHeight={120}
          autoGrow
          style={{ fontSize: 12.5 }}
        />
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, userSelect: 'none' }}>
          <Toggle checked={amend} onChange={setAmend} width={32} height={19} />
          <span
            onClick={() => setAmend(v => !v)}
            style={{ fontSize: 12, color: C.textSecondary, fontFamily: FONT.sans, cursor: 'pointer' }}
          >Дополнить последний коммит</span>
        </div>
        <Button
          variant="primary"
          fullWidth
          disabled={!canCommit}
          loading={st.busy}
          onClick={() => void handleCommit()}
        >
          Зафиксировать ({stagedCount})
        </Button>
      </div>

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
          <TextField
            value={newBranchName}
            onChange={setNewBranchName}
            placeholder="feature/my-branch"
            mono
            autoFocus
            onEnter={handleCreateBranch}
          />
        </Modal>
      )}

      {/* === Диалог «Отложить изменения» (stash push) === */}
      {stashOpen && (
        <Modal
          width={MODAL_W.form}
          onClose={() => { setStashOpen(false); setStashName(''); }}
          title="Отложить изменения"
          subtitle="Все изменения (включая новые файлы) будут убраны из рабочего дерева и сохранены в стэш"
          footer={
            <ModalActions
              confirmLabel="Отложить"
              onConfirm={handleStashPush}
              onCancel={() => { setStashOpen(false); setStashName(''); }}
            />
          }
        >
          <TextField
            value={stashName}
            onChange={setStashName}
            placeholder="Название (необязательно)"
            autoFocus
            onEnter={handleStashPush}
          />
        </Modal>
      )}

      {/* === Подтверждение удаления стэша === */}
      {stashDropConfirm && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => setStashDropConfirm(null)}
          title="Удалить стэш"
          subtitle={<span style={{ color: C.textPrimary }}>{stashDropConfirm.message || `stash@{${stashDropConfirm.index}}`}</span>}
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={handleStashDrop}
              onCancel={() => setStashDropConfirm(null)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Стэш будет удалён безвозвратно.
          </div>
        </Modal>
      )}

      {/* === Подтверждение отмены изменений (discard) === */}
      {discardConfirm && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => setDiscardConfirm(null)}
          title="Отменить изменения"
          subtitle={<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{discardConfirm.path}</span>}
          footer={
            <ModalActions
              confirmLabel="Отменить изменения"
              confirmVariant="danger"
              onConfirm={handleDiscard}
              onCancel={() => setDiscardConfirm(null)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Будут безвозвратно потеряны изменения в <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{splitPath(discardConfirm.path)[1]}</span>.
          </div>
        </Modal>
      )}
    </div>
  );
}

// === История коммитов ===

export function GitHistoryPanel({ project, onOpenCommit }: { project: Project; onOpenCommit?: (sha: string) => void }) {
  const st = useGitState(project.id);
  const [selectedSha, setSelectedSha] = useState<string | null>(null);

  useEffect(() => { void loadGitLog(project.id); }, [project.id]);

  return (
    <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '0 8px 12px' }}>
      {st.error && (
        <div
          onClick={() => clearGitError(project.id)}
          title="Скрыть"
          style={{ margin: '0 4px 8px', fontSize: 12, color: C.dangerText, fontFamily: FONT.sans, lineHeight: 1.4, cursor: 'pointer' }}
        >{st.error}</div>
      )}
      {!st.logLoaded ? (
        <div style={{ padding: '24px 12px', color: C.textMuted, fontSize: 13, textAlign: 'center', fontFamily: FONT.mono }}>Загрузка…</div>
      ) : st.log.length === 0 ? (
        <div style={{ padding: '24px 12px', color: C.textMuted, fontSize: 12.5, textAlign: 'center', fontFamily: FONT.sans }}>
          История пуста
        </div>
      ) : (
        st.log.map(entry => {
          const selected = selectedSha === entry.sha;
          const color = colorFor(entry.author);
          return (
            <div
              key={entry.sha}
              onClick={() => { setSelectedSha(entry.sha); onOpenCommit?.(entry.sha); }}
              style={{
                display: 'flex', alignItems: 'flex-start', gap: 9,
                padding: '7px 8px', borderRadius: 8, cursor: 'pointer',
                background: selected ? C.bgSelected : 'transparent',
                transition: 'background 0.1s',
              }}
            >
              <span title={entry.author} style={{
                width: 24, height: 24, borderRadius: '50%', flexShrink: 0, marginTop: 1,
                background: color, color: '#FFF',
                fontSize: 11, fontWeight: 700, fontFamily: FONT.sans,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}>{(entry.author.trim()[0] ?? '?').toUpperCase()}</span>
              <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
                <span title={entry.subject} style={{
                  fontSize: 13, color: C.textHeading, fontFamily: FONT.sans, lineHeight: 1.35,
                  overflow: 'hidden', textOverflow: 'ellipsis',
                  display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical',
                }}>{entry.subject}</span>
                <span style={{ fontSize: 10.5, color: C.textMuted, fontFamily: FONT.sans, display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                  <span style={{ fontFamily: FONT.mono, color: C.accent }}>{entry.shortSha}</span>
                  <span>{entry.author}</span>
                  <span>·</span>
                  <span>{relTime(entry.date)}</span>
                </span>
              </span>
            </div>
          );
        })
      )}
    </div>
  );
}
