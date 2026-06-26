import { useState } from 'react';
import type { Project } from '../../../types';
import { api } from '../../../lib/api';
import { useOnline } from '../../../hooks/useOnline';
import { C, R } from '../../../lib/design';
import { Modal, ModalActions, TextField, TextArea } from '../../../components/ui';
import { ProjectSyncToggle } from '../../../components/ProjectSyncToggle';

interface Props {
  project: Project;
  onSuccess: (updated: Project) => void;
  onClose: () => void;
}

type View = 'main' | 'prompt';

export function EditDialog({ project, onSuccess, onClose }: Props) {
  const online = useOnline();
  const [view, setView] = useState<View>('main');
  const [name, setName] = useState(project.name);
  const [path, setPath] = useState(project.rootPath);
  const [systemPrompt, setSystemPrompt] = useState(project.systemPrompt ?? '');
  const [draftPrompt, setDraftPrompt] = useState('');
  const builtinPrompt = project.builtInSystemPrompt ?? '';
  const [error, setError] = useState('');

  const handleConfirm = async () => {
    setError('');
    try {
      const updated = await api.projects.update(project.id, {
        name: name.trim(),
        rootPath: path.trim(),
        systemPrompt,
      });
      onSuccess(updated);
    } catch (e: any) {
      setError(e.message);
    }
  };

  const handleEditPrompt = () => {
    setDraftPrompt(systemPrompt);
    setView('prompt');
  };

  if (view === 'prompt') {
    return (
      <Modal
        title="Системный промпт"
        width={620}
        onClose={() => setView('main')}
        footer={
          <ModalActions
            confirmLabel="Применить"
            onConfirm={() => { setSystemPrompt(draftPrompt); setView('main'); }}
            onCancel={() => setView('main')}
          />
        }
      >
        {/* Фиксированная часть — read-only плашка */}
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: 10,
          padding: '12px 14px',
          background: C.bgPanel,
          border: `1px dashed ${C.border}`,
          borderRadius: R.xl,
        }}>
          <span style={{ fontSize: 14, lineHeight: 1, marginTop: 1, flexShrink: 0 }}>🔒</span>
          <div style={{
            fontFamily: 'JetBrains Mono, monospace',
            fontSize: 12,
            color: 'rgba(60, 45, 30, 0.55)',
            lineHeight: 1.6,
            whiteSpace: 'pre-wrap',
            maxHeight: 120,
            overflowY: 'auto',
          }}>
            {builtinPrompt}
          </div>
        </div>

        {/* Разделитель */}
        <div style={{ borderBottom: `1px dashed ${C.border}`, margin: '2px 0' }} />

        {/* Пользовательская часть */}
        <div>
          <div style={{
            fontSize: 12, fontWeight: 600, color: C.textSecondary,
            fontFamily: 'Hanken Grotesk, sans-serif',
            textTransform: 'uppercase', letterSpacing: '0.05em',
            marginBottom: 6,
          }}>
            Ваши инструкции
          </div>
          <TextArea
            value={draftPrompt}
            onChange={setDraftPrompt}
            placeholder="Контекст проекта, правила, предпочтения…"
            minHeight={160}
            style={{ maxHeight: 320 }}
          />
        </div>
      </Modal>
    );
  }

  return (
    <Modal
      title="Редактировать проект"
      width={480}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Сохранить"
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <TextField value={name} onChange={setName} placeholder="Название" />
      <TextField value={path} onChange={setPath} placeholder="Путь к папке" mono />
      <hr style={{ border: 'none', borderTop: `1px solid ${C.border}`, margin: 0 }} />
      <div style={{
        display: 'flex', alignItems: 'center', gap: 12,
        padding: '12px 14px', background: C.bgWhite,
        border: `1px solid ${C.border}`, borderRadius: R.xl,
      }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: 3 }}>
            Системный промпт
          </div>
          <div style={{ fontSize: 13, color: systemPrompt ? C.textHeading : C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {systemPrompt || 'Не задан'}
          </div>
        </div>
        <button
          onClick={handleEditPrompt}
          style={{
            flexShrink: 0, padding: '6px 14px', background: C.accent, color: '#fff',
            border: 'none', borderRadius: R.xl, fontSize: 13, cursor: 'pointer', fontFamily: 'inherit',
          }}
        >
          Редактировать
        </button>
      </div>
      <ProjectSyncToggle projectId={project.id} online={online} />
    </Modal>
  );
}
