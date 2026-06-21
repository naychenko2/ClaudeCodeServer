import { useEffect, useState } from 'react';
import type { Project } from '../types';
import { api } from '../lib/api';

interface Props {
  onOpen: (project: Project) => void;
  onLogout: () => void;
}

// Цветовые плитки для карточек проектов
const TILE_COLORS: [string, string][] = [
  ['#E7F0E8', '#3F7A4F'],
  ['#E6EEF5', '#3E7CA6'],
  ['#FBEBE0', '#C2693B'],
  ['#F2E6F0', '#8E4A82'],
];

export function ProjectListPage({ onOpen, onLogout }: Props) {
  const [projects, setProjects] = useState<Project[]>([]);
  const [search, setSearch] = useState('');
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState('');
  const [newPath, setNewPath] = useState('');
  const [deleteTarget, setDeleteTarget] = useState<Project | null>(null);
  const [editTarget, setEditTarget] = useState<Project | null>(null);
  const [editName, setEditName] = useState('');
  const [editPath, setEditPath] = useState('');
  const [error, setError] = useState('');

  const serverUrl = localStorage.getItem('cc_server_url') ?? '';

  useEffect(() => { api.projects.list().then(setProjects); }, []);

  const filtered = projects.filter(p =>
    p.name.toLowerCase().includes(search.toLowerCase()) ||
    p.rootPath.toLowerCase().includes(search.toLowerCase())
  );

  const handleCreate = async () => {
    setError('');
    try {
      const p = await api.projects.create(newName.trim(), newPath.trim());
      setProjects(prev => [...prev, p]);
      setShowCreate(false);
      setNewName('');
      setNewPath('');
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

  // Стиль input для диалогов
  const dialogInput: React.CSSProperties = {
    width: '100%',
    height: 48,
    border: '1px solid #E0D7C8',
    borderRadius: 12,
    background: '#FFFFFF',
    padding: '0 14px',
    fontSize: 15,
    fontFamily: "'JetBrains Mono', monospace",
    marginBottom: 10,
    boxSizing: 'border-box',
    outline: 'none',
    color: '#2A251F',
  };

  const btnCancel: React.CSSProperties = {
    flex: 1,
    background: '#EDE7DC',
    color: '#756B5E',
    borderRadius: 13,
    padding: 14,
    border: 'none',
    cursor: 'pointer',
    fontSize: 14,
    fontWeight: 600,
    fontFamily: "'Hanken Grotesk', sans-serif",
  };

  const btnAccent: React.CSSProperties = {
    flex: 1,
    background: '#D97757',
    color: '#FBF8F2',
    borderRadius: 13,
    padding: 14,
    border: 'none',
    cursor: 'pointer',
    fontSize: 14,
    fontWeight: 600,
    fontFamily: "'Hanken Grotesk', sans-serif",
  };

  const btnDanger: React.CSSProperties = {
    flex: 1,
    background: '#B4452F',
    color: '#FBF8F2',
    borderRadius: 13,
    padding: 14,
    border: 'none',
    cursor: 'pointer',
    fontSize: 14,
    fontWeight: 600,
    fontFamily: "'Hanken Grotesk', sans-serif",
  };

  const overlayStyle: React.CSSProperties = {
    position: 'fixed',
    inset: 0,
    background: 'rgba(23,19,15,0.42)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  };

  const modalStyle: React.CSSProperties = {
    background: '#F4F0E8',
    borderRadius: 20,
    padding: 24,
    width: 400,
    boxShadow: '0 24px 60px rgba(23,19,15,0.4)',
  };

  return (
    <div style={{ minHeight: '100vh', background: '#F4F0E8', fontFamily: "'Hanken Grotesk', sans-serif", padding: '4px 22px 14px' }}>
      <div style={{ maxWidth: 640, margin: '0 auto' }}>

        {/* Шапка */}
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 20, paddingTop: 20 }}>
          <h1 style={{
            fontFamily: "'PT Serif', serif",
            fontSize: 30,
            fontWeight: 500,
            margin: 0,
            letterSpacing: '-0.01em',
            color: '#2A251F',
          }}>
            Проекты
          </h1>

          {/* Badge с аватаром */}
          <div
            onClick={onLogout}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 7,
              background: '#EDE7DC',
              borderRadius: 20,
              padding: '6px 12px 6px 8px',
              cursor: 'pointer',
            }}
          >
            <div style={{
              width: 22,
              height: 22,
              borderRadius: '50%',
              background: '#D97757',
              color: '#FBF8F2',
              fontSize: 11,
              fontWeight: 700,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              flexShrink: 0,
            }}>
              AD
            </div>
            <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 11, color: '#756B5E' }}>
              {serverUrl || 'localhost'}
            </span>
          </div>
        </div>

        {/* Поиск */}
        <div style={{
          display: 'flex',
          alignItems: 'center',
          background: '#FFFFFF',
          border: '1px solid #E0D7C8',
          borderRadius: 12,
          padding: '0 13px',
          height: 44,
          marginBottom: 16,
        }}>
          <span style={{ color: '#9A8F7E', marginRight: 8, display: 'flex', alignItems: 'center' }}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <circle cx="11" cy="11" r="7"/>
              <path d="m21 21-4.3-4.3"/>
            </svg>
          </span>
          <input
            placeholder="Поиск проектов…"
            value={search}
            onChange={e => setSearch(e.target.value)}
            style={{
              border: 'none',
              background: 'none',
              flex: 1,
              fontSize: 14.5,
              color: '#9A8F7E',
              fontFamily: 'inherit',
              outline: 'none',
            }}
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
                  display: 'flex',
                  alignItems: 'center',
                  gap: 14,
                  background: '#FFFFFF',
                  border: '1px solid #E8E1D4',
                  borderRadius: 16,
                  padding: 14,
                  cursor: 'pointer',
                  boxShadow: '0 2px 8px rgba(60,50,35,0.04)',
                }}
              >
                {/* Цветная плитка с буквой */}
                <div style={{
                  width: 50,
                  height: 50,
                  borderRadius: 14,
                  background: tileBg,
                  color: tileFg,
                  fontFamily: "'PT Serif', serif",
                  fontSize: 22,
                  fontWeight: 600,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  flexShrink: 0,
                }}>
                  {letter}
                </div>

                {/* Текстовая часть */}
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{
                    fontSize: 16,
                    fontWeight: 600,
                    color: '#2A251F',
                    whiteSpace: 'nowrap',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                  }}>
                    {p.name}
                  </div>
                  <div style={{
                    fontFamily: "'JetBrains Mono', monospace",
                    fontSize: 11.5,
                    color: '#9A8F7E',
                    margin: '3px 0 6px',
                    whiteSpace: 'nowrap',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                  }}>
                    {p.rootPath}
                  </div>
                  <div style={{ fontSize: 12, color: '#756B5E' }}>
                    {new Date(p.updatedAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })}
                  </div>
                </div>

                {/* Кнопки действий */}
                <div style={{ display: 'flex', gap: 4, flexShrink: 0 }} onClick={e => e.stopPropagation()}>
                  {/* Редактировать */}
                  <button
                    onClick={e => openEdit(p, e)}
                    title="Редактировать"
                    style={{
                      width: 26,
                      height: 26,
                      borderRadius: 7,
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      color: '#B0A697',
                      background: 'none',
                      border: 'none',
                      cursor: 'pointer',
                      flexShrink: 0,
                    }}
                  >
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
                      <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
                    </svg>
                  </button>
                  {/* Удалить */}
                  <button
                    onClick={e => { e.stopPropagation(); setDeleteTarget(p); }}
                    title="Удалить"
                    style={{
                      width: 26,
                      height: 26,
                      borderRadius: 7,
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      color: '#B0A697',
                      background: 'none',
                      border: 'none',
                      cursor: 'pointer',
                      flexShrink: 0,
                    }}
                  >
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <polyline points="3 6 5 6 21 6"/>
                      <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>
                      <path d="M10 11v6"/>
                      <path d="M14 11v6"/>
                      <path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>
                    </svg>
                  </button>
                </div>
              </div>
            );
          })}

          {/* Кнопка добавления — всегда в конце списка */}
          <div
            onClick={() => setShowCreate(true)}
            style={{
              border: '1.5px dashed #D0C6B4',
              borderRadius: 16,
              padding: 15,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              gap: 8,
              color: '#BE5536',
              fontSize: 14.5,
              fontWeight: 600,
              cursor: 'pointer',
            }}
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
              <line x1="12" y1="5" x2="12" y2="19"/>
              <line x1="5" y1="12" x2="19" y2="12"/>
            </svg>
            Добавить проект
          </div>
        </div>

        {/* Empty state — только если нет проектов вообще */}
        {filtered.length === 0 && search === '' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0', color: '#9A8F7E', fontSize: 14 }}>
            Нет проектов. Создайте первый выше.
          </div>
        )}
        {filtered.length === 0 && search !== '' && (
          <div style={{ textAlign: 'center', padding: '48px 0 0', color: '#9A8F7E', fontSize: 14 }}>
            Ничего не найдено по запросу «{search}»
          </div>
        )}
      </div>

      {/* Диалог создания */}
      {showCreate && (
        <div style={overlayStyle}>
          <div style={modalStyle}>
            <div style={{ width: 40, height: 4, borderRadius: 2, background: '#D0C6B4', margin: '0 auto 18px' }} />
            <h2 style={{ fontFamily: "'PT Serif', serif", fontSize: 24, fontWeight: 500, margin: '0 0 16px', color: '#2A251F' }}>
              Новый проект
            </h2>
            {error && <div style={{ color: '#B4452F', fontSize: 13, marginBottom: 12 }}>{error}</div>}
            <input
              placeholder="Название"
              value={newName}
              onChange={e => setNewName(e.target.value)}
              style={dialogInput}
            />
            <input
              placeholder="Путь к папке"
              value={newPath}
              onChange={e => setNewPath(e.target.value)}
              style={{ ...dialogInput, marginBottom: 18 }}
            />
            <div style={{ display: 'flex', gap: 10 }}>
              <button onClick={() => { setShowCreate(false); setError(''); }} style={btnCancel}>Отмена</button>
              <button onClick={handleCreate} style={btnAccent}>Добавить</button>
            </div>
          </div>
        </div>
      )}

      {/* Диалог редактирования */}
      {editTarget && (
        <div style={overlayStyle}>
          <div style={modalStyle}>
            <div style={{ width: 40, height: 4, borderRadius: 2, background: '#D0C6B4', margin: '0 auto 18px' }} />
            <h2 style={{ fontFamily: "'PT Serif', serif", fontSize: 24, fontWeight: 500, margin: '0 0 16px', color: '#2A251F' }}>
              Редактировать проект
            </h2>
            {error && <div style={{ color: '#B4452F', fontSize: 13, marginBottom: 12 }}>{error}</div>}
            <input
              placeholder="Название"
              value={editName}
              onChange={e => setEditName(e.target.value)}
              style={dialogInput}
            />
            <input
              placeholder="Путь к папке"
              value={editPath}
              onChange={e => setEditPath(e.target.value)}
              style={{ ...dialogInput, marginBottom: 18 }}
            />
            <div style={{ display: 'flex', gap: 10 }}>
              <button onClick={() => { setEditTarget(null); setError(''); }} style={btnCancel}>Отмена</button>
              <button onClick={handleEdit} style={btnAccent}>Сохранить</button>
            </div>
          </div>
        </div>
      )}

      {/* Диалог удаления */}
      {deleteTarget && (
        <div style={overlayStyle}>
          <div style={{ ...modalStyle, width: 380 }}>
            <div style={{ width: 40, height: 4, borderRadius: 2, background: '#D0C6B4', margin: '0 auto 18px' }} />
            <h2 style={{ fontFamily: "'PT Serif', serif", fontSize: 24, fontWeight: 500, margin: '0 0 8px', color: '#2A251F' }}>
              Удалить проект?
            </h2>
            <p style={{ fontSize: 14, color: '#756B5E', margin: '0 0 20px' }}>
              «{deleteTarget.name}» будет удалён. Это действие нельзя отменить.
            </p>
            <div style={{ display: 'flex', gap: 10 }}>
              <button onClick={() => setDeleteTarget(null)} style={btnCancel}>Отмена</button>
              <button onClick={handleDelete} style={btnDanger}>Удалить</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
