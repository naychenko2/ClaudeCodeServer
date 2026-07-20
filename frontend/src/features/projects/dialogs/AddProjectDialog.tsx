import { useState } from 'react';
import type { Project, ProjectGroup } from '../../../types';
import { api } from '../../../lib/api';
import { C, FONT, R, MODAL_W } from '../../../lib/design';
import { Modal, ModalActions, TextField, Field, SegmentedControl, Toggle } from '../../../components/ui';
import { GroupSelect } from '../GroupSelect';
import { SyncToggleRow } from '../components/SyncToggleRow';

interface Props {
  groups: ProjectGroup[];
  defaultGroupId?: string;
  onSuccess: (project: Project) => void;
  onClose: () => void;
}

type Mode = 'new' | 'existing';
type GitMode = 'none' | 'manual' | 'auto';

const GIT_MODES: { value: GitMode; label: string; hint: string }[] = [
  { value: 'none', label: 'Без ведения истории', hint: 'Обычная папка — версии файлов не сохраняются' },
  { value: 'manual', label: 'Ручное ведение истории', hint: 'Версии сохраняются, когда вы сами нажмёте «Зафиксировать» в разделе «Файлы». Рекомендуется для разработки кода' },
  { value: 'auto', label: 'Автоматическое ведение истории', hint: 'Каждый ход ИИ сохраняется в историю сам. Рекомендуется для работы с документами' },
];

// Единый диалог добавления проекта: сегмент «Новый / Существующий».
//  • Новый — создаём новую папку под путём по умолчанию (path = null).
//  • Существующий — привязываем существующую папку по пути.
export function AddProjectDialog({ groups, defaultGroupId, onSuccess, onClose }: Props) {
  const [mode, setMode] = useState<Mode>('new');
  const [name, setName] = useState('');
  const [path, setPath] = useState('');
  const [groupId, setGroupId] = useState(defaultGroupId ?? '');
  const [sync, setSync] = useState(false);
  const [gitMode, setGitMode] = useState<GitMode>('none');
  const [gitPush, setGitPush] = useState(false);
  const [error, setError] = useState('');

  const handleConfirm = async () => {
    setError('');
    try {
      const rootPath = mode === 'existing' ? (path.trim() || null) : null;
      const p = await api.projects.create(name.trim(), rootPath, false, groupId || null, {
        enableGit: gitMode !== 'none',
        gitAutoCommit: gitMode === 'auto',
        gitAutoPush: gitMode === 'auto' && gitPush,
      });
      if (sync) api.sync.add(p.id, '', true).catch(() => {});
      onSuccess(p);
    } catch (e: any) {
      setError(e.message);
    }
  };

  return (
    <Modal
      title="Добавить проект"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={mode === 'existing' ? 'Добавить' : 'Создать'}
          confirmDisabled={!name.trim() || (mode === 'existing' && !path.trim())}
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}

      <SegmentedControl<Mode>
        value={mode}
        onChange={setMode}
        options={[{ value: 'new', label: 'Новый' }, { value: 'existing', label: 'Существующий' }]}
      />

      <TextField value={name} onChange={setName} placeholder="Название" autoFocus />

      {mode === 'existing' && (
        <Field label="Путь к папке" hint="Абсолютный путь к существующей папке проекта">
          <TextField value={path} onChange={setPath} placeholder="C:\\Sources\\my-project" mono />
        </Field>
      )}

      {groups.length > 0 && (
        <Field label="Группа">
          <GroupSelect groups={groups} value={groupId} onChange={setGroupId} />
        </Field>
      )}

      {/* Ведение истории файлов (git): без истории / ручной (код) / авто (документы) */}
      <Field label="История файлов (Git)">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {GIT_MODES.map(m => {
            const active = gitMode === m.value;
            return (
              <div
                key={m.value}
                onClick={() => setGitMode(m.value)}
                title={m.hint}
                style={{
                  display: 'flex', alignItems: 'flex-start', gap: 9, padding: '8px 11px', cursor: 'pointer',
                  borderRadius: R.lg, border: `1px solid ${active ? C.accent : C.border}`,
                  background: active ? C.accentLight : C.bgWhite,
                }}
              >
                <span style={{
                  width: 14, height: 14, borderRadius: '50%', marginTop: 2, flexShrink: 0,
                  border: `1.5px solid ${active ? C.accent : C.dashed}`,
                  background: active ? C.accent : 'transparent',
                  boxShadow: active ? `inset 0 0 0 2.5px ${C.bgWhite}` : 'none',
                }} />
                <span style={{ display: 'flex', flexDirection: 'column', gap: 1, minWidth: 0 }}>
                  <span style={{ fontSize: 13, fontFamily: FONT.sans, fontWeight: 600, color: active ? C.textHeading : C.textPrimary }}>{m.label}</span>
                  <span style={{ fontSize: 11.5, fontFamily: FONT.sans, color: C.textSecondary, lineHeight: 1.35 }}>{m.hint}</span>
                </span>
              </div>
            );
          })}
          {gitMode === 'auto' && (
            <div
              onClick={() => setGitPush(v => !v)}
              style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '2px 11px 0 34px', cursor: 'pointer' }}
            >
              <Toggle checked={gitPush} onChange={setGitPush} />
              <span style={{ fontSize: 12.5, fontFamily: FONT.sans, color: C.textPrimary }}>Ещё и отправлять копию на git-сервер (push)</span>
            </div>
          )}
        </div>
      </Field>

      <SyncToggleRow enabled={sync} onChange={setSync} />
    </Modal>
  );
}
