import { useEffect, useState } from 'react';
import { GitBranch, Lock, X } from 'lucide-react';
import type { Project, ProjectGroup, PermissionRule, SystemPromptPart } from '../../../types';
import { api } from '../../../lib/api';
import { useOnline } from '../../../hooks/useOnline';
import { C, FONT, R } from '../../../lib/design';
import { Modal, ModalActions, TextField, TextArea, Field, Button, Toggle } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { GroupSelect } from '../GroupSelect';
import { ProjectSyncToggle } from '../../../components/ProjectSyncToggle';

// === История файлов (Git) в настройках проекта ===
// Включение необратимо by design: «выключить» означало бы удалить .git со всей историей,
// поэтому для ведущегося репозитория показываем статус-строку и выбор ручное/авто,
// а карточки «Без ведения истории» больше нет.
function GitHistorySection({ project }: { project: Project }) {
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [isRepo, setIsRepo] = useState(false);
  const [autoCommit, setAutoCommit] = useState(false);
  const [autoPush, setAutoPush] = useState(false);
  const [remoteUrl, setRemoteUrl] = useState<string | null>(null);
  const [commitCount, setCommitCount] = useState<number | null>(null);
  const [firstDate, setFirstDate] = useState<string | null>(null);
  const [err, setErr] = useState('');

  const reload = async () => {
    try {
      const st = await api.git.status(project.id);
      setIsRepo(st.isRepo);
      if (st.isRepo) {
        const [remote, log] = await Promise.all([
          api.git.remote(project.id),
          api.git.log(project.id, 1000),
        ]);
        setAutoCommit(remote.autoCommit);
        setAutoPush(remote.autoPush);
        setRemoteUrl(remote.remoteUrl);
        setCommitCount(log.length);
        setFirstDate(log.length ? log[log.length - 1].date : null);
      }
    } catch { /* оффлайн/ошибка — секция покажет «недоступно» через isRepo=false без карточек? нет: просто молчим */ }
    finally { setLoading(false); }
  };
  useEffect(() => { void reload(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [project.id]);

  const run = async (op: () => Promise<unknown>) => {
    setBusy(true); setErr('');
    try { await op(); await reload(); }
    catch (e: any) { setErr(e.message ?? 'Не получилось'); }
    finally { setBusy(false); }
  };

  const enable = (auto: boolean) => run(async () => {
    await api.git.init(project.id);
    if (auto) await api.git.setAutoCommit(project.id, true, false);
  });
  const setMode = (auto: boolean) => run(() => api.git.setAutoCommit(project.id, auto, auto ? autoPush : false));
  const togglePush = () => run(() => api.git.setAutoCommit(project.id, true, !autoPush));

  const card = (active: boolean, label: string, hint: string, onClick: () => void) => (
    <div
      onClick={busy ? undefined : onClick}
      style={{
        display: 'flex', alignItems: 'flex-start', gap: 9, padding: '8px 11px',
        cursor: busy ? 'default' : 'pointer', opacity: busy ? 0.6 : 1,
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
        <span style={{ fontSize: 13, fontFamily: FONT.sans, fontWeight: 600, color: active ? C.textHeading : C.textPrimary }}>{label}</span>
        <span style={{ fontSize: 11.5, fontFamily: FONT.sans, color: C.textSecondary, lineHeight: 1.35 }}>{hint}</span>
      </span>
    </div>
  );

  const firstDateStr = firstDate
    ? new Date(firstDate).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' })
    : null;

  return (
    <div style={{
      padding: '11px 14px', background: C.bgWhite,
      border: `1px solid ${C.border}`, borderRadius: R.xl,
      display: 'flex', flexDirection: 'column', gap: 8,
    }}>
      <div style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
        История файлов (Git)
      </div>
      {err && <div style={{ fontSize: 12, color: C.dangerText }}>{err}</div>}
      {loading ? (
        <div style={{ fontSize: 12.5, color: C.textMuted }}>Загрузка…</div>
      ) : !isRepo ? (
        <>
          {card(true, 'Без ведения истории', 'Обычная папка — версии файлов не сохраняются', () => {})}
          {card(false, 'Ручное ведение истории', 'Версии сохраняются, когда вы сами нажмёте «Зафиксировать» в разделе «Файлы». Рекомендуется для разработки кода', () => enable(false))}
          {card(false, 'Автоматическое ведение истории', 'Каждый ход ИИ сохраняется в историю сам. Рекомендуется для работы с документами', () => enable(true))}
        </>
      ) : (
        <>
          {/* История уже ведётся — выключения нет by design (означало бы удалить .git) */}
          <div style={{
            display: 'flex', alignItems: 'center', gap: 8, padding: '7px 11px',
            borderRadius: R.lg, background: C.successBg, color: C.successText,
            fontSize: 12.5, fontFamily: FONT.sans,
          }}>
            <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            <span>
              История ведётся{commitCount !== null ? ` · ${commitCount >= 1000 ? '1000+' : commitCount} коммитов` : ''}{firstDateStr ? ` · с ${firstDateStr}` : ''}
            </span>
          </div>
          {card(!autoCommit, 'Ручное ведение истории', 'Версии сохраняются, когда вы сами нажмёте «Зафиксировать» в разделе «Файлы». Рекомендуется для разработки кода', () => setMode(false))}
          {card(autoCommit, 'Автоматическое ведение истории', 'Каждый ход ИИ сохраняется в историю сам. Рекомендуется для работы с документами', () => setMode(true))}
          {autoCommit && (
            <div
              onClick={remoteUrl && !busy ? togglePush : undefined}
              title={remoteUrl ? undefined : 'Git-сервер не настроен — отправлять некуда'}
              style={{
                display: 'flex', alignItems: 'center', gap: 8, padding: '2px 11px 0 34px',
                cursor: remoteUrl && !busy ? 'pointer' : 'default', opacity: remoteUrl ? 1 : 0.5,
              }}
            >
              <Toggle checked={autoPush} onChange={() => { if (remoteUrl && !busy) void togglePush(); }} />
              <span style={{ fontSize: 12.5, fontFamily: FONT.sans, color: C.textPrimary }}>Ещё и отправлять копию на git-сервер (push)</span>
            </div>
          )}
        </>
      )}
    </div>
  );
}

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
  const [toolsEnabled, setToolsEnabled] = useState(project.toolsEnabled ?? false);
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
        toolsEnabled,
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
          <span style={{ flexShrink: 0, color: C.textMuted, marginTop: 1 }}><Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></span>
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
                <span style={{ flexShrink: 0, color: C.textMuted, marginTop: 1 }}><Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></span>
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
              style={{ flexShrink: 0, width: 30, height: 30, border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 14, display: 'flex', alignItems: 'center', justifyContent: 'center' }}
            >
              <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
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
      {/* Инструменты (терминал + preview) */}
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        padding: '11px 14px', background: C.bgWhite,
        border: `1px solid ${C.border}`, borderRadius: R.xl,
      }}>
        <div>
          <div style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: 2 }}>
            Инструменты
          </div>
          <div style={{ fontSize: 12, color: C.textMuted }}>
            Встроенный терминал и предпросмотр dev-сервера
          </div>
        </div>
        <button
          onClick={() => setToolsEnabled(v => !v)}
          style={{
            flexShrink: 0,
            width: 40, height: 22,
            background: toolsEnabled ? C.accent : C.border,
            border: 'none', borderRadius: 11, cursor: 'pointer',
            position: 'relative', transition: 'background 0.15s',
          }}
        >
          <span style={{
            position: 'absolute', top: 3,
            left: toolsEnabled ? 21 : 3,
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
      <GitHistorySection project={project} />
      <ProjectSyncToggle projectId={project.id} online={online} />
    </Modal>
  );
}
