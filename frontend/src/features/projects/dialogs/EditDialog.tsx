import { useEffect, useState } from 'react';
import type { Project, ProjectGroup, PermissionRule, SystemPromptPart } from '../../../types';
import { api } from '../../../lib/api';
import { useOnline } from '../../../hooks/useOnline';
import { C, R } from '../../../lib/design';
import { Modal, ModalActions, TextField, TextArea, Field, Button } from '../../../components/ui';
import { GroupSelect } from '../GroupSelect';
import { ProjectSyncToggle } from '../../../components/ProjectSyncToggle';

interface Props {
  project: Project;
  groups?: ProjectGroup[];
  onSuccess: (updated: Project) => void;
  onClose: () => void;
}

type View = 'main' | 'prompt' | 'rules';

export function EditDialog({ project, groups = [], onSuccess, onClose }: Props) {
  const online = useOnline();
  const [view, setView] = useState<View>('main');
  const [name, setName] = useState(project.name);
  const [groupId, setGroupId] = useState(project.groupId ?? '');
  const [systemPrompt, setSystemPrompt] = useState(project.systemPrompt ?? '');
  const [showHiddenFiles, setShowHiddenFiles] = useState(project.showHiddenFiles ?? false);
  const [rules, setRules] = useState<PermissionRule[]>(project.permissionRules ?? []);
  const [draftPrompt, setDraftPrompt] = useState('');
  const [promptParts, setPromptParts] = useState<SystemPromptPart[] | null>(null);
  const builtinPrompt = promptParts?.find(p => p.kind === 'builtin')?.content
    ?? project.builtInSystemPrompt ?? '';
  const autoParts = promptParts?.filter(p => p.kind === 'auto') ?? [];
  const [error, setError] = useState('');

  // Эффективный промпт с сервера — ровно те части, что реально уходят в claude
  useEffect(() => {
    if (view !== 'prompt' || promptParts) return;
    api.projects.getEffectivePrompt(project.id)
      .then(r => setPromptParts(r.parts))
      .catch(() => {});
  }, [view, promptParts, project.id]);

  const handleConfirm = async () => {
    setError('');
    try {
      const updated = await api.projects.update(project.id, {
        name: name.trim(),
        groupId,
        systemPrompt,
        showHiddenFiles,
        permissionRules: rules.filter(r => r.pattern.trim()).map(r => ({ pattern: r.pattern.trim(), action: r.action })),
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
            color: C.textMuted,
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

        {/* Автодополнения (база знаний, теги) — read-only, добавляются сервером после ваших инструкций */}
        {autoParts.length > 0 && (
          <>
            <div style={{ borderBottom: `1px dashed ${C.border}`, margin: '2px 0' }} />
            <div>
              <div style={{
                fontSize: 12, fontWeight: 600, color: C.textSecondary,
                fontFamily: 'Hanken Grotesk, sans-serif',
                textTransform: 'uppercase', letterSpacing: '0.05em',
                marginBottom: 6,
              }}>
                Добавляется автоматически
              </div>
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
                  color: C.textMuted,
                  lineHeight: 1.6,
                  whiteSpace: 'pre-wrap',
                  maxHeight: 160,
                  overflowY: 'auto',
                }}>
                  {autoParts.map(p => p.content).join('\n\n')}
                </div>
              </div>
            </div>
          </>
        )}
      </Modal>
    );
  }

  if (view === 'rules') {
    const updateRule = (i: number, patch: Partial<PermissionRule>) =>
      setRules(rs => rs.map((r, j) => (j === i ? { ...r, ...patch } : r)));
    return (
      <Modal
        title="Правила разрешений"
        width={560}
        onClose={() => setView('main')}
        footer={<ModalActions confirmLabel="Готово" onConfirm={() => setView('main')} onCancel={() => setView('main')} />}
      >
        <div style={{ fontSize: 12.5, color: C.textMuted, lineHeight: 1.55 }}>
          Авто-разрешения и запреты для запросов прав. Шаблон: <code>Инструмент</code> или <code>Инструмент(маска)</code> с <code>*</code>.
          Запрет приоритетнее разрешения; без совпадения — спросит как обычно.
          Примеры: <code>Bash(npm run *)</code>, <code>Edit</code>, <code>WebFetch</code>.
        </div>
        {rules.map((r, i) => (
          <div key={i} style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <button
              onClick={() => updateRule(i, { action: r.action === 'allow' ? 'deny' : 'allow' })}
              style={{
                flexShrink: 0, width: 92, padding: '7px 0', borderRadius: R.md, border: 'none', cursor: 'pointer',
                fontSize: 12.5, fontWeight: 600, fontFamily: 'inherit',
                background: r.action === 'deny' ? C.dangerBg : C.accentLight,
                color: r.action === 'deny' ? C.danger : C.accent,
              }}
            >
              {r.action === 'deny' ? 'Запретить' : 'Разрешить'}
            </button>
            <input
              value={r.pattern}
              onChange={e => updateRule(i, { pattern: e.target.value })}
              placeholder="Bash(npm run *)"
              style={{
                flex: 1, minWidth: 0, height: 34, padding: '0 10px', borderRadius: R.md,
                border: `1px solid ${C.border}`, fontFamily: 'JetBrains Mono, monospace', fontSize: 12.5,
                color: C.textPrimary, background: C.bgWhite, outline: 'none',
              }}
            />
            <button
              onClick={() => setRules(rs => rs.filter((_, j) => j !== i))}
              title="Удалить"
              style={{ flexShrink: 0, width: 30, height: 30, border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 14 }}
            >
              ✕
            </button>
          </div>
        ))}
        <button
          onClick={() => setRules(rs => [...rs, { pattern: '', action: 'allow' }])}
          style={{
            alignSelf: 'flex-start', padding: '7px 14px', borderRadius: R.md, cursor: 'pointer',
            border: `1px dashed ${C.border}`, background: 'none', color: C.textSecondary, fontSize: 13, fontFamily: 'inherit',
          }}
        >
          + Добавить правило
        </button>
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
      {groups.length > 0 && (
        <Field label="Группа">
          <GroupSelect groups={groups} value={groupId} onChange={setGroupId} />
        </Field>
      )}
      <div style={{
        padding: '9px 13px', background: C.bgPanel,
        border: `1px solid ${C.border}`, borderRadius: R.xl,
        display: 'flex', flexDirection: 'column', gap: 2,
      }}>
        <div style={{ fontSize: 11, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          Путь
        </div>
        <div style={{
          fontFamily: 'JetBrains Mono, monospace', fontSize: 12.5,
          color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }} title={project.rootPath}>
          {project.rootPath}
        </div>
      </div>
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
        <Button variant="ghost" size="sm" onClick={handleEditPrompt} style={{ flexShrink: 0 }}>
          Редактировать
        </Button>
      </div>
      {/* Скрытые файлы */}
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        padding: '11px 14px', background: C.bgWhite,
        border: `1px solid ${C.border}`, borderRadius: R.xl,
      }}>
        <div>
          <div style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: 2 }}>
            Скрытые файлы и папки
          </div>
          <div style={{ fontSize: 12, color: C.textMuted }}>
            Показывать файлы и папки, начинающиеся с точки
          </div>
        </div>
        <button
          onClick={() => setShowHiddenFiles(v => !v)}
          style={{
            flexShrink: 0,
            width: 40, height: 22,
            background: showHiddenFiles ? C.accent : C.border,
            border: 'none', borderRadius: 11, cursor: 'pointer',
            position: 'relative', transition: 'background 0.15s',
          }}
        >
          <span style={{
            position: 'absolute', top: 3,
            left: showHiddenFiles ? 21 : 3,
            width: 16, height: 16,
            background: C.bgWhite, borderRadius: '50%',
            transition: 'left 0.15s',
          }} />
        </button>
      </div>
      {/* Правила разрешений */}
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        padding: '11px 14px', background: C.bgWhite,
        border: `1px solid ${C.border}`, borderRadius: R.xl,
      }}>
        <div style={{ minWidth: 0 }}>
          <div style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: 2 }}>
            Правила разрешений
          </div>
          <div style={{ fontSize: 12, color: C.textMuted }}>
            {rules.length ? `${rules.length} ${rules.length === 1 ? 'правило' : 'правил'}` : 'Нет правил — спрашивать каждый раз'}
          </div>
        </div>
        <Button variant="ghost" size="sm" onClick={() => setView('rules')} style={{ flexShrink: 0 }}>
          Настроить
        </Button>
      </div>
      <ProjectSyncToggle projectId={project.id} online={online} />
    </Modal>
  );
}
