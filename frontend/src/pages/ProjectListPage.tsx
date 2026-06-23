import { useEffect, useState } from 'react';
import type { Project } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { ProjectSyncToggle } from '../components/ProjectSyncToggle';
import { ConnectionStatus } from '../components/ConnectionStatus';
import { toggleSyncMark } from '../lib/sync';
import { C, R, FONT, SHADOW, MODAL_W } from '../lib/design';
import { Modal, ModalActions, TextField, Toggle } from '../components/ui';

interface Props {
  onOpen: (project: Project) => void;
  onLogout: () => void;
}

// Склонение: «1 чат», «3 чата», «5 чатов»
function sessionsLabel(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  let w = 'чатов';
  if (m10 === 1 && m100 !== 11) w = 'чат';
  else if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) w = 'чата';
  return `${n} ${w}`;
}

// Цветовые плитки для карточек проектов
const TILE_COLORS: [string, string][] = [
  ['#E7F0E8', '#3F7A4F'],
  ['#E6EEF5', '#3E7CA6'],
  ['#FBEBE0', '#C2693B'],
  ['#F2E6F0', '#8E4A82'],
];

export function ProjectListPage({ onOpen, onLogout }: Props) {
  const online = useOnline();
  const [projects, setProjects] = useState<Project[]>([]);
  const [search, setSearch] = useState('');
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState('');
  const [newPath, setNewPath] = useState('');
  const [newSync, setNewSync] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<Project | null>(null);
  const [editTarget, setEditTarget] = useState<Project | null>(null);
  const [editName, setEditName] = useState('');
  const [editPath, setEditPath] = useState('');
  const [error, setError] = useState('');

  const serverUrl = localStorage.getItem('cc_server_url') ?? '';

  useEffect(() => { api.projects.list().then(setProjects).catch(() => {}); }, []);

  const filtered = projects.filter(p =>
    p.name.toLowerCase().includes(search.toLowerCase()) ||
    p.rootPath.toLowerCase().includes(search.toLowerCase())
  );

  const handleCreate = async () => {
    setError('');
    try {
      const p = await api.projects.create(newName.trim(), newPath.trim());
      setProjects(prev => [...prev, p]);
      // Включаем синхронизацию всего проекта сразу при создании, если выбрано
      if (newSync) toggleSyncMark(p.id, { name: '', path: '', isDirectory: true, modified: '', isModified: false });
      setShowCreate(false);
      setNewName('');
      setNewPath('');
      setNewSync(false);
    } catch (e: any) {
      setError(e.message);
    }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await api.projects.delete(deleteTarget.id);
    setProjects(prev => prev.filter(p => p.id !== deleteTarget.id));
    setDeleteTarget(null);
  };

  const openEdit = (p: Project, e: React.MouseEvent) => {
    e.stopPropagation();
    setEditTarget(p);
    setEditName(p.name);
    setEditPath(p.rootPath);
    setError('');
  };

  const handleEdit = async () => {
    if (!editTarget) return;
    setError('');
    try {
      const updated = await api.projects.update(editTarget.id, { name: editName.trim(), rootPath: editPath.trim() });
      setProjects(prev => prev.map(p => p.id === updated.id ? updated : p));
      setEditTarget(null);
    } catch (e: any) {
      setError(e.message);
    }
  };

  const errorLine = error ? <div style={{ color: C.danger, fontSize: 13 }}>{error}</div> : null;

  return (
    <div style={{ minHeight: '100vh', background: C.bgMain, fontFamily: FONT.sans, padding: '4px 22px 14px' }}>
      <div style={{ maxWidth: 640, margin: '0 auto' }}>

        {/* Шапка */}
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, marginBottom: 20, paddingTop: 20 }}>
          <h1 style={{
            fontFamily: FONT.serif, fontSize: 30, fontWeight: 500, margin: 0,
            letterSpacing: '-0.01em', color: C.textHeading, flexShrink: 0,
          }}>
            Проекты
          </h1>

          {/* Badge с аватаром — сжимается, текст внутри обрезается многоточием */}
          <div
            onClick={onLogout}
            style={{
              display: 'flex', alignItems: 'center', gap: 7, background: C.bgPanel,
              borderRadius: 20, padding: '6px 12px 6px 8px', cursor: 'pointer',
              minWidth: 0, flexShrink: 1, maxWidth: 260, overflow: 'hidden',
            }}
          >
            <div style={{
              width: 22, height: 22, borderRadius: '50%', background: C.accent,
              color: C.onAccent, fontSize: 11, fontWeight: 700,
              display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
            }}>
              AD
            </div>
            <ConnectionStatus variant="badge" label={serverUrl || 'localhost'} />
          </div>
        </div>

        {/* Поиск */}
        <div style={{
          display: 'flex', alignItems: 'center', background: C.bgWhite,
          border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '0 13px', height: 44, marginBottom: 16,
        }}>
          <span style={{ color: C.textMuted, marginRight: 8, display: 'flex', alignItems: 'center' }}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <circle cx="11" cy="11" r="7"/>
              <path d="m21 21-4.3-4.3"/>
            </svg>
          </span>
          <input
            placeholder="Поиск проектов…"
            value={search}
            onChange={e => setSearch(e.target.value)}
            style={{ border: 'none', background: 'none', flex: 1, fontSize: 14.5, color: C.textHeading, fontFamily: 'inherit', outline: 'none' }}
          />
        </div>

        {/* Список проектов */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
          {filtered.map((p, index) => {
            const [tileBg, tileFg] = TILE_COLORS[index % TILE_COLORS.length];
            const letter = p.name.charAt(0).toUpperCase() || '?';
            return (
              <div
                key={p.id}
                onClick={() => onOpen(p)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 14, background: C.bgWhite,
                  border: `1px solid ${C.borderLight}`, borderRadius: 16, padding: 14,
                  cursor: 'pointer', boxShadow: SHADOW.card,
                }}
              >
                {/* Цветная плитка с буквой */}
                <div style={{
                  width: 50, height: 50, borderRadius: R.xxl, background: tileBg, color: tileFg,
                  fontFamily: FONT.serif, fontSize: 22, fontWeight: 600,
                  display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
                }}>
                  {letter}
                </div>

                {/* Текстовая часть */}
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 16, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {p.name}
                  </div>
                  <div style={{ fontFamily: FONT.mono, fontSize: 11.5, color: C.textMuted, margin: '3px 0 6px', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {p.rootPath}
                  </div>
                  {/* Число чатов + дата (MA13) */}
                  <div style={{ fontSize: 12, color: C.textSecondary, display: 'flex', alignItems: 'center', gap: 7 }}>
                    <span>{sessionsLabel(p.sessionCount ?? 0)}</span>
                    <span style={{ color: C.border }}>·</span>
                    <span style={{ color: C.textMuted }}>
                      {new Date(p.updatedAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })}
                    </span>
                  </div>
                </div>

                {/* Кнопки действий — только онлайн */}
                {online && (
                <div style={{ display: 'flex', gap: 4, flexShrink: 0 }} onClick={e => e.stopPropagation()}>
                  <button onClick={e => openEdit(p, e)} title="Редактировать" style={cardIconBtn}>
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
                      <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
                    </svg>
                  </button>
                  <button onClick={e => { e.stopPropagation(); setDeleteTarget(p); }} title="Удалить" style={cardIconBtn}>
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <polyline points="3 6 5 6 21 6"/>
                      <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>
                      <path d="M10 11v6"/>
                      <path d="M14 11v6"/>
                      <path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>
                    </svg>
                  </button>
                </div>
                )}
              </div>
            );
          })}

          {/* Кнопка добавления — всегда в конце списка, только онлайн */}
          {online && (
          <div
            onClick={() => setShowCreate(true)}
            style={{
              border: `1.5px dashed ${C.dashed}`, borderRadius: 16, padding: 15,
              display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
              color: C.accent, fontSize: 14.5, fontWeight: 600, cursor: 'pointer',
            }}
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
              <line x1="12" y1="5" x2="12" y2="19"/>
              <line x1="5" y1="12" x2="19" y2="12"/>
            </svg>
            Добавить проект
          </div>
          )}
        </div>

        {/* Empty state — только если нет проектов вообще */}
        {filtered.length === 0 && search === '' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0', color: C.textMuted, fontSize: 14 }}>
            Нет проектов. Создайте первый выше.
          </div>
        )}
        {filtered.length === 0 && search !== '' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0', color: C.textMuted, fontSize: 14 }}>
            Ничего не найдено по запросу «{search}»
          </div>
        )}
      </div>

      {/* Диалог создания */}
      {showCreate && (
        <Modal
          title="Новый проект"
          width={MODAL_W.form}
          onClose={() => { setShowCreate(false); setError(''); setNewSync(false); }}
          footer={
            <ModalActions
              confirmLabel="Добавить"
              onConfirm={handleCreate}
              onCancel={() => { setShowCreate(false); setError(''); setNewSync(false); }}
            />
          }
        >
          {errorLine}
          <TextField value={newName} onChange={setNewName} placeholder="Название" />
          <TextField value={newPath} onChange={setNewPath} placeholder="Путь к папке" mono />
          {/* Включить синхронизацию всего проекта сразу при создании */}
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, padding: '10px 12px', background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl }}>
            <div style={{ minWidth: 0 }}>
              <div style={{ fontSize: 14, fontWeight: 600, color: C.textHeading }}>Синхронизировать для офлайна</div>
              <div style={{ fontSize: 12, color: C.textMuted, marginTop: 2 }}>Скачать все файлы проекта сразу после создания</div>
            </div>
            <Toggle checked={newSync} onChange={setNewSync} width={44} height={26} />
          </div>
        </Modal>
      )}

      {/* Диалог редактирования */}
      {editTarget && (
        <Modal
          title="Редактировать проект"
          width={MODAL_W.form}
          onClose={() => { setEditTarget(null); setError(''); }}
          footer={
            <ModalActions
              confirmLabel="Сохранить"
              onConfirm={handleEdit}
              onCancel={() => { setEditTarget(null); setError(''); }}
            />
          }
        >
          {errorLine}
          <TextField value={editName} onChange={setEditName} placeholder="Название" />
          <TextField value={editPath} onChange={setEditPath} placeholder="Путь к папке" mono />
          <ProjectSyncToggle projectId={editTarget.id} online={online} />
        </Modal>
      )}

      {/* Диалог удаления */}
      {deleteTarget && (
        <Modal
          title="Удалить проект?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteTarget(null)}
          subtitle={
            <>
              Проект «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{deleteTarget.name}</strong>» будет удалён без возможности восстановления. Файлы на диске не затрагиваются.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={handleDelete}
              onCancel={() => setDeleteTarget(null)}
            />
          }
        />
      )}
    </div>
  );
}

// Иконка-кнопка действия в карточке проекта
const cardIconBtn: React.CSSProperties = {
  width: 26, height: 26, borderRadius: R.sm, display: 'flex', alignItems: 'center', justifyContent: 'center',
  color: C.textMuted, background: 'none', border: 'none', cursor: 'pointer', flexShrink: 0,
};
