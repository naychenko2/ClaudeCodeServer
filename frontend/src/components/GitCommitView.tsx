import { useEffect, useMemo, useState } from 'react';
import { ChevronDown, ChevronRight, MoreHorizontal, Search, Undo2, X } from 'lucide-react';
import type { Project, GitCommitDetail, GitFileChange } from '../types';
import { api } from '../lib/api';
import { gitRevertCommit } from '../lib/git';
import { C, FONT, MODAL_W, R } from '../lib/design';
import { DiffView } from './DiffView';
import { IconButton, Menu, MenuItem, Modal, ModalActions } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

// Цвета статус-бейджей — как в GitPanel/дереве файлов
const BADGE: Record<string, { color: string; bg: string; label: string }> = {
  M: { color: C.accent, bg: C.accentLight, label: 'M' },
  A: { color: C.successText, bg: C.successBg, label: '+' },
  D: { color: C.danger, bg: C.dangerBg, label: 'D' },
  R: { color: C.info, bg: C.infoBg, label: 'R' },
  C: { color: C.info, bg: C.infoBg, label: 'C' },
};

function badgeEl(status: string, size = 16) {
  const b = BADGE[status] ?? { color: C.textMuted, bg: C.bgSelected, label: status };
  return (
    <span style={{ width: size, height: size, borderRadius: 4, fontSize: 9, fontWeight: 700, display: 'flex', alignItems: 'center', justifyContent: 'center', color: b.color, background: b.bg, flexShrink: 0 }}>{b.label}</span>
  );
}

// Верхняя папка пути — ключ группировки списка файлов
const topDir = (p: string) => {
  const i = p.indexOf('/');
  return i < 0 ? '/' : p.slice(0, i);
};

// Группы автосворачиваются на больших коммитах (кроме группы выбранного файла)
const AUTO_COLLAPSE_FROM = 20;

// Просмотр коммита: слева список файлов (поиск + группы по папкам), справа diff выбранного.
// Компоновка по макету дизайнера — ряд чипов не масштабировался на десятки файлов.
export function GitCommitView({ project, sha, onClose, isMobile = false }: {
  project: Project;
  sha: string;
  onClose: () => void;
  isMobile?: boolean;
}) {
  const [detail, setDetail] = useState<GitCommitDetail | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [activePath, setActivePath] = useState<string | null>(null);
  const [diff, setDiff] = useState<string | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [filter, setFilter] = useState('');
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  // Мобильная раскладка: список и diff — по очереди (list → item, как в «Знаниях»)
  const [mobileShowDiff, setMobileShowDiff] = useState(false);
  // Меню действий (⋯) + подтверждение отката коммита
  const [actionsMenu, setActionsMenu] = useState(false);
  const [revertConfirm, setRevertConfirm] = useState(false);
  const [revertError, setRevertError] = useState<string | null>(null);
  // Разворот длинного описания коммита (клэмп в шапке)
  const [bodyExpanded, setBodyExpanded] = useState(false);
  const bodyIsLong = !!detail?.body && (detail.body.length > 220 || detail.body.split('\n').length > (isMobile ? 2 : 4));
  const [reverting, setReverting] = useState(false);

  const handleRevert = async () => {
    setReverting(true);
    setRevertError(null);
    const err = await gitRevertCommit(project.id, sha);
    setReverting(false);
    if (err) setRevertError(err);
    else {
      // Успех: обратный коммит создан — закрываем просмотр, историю перечитает стор
      setRevertConfirm(false);
      onClose();
    }
  };

  useEffect(() => {
    setDetail(null); setNotFound(false); setActivePath(null); setDiff(null);
    setFilter(''); setMobileShowDiff(false); setBodyExpanded(false);
    let cancelled = false;
    api.git.commitDetail(project.id, sha)
      .then(d => {
        if (cancelled) return;
        setDetail(d);
        setActivePath(d.files[0]?.path ?? null);
        // Большой коммит — все группы, кроме первой, свёрнуты
        if (d.files.length >= AUTO_COLLAPSE_FROM) {
          const dirs = [...new Set(d.files.map(f => topDir(f.path)))];
          setCollapsed(new Set(dirs.slice(1)));
        } else {
          setCollapsed(new Set());
        }
      })
      .catch(() => { if (!cancelled) setNotFound(true); });
    return () => { cancelled = true; };
  }, [project.id, sha]);

  useEffect(() => {
    if (!activePath) { setDiff(null); return; }
    let cancelled = false;
    setDiffLoading(true);
    api.git.commitFileDiff(project.id, sha, activePath)
      .then(r => { if (!cancelled) setDiff(r.diff); })
      .catch(() => { if (!cancelled) setDiff(null); })
      .finally(() => { if (!cancelled) setDiffLoading(false); });
    return () => { cancelled = true; };
  }, [project.id, sha, activePath]);

  const dateStr = detail
    ? new Date(detail.date).toLocaleString('ru-RU', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' })
    : '';

  // Фильтр по подстроке пути + группировка по верхней папке
  const groups = useMemo(() => {
    if (!detail) return [];
    const q = filter.trim().toLowerCase();
    const files = q ? detail.files.filter(f => f.path.toLowerCase().includes(q)) : detail.files;
    const map = new Map<string, GitFileChange[]>();
    for (const f of files) {
      const dir = topDir(f.path);
      (map.get(dir) ?? map.set(dir, []).get(dir)!).push(f);
    }
    return [...map.entries()];
  }, [detail, filter]);

  const selectFile = (path: string) => {
    setActivePath(path);
    if (isMobile) setMobileShowDiff(true);
  };

  const fileRow = (f: GitFileChange) => {
    const name = f.path.split('/').pop() ?? f.path;
    const dir = f.path.includes('/') ? f.path.slice(0, f.path.lastIndexOf('/')) : '';
    const active = f.path === activePath;
    return (
      <div
        key={f.path}
        onClick={() => selectFile(f.path)}
        title={f.oldPath ? `${f.oldPath} → ${f.path}` : f.path}
        style={{
          display: 'flex', alignItems: 'center', gap: 8, padding: '5px 10px', cursor: 'pointer',
          borderRadius: R.md, background: active ? C.bgSelected : 'transparent',
        }}
      >
        {badgeEl(f.status)}
        <span style={{ minWidth: 0, display: 'flex', flexDirection: 'column' }}>
          <span style={{ fontFamily: FONT.mono, fontSize: 12.5, color: active ? C.textHeading : C.textPrimary, fontWeight: active ? 600 : 400, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{name}</span>
          {dir && <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{dir}</span>}
        </span>
      </div>
    );
  };

  const fileList = (
    <div style={{ width: isMobile ? '100%' : 260, flexShrink: 0, display: 'flex', flexDirection: 'column', minHeight: 0, borderRight: isMobile ? 'none' : `1px solid ${C.border}`, background: C.bgMain }}>
      {/* Поиск по файлам коммита */}
      <div style={{ padding: '8px 10px 6px', flexShrink: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '0 9px', height: 30 }}>
          <Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
          <input
            value={filter}
            onChange={e => setFilter(e.target.value)}
            placeholder="Файл или путь…"
            style={{ flex: 1, minWidth: 0, border: 'none', outline: 'none', background: 'transparent', fontSize: 12, fontFamily: FONT.mono, color: C.textHeading }}
          />
          {filter && (
            <button onClick={() => setFilter('')} style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 0, display: 'flex' }}>
              <X size={12} strokeWidth={ICON_STROKE} />
            </button>
          )}
        </div>
      </div>
      {/* Группы по верхней папке */}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '0 6px 10px' }}>
        {groups.length === 0 && (
          <div style={{ padding: '18px 10px', textAlign: 'center', color: C.textMuted, fontSize: 12, fontFamily: FONT.sans }}>
            {filter ? 'Ничего не найдено' : 'Нет файлов'}
          </div>
        )}
        {groups.map(([dir, files]) => {
          const isCollapsed = collapsed.has(dir) && !files.some(f => f.path === activePath);
          return (
            <div key={dir}>
              <div
                onClick={() => setCollapsed(prev => {
                  const next = new Set(prev);
                  if (next.has(dir)) next.delete(dir); else next.add(dir);
                  return next;
                })}
                style={{ display: 'flex', alignItems: 'center', gap: 4, padding: '7px 6px 3px', cursor: 'pointer', color: C.textSecondary }}
              >
                {isCollapsed ? <ChevronRight size={12} strokeWidth={2} /> : <ChevronDown size={12} strokeWidth={2} />}
                <span style={{ fontSize: 11, fontWeight: 700, fontFamily: FONT.sans, textTransform: 'uppercase', letterSpacing: 0.4 }}>{dir}</span>
                <span style={{ fontSize: 10.5, color: C.textMuted, fontFamily: FONT.mono }}>{files.length}</span>
              </div>
              {!isCollapsed && files.map(fileRow)}
            </div>
          );
        })}
      </div>
    </div>
  );

  const diffPane = (
    <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      {/* Sticky-шапка: путь + бейдж (+ «назад к списку» на мобиле) */}
      {activePath && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '7px 12px', borderBottom: `1px solid ${C.border}`, background: C.bgCard, flexShrink: 0 }}>
          {isMobile && (
            <button onClick={() => setMobileShowDiff(false)} style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.textSecondary, display: 'flex', padding: 2 }}>
              <ChevronRight size={15} strokeWidth={2} style={{ transform: 'rotate(180deg)' }} />
            </button>
          )}
          {badgeEl(detail?.files.find(f => f.path === activePath)?.status ?? 'M', 16)}
          <span style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textPrimary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{activePath}</span>
        </div>
      )}
      <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
        {diffLoading ? (
          <div style={{ padding: 24, color: C.textMuted, fontSize: 13, fontFamily: FONT.mono, textAlign: 'center' }}>Загрузка…</div>
        ) : diff ? (
          <DiffView diff={diff} />
        ) : (
          <div style={{ padding: 24, color: C.textMuted, fontSize: 12.5, fontFamily: FONT.sans, textAlign: 'center' }}>
            {notFound ? 'Не удалось загрузить коммит' : detail && detail.files.length === 0 ? 'Пустой коммит — нет изменённых файлов' : 'Diff недоступен (бинарный файл?)'}
          </div>
        )}
      </div>
    </div>
  );

  return (
    <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden', background: C.bgMain }}>
      {/* Шапка коммита */}
      <div style={{ padding: '12px 16px 10px', borderBottom: `1px solid ${C.border}`, background: C.bgCard, flexShrink: 0 }}>
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontFamily: FONT.serif, fontSize: 16.5, fontWeight: 700, color: C.textHeading, lineHeight: 1.35, overflowWrap: 'anywhere' }}>
              {detail?.subject ?? (notFound ? 'Коммит не найден' : 'Загрузка…')}
            </div>
            {detail && (
              <div style={{ marginTop: 5, display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap', fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans }}>
                <span style={{ fontFamily: FONT.mono, fontSize: 12, color: C.accent, background: C.accentLight, padding: '2px 8px', borderRadius: R.sm }}>{detail.shortSha}</span>
                <span>{detail.author}</span>
                <span style={{ color: C.textMuted }}>·</span>
                <span style={{ color: C.textMuted }}>{dateStr}</span>
                <span style={{ color: C.textMuted }}>·</span>
                <span style={{ color: C.textMuted }}>{detail.files.length} файлов</span>
              </div>
            )}
            {detail?.body && (
              <div style={{ marginTop: 7 }}>
                {/* Длинное описание не должно съедать контентную зону: свёрнуто — клэмп на
                    несколько строк, развёрнуто — ограниченная высота со своим скроллом */}
                <div style={bodyExpanded
                  ? { fontSize: 13, color: C.textPrimary, fontFamily: FONT.sans, whiteSpace: 'pre-wrap', lineHeight: 1.5, maxHeight: isMobile ? '22vh' : '28vh', overflowY: 'auto' }
                  : { fontSize: 13, color: C.textPrimary, fontFamily: FONT.sans, whiteSpace: 'pre-wrap', lineHeight: 1.5, display: '-webkit-box', WebkitLineClamp: isMobile ? 2 : 4, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>
                  {detail.body}
                </div>
                {bodyIsLong && (
                  <button
                    onClick={() => setBodyExpanded(v => !v)}
                    style={{ marginTop: 3, padding: 0, border: 'none', background: 'none', cursor: 'pointer', fontSize: 12, color: C.accent, fontFamily: FONT.sans }}
                  >
                    {bodyExpanded ? 'Свернуть' : 'Показать полностью'}
                  </button>
                )}
              </div>
            )}
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 2, flexShrink: 0 }}>
            <div style={{ position: 'relative' }}>
              <IconButton size="md" active={actionsMenu} onClick={() => setActionsMenu(v => !v)} title="Действия">
                <MoreHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              </IconButton>
              {actionsMenu && (
                <Menu onClose={() => setActionsMenu(false)} align="right" top={36} minWidth={230}>
                  <MenuItem
                    icon={<Undo2 size={15} strokeWidth={ICON_STROKE} />}
                    label="Откатить коммит (revert)"
                    onClick={() => { setActionsMenu(false); setRevertError(null); setRevertConfirm(true); }}
                  />
                </Menu>
              )}
            </div>
            <IconButton size="md" onClick={onClose} title="Закрыть">
              <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
        </div>
      </div>

      {/* === Подтверждение отката коммита (revert) === */}
      {revertConfirm && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => { if (!reverting) setRevertConfirm(false); }}
          title="Откатить коммит"
          subtitle={
            <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>
              {detail?.shortSha ?? sha.slice(0, 7)}{detail?.subject ? ` — ${detail.subject}` : ''}
            </span>
          }
          footer={
            <ModalActions
              confirmLabel={reverting ? 'Откатываю…' : 'Откатить'}
              confirmDisabled={reverting}
              onConfirm={handleRevert}
              onCancel={() => setRevertConfirm(false)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Будет создан обратный коммит с противоположными изменениями — история не переписывается.
          </div>
          {revertError && (
            <div style={{ marginTop: 10, fontSize: 12.5, color: C.dangerText, fontFamily: FONT.sans, lineHeight: 1.45 }}>
              {revertError}
            </div>
          )}
        </Modal>
      )}

      {/* Тело: список файлов + diff (на мобиле — по очереди) */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex' }}>
        {isMobile ? (mobileShowDiff ? diffPane : fileList) : (<>{fileList}{diffPane}</>)}
      </div>
    </div>
  );
}
