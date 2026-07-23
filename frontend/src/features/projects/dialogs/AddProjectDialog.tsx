import { useState } from 'react';
import type { Project, ProjectGroup } from '../../../types';
import { api } from '../../../lib/api';
import { C, MODAL_W } from '../../../lib/design';
import { Modal, ModalActions, TextField, Field, SegmentedControl } from '../../../components/ui';
import { AGENT_COLORS, agentDotColor } from '../../../components/AgentSelector';
import { GroupSelect } from '../GroupSelect';
import { SyncToggleRow } from '../components/SyncToggleRow';
import { GIT_MODES, GitModeCard, GitPushRow, type GitMode } from '../components/GitModeCards';
import { invalidateProjectsCache } from '../useAllProjects';

interface Props {
  groups: ProjectGroup[];
  defaultGroupId?: string;
  onSuccess: (project: Project) => void;
  onClose: () => void;
}

type Mode = 'new' | 'existing';

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
  const [color, setColor] = useState<string | null>(null);
  const [error, setError] = useState('');

  const handleConfirm = async () => {
    setError('');
    try {
      const rootPath = mode === 'existing' ? (path.trim() || null) : null;
      const p = await api.projects.create(name.trim(), rootPath, false, groupId || null, {
        enableGit: gitMode !== 'none',
        gitAutoCommit: gitMode === 'auto',
        gitAutoPush: gitMode === 'auto' && gitPush,
      }, color);
      if (sync) api.sync.add(p.id, '', true).catch(() => {});
      invalidateProjectsCache(); // полка/палитра проектов видят новый проект сразу
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

      {/* Цвет иконки: инициалы на цветном фоне. Картинку/генерацию задают уже после создания
          (в «Редактировать проект» — там есть id). Не выбран → авто-цвет по проекту. */}
      <Field label="Цвет иконки">
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          <span style={{
            width: 26, height: 26, borderRadius: 7, flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            background: color ? agentDotColor(color) : C.textMuted, color: '#fff',
            fontSize: 11, fontWeight: 700,
          }}>
            {(name.trim().slice(0, 2) || '?').toUpperCase()}
          </span>
          {Object.keys(AGENT_COLORS).map(key => (
            <button
              key={key}
              type="button"
              title={key}
              onClick={() => setColor(c => (c === key ? null : key))}
              style={{
                width: 22, height: 22, borderRadius: '50%', cursor: 'pointer',
                background: agentDotColor(key), flexShrink: 0,
                border: color === key ? `2px solid ${C.textHeading}` : '2px solid transparent',
                boxShadow: color === key ? `0 0 0 1px ${C.bgWhite} inset` : undefined,
              }}
            />
          ))}
        </div>
      </Field>

      {mode === 'existing' && (
        <Field label="Путь к папке" hint="Абсолютный путь к существующей папке проекта">
          <TextField value={path} onChange={setPath} placeholder="C:\Sources\my-project" mono />
        </Field>
      )}

      {groups.length > 0 && (
        <Field label="Группа">
          <GroupSelect groups={groups} value={groupId} onChange={setGroupId} />
        </Field>
      )}

      {/* Ведение истории файлов (git): без истории / ручной (код) / авто (документы).
          Карточки однострочные (подсказка в title) — тот же компактный паттерн, что и
          в «Редактировать проект» (GitModeCards), иначе секция разносит диалог по высоте. */}
      <Field label="История файлов (Git)">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
          {GIT_MODES.map(m => (
            <GitModeCard
              key={m.value}
              active={gitMode === m.value}
              label={m.label}
              hint={m.hint}
              onClick={() => setGitMode(m.value)}
            />
          ))}
          {gitMode === 'auto' && <GitPushRow checked={gitPush} onChange={setGitPush} />}
        </div>
      </Field>

      <SyncToggleRow enabled={sync} onChange={setSync} />
    </Modal>
  );
}
