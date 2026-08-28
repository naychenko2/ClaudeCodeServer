import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { Crown, Folder, GitBranch, Lock, Sparkles, X } from 'lucide-react';
import type { Project, ProjectGroup, PermissionRule, SystemPromptPart } from '../../../types';
import { api } from '../../../lib/api';
import { useOnline } from '../../../hooks/useOnline';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { Modal, ModalActions, TextArea, Field, Button, Toggle } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { useFeature, FLAGS } from '../../../lib/featureFlags';
import { useIsMobile } from '../../../lib/breakpoints';
import { useMe } from '../../../lib/defaultPersona';
import { getNav } from '../../../lib/nav';
import { OPEN_INTRO_EVENT } from '../../onboarding/OnboardingPage';
import { GroupSelect } from '../GroupSelect';
import { GIT_BODY_H, GIT_CARD_H, GitModeCard, GitPushRow } from '../components/GitModeCards';
import { ProjectSyncToggle } from '../../../components/ProjectSyncToggle';
import { ProjectIconSection } from '../ProjectIconSection';
import { McpProjectSection } from '../../mcp/McpProjectSection';
import { BackgroundSection } from './BackgroundSection';
import { AccordionSection, type AccordionSummaryTone } from './AccordionSection';
import { invalidateProjectsCache } from '../useAllProjects';

// Строка «Руководитель проекта не назначен» (фича default-personas-onboarding, п.5.3):
// главная кнопка — «Познакомиться с проектом» (интервью работает всегда, «Назначить
// руководителя» упирается в пустую команду, если персон в проекте ещё нет). Обе кнопки
// сначала закрывают диалог — иначе overlay знакомства и модалка окажутся на одном Z.modal.
function ProjectLeadSection({ project, onClose }: { project: Project; onClose: () => void }) {
  const isMobile = useIsMobile();
  const goIntro = () => { onClose(); window.dispatchEvent(new CustomEvent(OPEN_INTRO_EVENT, { detail: { projectId: project.id } })); };
  // Панель «Команда»: тот же приём, что у диплинка на персону (cc_pending_persona) —
  // sessionStorage + событие для уже смонтированного воркспейса того же проекта,
  // cc-open-session для перехода из другого проекта/раздела (см. WorkspacePage).
  const goTeam = () => {
    onClose();
    sessionStorage.setItem('cc_pending_team_center', project.id);
    const n = getNav();
    if (n?.screen === 'project' && n.project?.id === project.id) {
      window.dispatchEvent(new Event('cc-pending-team-center'));
    } else {
      window.dispatchEvent(new CustomEvent('cc-open-session', { detail: { project } }));
    }
  };
  return (
    <div style={{
      padding: '8px 12px', background: C.bgWhite,
      border: `1px solid ${C.border}`, borderRadius: R.xl,
      display: 'flex', flexDirection: 'column', gap: SP.sm,
    }}>
      <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
        Руководитель проекта
      </div>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm }}>
        <Crown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.accent, marginTop: 2 }} />
        <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.5 }}>
          У проекта нет руководителя. Пока чаты ведёт ваш личный ассистент — без командных механик.
        </div>
      </div>
      <div style={{ display: 'flex', gap: SP.sm, flexDirection: isMobile ? 'column' : 'row' }}>
        <Button variant="ghostAccent" size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
          leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={goIntro}>
          Познакомиться с проектом
        </Button>
        <Button variant="ghost" size={isMobile ? 'md' : 'sm'} fullWidth={isMobile} onClick={goTeam}>
          Назначить руководителя
        </Button>
      </div>
    </div>
  );
}

// === История файлов (Git) в настройках проекта ===
// Включение необратимо by design: «выключить» означало бы удалить .git со всей историей,
// поэтому для ведущегося репозитория показываем статус-строку и выбор ручное/авто,
// а карточки «Без ведения истории» больше нет.
// Карточки — однострочные (подсказка в title), иначе секция «съедала» весь диалог по высоте.
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

  const reload = useCallback(async () => {
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
  }, [project.id]);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- загрузка состояния git-репозитория при смене проекта
  useEffect(() => { void reload(); }, [reload]);

  const run = async (op: () => Promise<unknown>) => {
    setBusy(true); setErr('');
    try { await op(); await reload(); }
    catch (e: unknown) { setErr(e instanceof Error ? e.message : 'Не получилось'); }
    finally { setBusy(false); }
  };

  const enable = (auto: boolean) => run(async () => {
    await api.git.init(project.id);
    if (auto) await api.git.setAutoCommit(project.id, true, false);
  });
  const setMode = (auto: boolean) => run(() => api.git.setAutoCommit(project.id, auto, auto ? autoPush : false));
  const togglePush = () => run(() => api.git.setAutoCommit(project.id, true, !autoPush));

  const firstDateStr = firstDate
    ? new Date(firstDate).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' })
    : null;

  const isMobile = useIsMobile();
  // Сводка статуса git-репозитория в заголовке аккордеона (словарь сводок спецификации).
  // Во время загрузки сводки нет: git-статус приезжает отдельным запросом, и до него
  // заголовок читается просто как название секции.
  const gitSummary: { text: string; tone: AccordionSummaryTone } | undefined = (() => {
    if (loading) return undefined;
    if (err) return { text: 'Ошибка — откройте', tone: 'err' };
    if (!isRepo) return { text: 'Не ведётся', tone: 'neutral' };
    const c = commitCount === null ? '' : (commitCount >= 1000 ? '1000+' : `${commitCount}`);
    if (autoCommit) {
      return autoPush
        ? { text: isMobile ? 'Авто · push' : 'Авто · push вкл', tone: 'ok' }
        : { text: isMobile ? `Авто · ${c}` : `Авто · ${c} коммитов`, tone: 'ok' };
    }
    return { text: isMobile ? `Ручная · ${c}` : `Ручная · ${c} коммитов`, tone: 'neutral' };
  })();

  return (
    <AccordionSection
      icon={GitBranch}
      title={isMobile ? 'История файлов' : 'История файлов (Git)'}
      summary={gitSummary?.text}
      summaryTone={gitSummary?.tone}
    >
      {err && <div style={{ fontSize: 12, color: C.dangerText, marginBottom: 5 }}>{err}</div>}
      {/* Высота тела фиксирована снизу ВО ВСЕХ состояниях: git-статус приезжает
          отдельным запросом, и без резерва диалог подпрыгивал на ~90px в момент
          ответа. Одного скелета мало — у репозитория готовый набор (бейдж и две
          карточки) на пару пикселей ниже трёх карточек, и диалог оседал на 2px.
          minHeight по самому высокому варианту убирает и это. */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 5, minHeight: GIT_BODY_H }}>
      {loading ? (
        <>
          {[0, 1, 2].map(i => (
            <div key={i} aria-hidden style={{ height: GIT_CARD_H, borderRadius: R.lg, background: C.bgInset }} />
          ))}
        </>
      ) : !isRepo ? (
        <>
          <GitModeCard active label="Без ведения истории" hint="Обычная папка — версии файлов не сохраняются" onClick={() => {}} />
          <GitModeCard active={false} label="Ручное ведение истории" hint="Версии сохраняются, когда вы сами нажмёте «Зафиксировать» в разделе «Файлы». Рекомендуется для разработки кода" disabled={busy} onClick={() => enable(false)} />
          <GitModeCard active={false} label="Автоматическое ведение истории" hint="Каждый ход ИИ сохраняется в историю сам. Рекомендуется для работы с документами" disabled={busy} onClick={() => enable(true)} />
        </>
      ) : (
        <>
          {/* История уже ведётся — выключения нет by design (означало бы удалить .git) */}
          <div style={{
            display: 'flex', alignItems: 'center', gap: 8, padding: '6px 10px',
            borderRadius: R.lg, background: C.successBg, color: C.successText,
            fontSize: 12, fontFamily: FONT.sans,
          }}>
            <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              История ведётся{commitCount !== null ? ` · ${commitCount >= 1000 ? '1000+' : commitCount} коммитов` : ''}{firstDateStr ? ` · с ${firstDateStr}` : ''}
            </span>
          </div>
          <GitModeCard active={!autoCommit} label="Ручное ведение истории" hint="Версии сохраняются, когда вы сами нажмёте «Зафиксировать» в разделе «Файлы». Рекомендуется для разработки кода" disabled={busy} onClick={() => setMode(false)} />
          <GitModeCard active={autoCommit} label="Автоматическое ведение истории" hint="Каждый ход ИИ сохраняется в историю сам. Рекомендуется для работы с документами" disabled={busy} onClick={() => setMode(true)} />
          {autoCommit && (
            <GitPushRow
              checked={autoPush}
              onChange={() => void togglePush()}
              disabled={!remoteUrl || busy}
              disabledTitle={remoteUrl ? undefined : 'Git-сервер не настроен — отправлять некуда'}
            />
          )}
        </>
      )}
      </div>
    </AccordionSection>
  );
}

// Однострочная строка настроек внутри общей карточки-списка (путь/промпт/тумблеры/правила).
// Разделитель вместо отдельных карточек — экономит паддинги и межблочные отступы,
// это и было главным вкладом диалога в вертикальный скролл.
function SettingsRow({ children, last, title }: { children: ReactNode; last?: boolean; title?: string }) {
  return (
    <div
      title={title}
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12,
        padding: '9px 14px',
        borderBottom: last ? 'none' : `1px solid ${C.borderLight}`,
      }}
    >
      {children}
    </div>
  );
}

interface Props {
  project: Project;
  groups?: ProjectGroup[];
  onSuccess: (updated: Project) => void;
  // Проброс обновлённого проекта в стор после иконочной мутации (generate/select/upload/recrop),
  // не дожидаясь «Сохранить» — иначе список стухнет при закрытии крестиком. Realtime у проектов нет.
  onIconUpdated?: (updated: Project) => void;
  // Секция MCP сохраняет своё поле сама, минуя handleConfirm — без этого проброса
  // повторное открытие диалога показывало состояние до клика (устаревший project prop)
  onProjectUpdated?: (updated: Project) => void;
  onClose: () => void;
}

type View = 'main' | 'prompt' | 'rules';

export function EditDialog({ project, groups = [], onSuccess, onIconUpdated, onProjectUpdated, onClose }: Props) {
  const online = useOnline();
  // Фон проекта — только под флагом и только владельцу: участник без прав владельца фон
  // менять не может (ADR-008 §7, постановка задачи). Бэк тоже гейтит (404), кнопки прячем
  // за тем же условием, чтобы не показывать недоступное действие.
  const bgEnabled = useFeature(FLAGS.projectBackgrounds);
  const me = useMe();
  const isOwner = !project.ownerId || project.ownerId === me.userId;
  const [view, setView] = useState<View>('main');
  const [name, setName] = useState(project.name);
  const [groupId, setGroupId] = useState(project.groupId ?? '');
  const [iconColor, setIconColor] = useState<string | null>(project.icon?.color ?? null);
  const [systemPrompt, setSystemPrompt] = useState(project.systemPrompt ?? '');
  const [showHiddenFiles, setShowHiddenFiles] = useState(project.showHiddenFiles ?? false);
  const [rules, setRules] = useState<PermissionRule[]>(project.permissionRules ?? []);
  // Строка «Руководитель проекта» (фича default-personas-onboarding, п.5.3) — у проекта
  // с назначенным руководителем секции нет вовсе
  const showLeadSection = useFeature(FLAGS.defaultPersonasOnboarding) && project.defaultPersonaId == null;
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
        color: iconColor ?? '',
      });
      invalidateProjectsCache(); // полка/палитра проектов подхватывают новое имя/иконку
      onSuccess(updated);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить');
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
              autoComplete="off"
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
      width={500}
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
      <ProjectIconSection
        project={project}
        name={name}
        onNameChange={setName}
        color={iconColor}
        onColorChange={setIconColor}
        onIconUpdated={updated => { setIconColor(updated.icon?.color ?? null); invalidateProjectsCache(); onIconUpdated?.(updated); }}
      />
      {groups.length > 0 && (
        <Field label="Группа">
          <GroupSelect groups={groups} value={groupId} onChange={setGroupId} />
        </Field>
      )}
      {/* Путь / системный промпт / скрытые файлы / инструменты / правила — единая карточка-
          список вместо пяти отдельных карточек: экономит паддинги и межблочные отступы */}
      <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden' }}>
        <SettingsRow title={project.rootPath}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
            <Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} />
            <span style={{
              fontFamily: 'JetBrains Mono, monospace', fontSize: 12.5, color: C.textSecondary,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {project.rootPath}
            </span>
          </div>
        </SettingsRow>
        <SettingsRow>
          <div style={{ minWidth: 0, overflow: 'hidden', whiteSpace: 'nowrap', textOverflow: 'ellipsis', fontSize: 13 }}>
            <span style={{ fontWeight: 600, color: C.textHeading }}>Системный промпт</span>
            {systemPrompt
              ? <span style={{ color: C.textMuted }}> · {systemPrompt}</span>
              : <span style={{ color: C.textMuted }}> · не задан</span>}
          </div>
          <Button variant="ghost" size="sm" onClick={handleEditPrompt} style={{ flexShrink: 0 }}>
            Редактировать
          </Button>
        </SettingsRow>
        <SettingsRow title="Показывать файлы и папки, начинающиеся с точки">
          <span style={{ fontSize: 13, color: C.textPrimary }}>Скрытые файлы и папки</span>
          <Toggle checked={showHiddenFiles} onChange={setShowHiddenFiles} />
        </SettingsRow>
        <SettingsRow last>
          <span style={{ fontSize: 13, color: C.textPrimary }}>
            Правила разрешений
            <span style={{ color: C.textMuted }}>
              {' · '}{rules.length ? `${rules.length} ${rules.length === 1 ? 'правило' : 'правил'}` : 'спрашивать каждый раз'}
            </span>
          </span>
          <Button variant="ghost" size="sm" onClick={() => setView('rules')} style={{ flexShrink: 0 }}>
            Настроить
          </Button>
        </SettingsRow>
      </div>
      {showLeadSection && <ProjectLeadSection project={project} onClose={onClose} />}
      <McpProjectSection project={project} onUpdated={onProjectUpdated} />
      <GitHistorySection project={project} />
      {bgEnabled && isOwner && (
        <BackgroundSection
          project={project}
          iconColor={iconColor}
          onColorChange={setIconColor}
          onProjectUpdated={onProjectUpdated ?? (() => {})}
        />
      )}
      <ProjectSyncToggle projectId={project.id} online={online} />
    </Modal>
  );
}
