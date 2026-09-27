// «Собрать архитектуру»: галочка «С агентом» и плашка сборки под шапкой документа.
// Проход 1 (детерминированный) отдаёт сводку сразу; проход 2 — задача исполнителю,
// её ход виден по task_changed (стор задач), а правки на холсте — по push filesChanged.
// Акцент здесь не используется: главное действие — кнопка сборки, плашка — справка.
import { useEffect, useRef, useState } from 'react';
import { Loader, Check, AlertTriangle, X, Bot } from 'lucide-react';
import {
  C, FS, SP, Button, IconButton, Toggle, ICON_SIZE, ICON_STROKE,
  useTasks, ensureTasksLoaded, openTaskInSection, usePersonas, ensurePersonasLoaded,
} from 'aihome_shell/kit';
import type { ArchState } from './architectureStore';
import type { ArchitectureGenerateResult } from '../../lib/api';
import type { Task } from '../../types';

// Метка незавершённой агентной сборки (IArchitectureAgentLauncher.BuildLabel на бэке)
const BUILD_LABEL = 'arch-build';

// Галочка живёт в localStorage страницы — в модель и на сервер не едет
export function useAgentPref(projectId: string): [boolean, (v: boolean) => void] {
  const key = `architecture-build-agent:${projectId}`;
  const [value, setValue] = useState(() => localStorage.getItem(key) === '1');
  useEffect(() => { setValue(localStorage.getItem(key) === '1'); }, [key]);
  return [value, (v: boolean) => { setValue(v); localStorage.setItem(key, v ? '1' : '0'); }];
}

// running — исполнитель реально работает; stalled — задача не закрыта, но и не идёт
// (не стартовала, остановлена, ждёт человека): сервер всё равно считает её блокирующей
export type AgentPhase = 'running' | 'stalled' | 'done' | 'failed' | null;

// Та же формула «сборка блокирует», что у сервера (ArchitectureAgentLauncherAdapter.FindBlocking):
// любая незакрытая задача проекта с меткой arch-build, регистр метки не важен
export function isBlockingBuild(t: Task, projectId: string): boolean {
  return t.projectId === projectId && t.status !== 'done'
    && (t.labels ?? []).some(l => l.toLowerCase() === BUILD_LABEL);
}

// Чистое ядро useAgentBuild (под тестом). seenKnown — задачу knownTaskId стор уже отдавал:
// пропала после этого — её удалили, а не «ещё не приехала», вечного running быть не должно
export function resolveAgentBuild(tasks: Task[], projectId: string, knownTaskId: string | null, seenKnown: boolean) {
  const known = knownTaskId ? tasks.find(t => t.id === knownTaskId) : undefined;
  const task = known ?? tasks.find(t => isBlockingBuild(t, projectId));
  let phase: AgentPhase = null;
  if (task) {
    if (task.status === 'done') phase = task.claudeResult === 'error' ? 'failed' : 'done';
    else if ((task.claudeStartedAt || task.status === 'inProgress') && !task.claudeResult && !task.executorStoppedAt) phase = 'running';
    else phase = 'stalled';
  } else if (knownTaskId && !seenKnown) {
    phase = 'running'; // задача только что создана, стор её ещё не получил
  }
  return { task, taskId: task?.id ?? (phase ? knownTaskId : null), phase, personaId: task?.personaId ?? null };
}

// Состояние агентной сборки проекта: задача из ответа сборки, а после перезагрузки
// страницы — незакрытая задача с меткой arch-build (стор задач живёт по task_changed)
export function useAgentBuild(projectId: string, knownTaskId: string | null) {
  const tasks = useTasks();
  useEffect(() => { void ensureTasksLoaded(); }, []);
  const seen = useRef<{ id: string | null; seen: boolean }>({ id: null, seen: false });
  if (seen.current.id !== knownTaskId) seen.current = { id: knownTaskId, seen: false };
  const r = resolveAgentBuild(tasks, projectId, knownTaskId, seen.current.seen);
  if (knownTaskId && r.task?.id === knownTaskId) seen.current.seen = true;
  return { taskId: r.taskId, phase: r.phase, personaId: r.personaId };
}

// Агентная сборка сейчас не запустится (сервер ответил бы 409): почему — для подсказок
export function agentBlockedHint(phase: AgentPhase): string | null {
  if (phase === 'running') return 'Агент уже собирает — дождитесь его';
  if (phase === 'stalled') return 'Задача сборки не закрыта — перезапустите или закройте её';
  return null;
}

// Ошибка пересборки ГОТОВОЙ модели: пустого состояния с ошибкой тут нет (холст на месте),
// поэтому её несёт плашка сборки. Повреждённый файл и отсутствующую модель ведут свои экраны
export function rebuildErrorOf(s: Pick<ArchState, 'status' | 'corrupt' | 'generateError'>): string | null {
  return s.status === 'ready' && !s.corrupt ? s.generateError : null;
}

export function AgentToggle({ checked, onChange, disabled, hint }: {
  checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; hint?: string | null;
}) {
  return (
    <div title={hint ?? undefined} style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm, maxWidth: 380, textAlign: 'left' }}>
      <Toggle checked={checked} onChange={onChange} disabled={disabled} focusable ariaLabel="С агентом" />
      <div style={{ minWidth: 0 }}>
        <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>С агентом</div>
        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
          {hint ?? 'Проверит кандидатов и дополнит модель. Создаётся задача со своим чатом; без персоны-архитектора работает исполнитель без персоны.'}
        </div>
      </div>
    </div>
  );
}

function summaryText(r: ArchitectureGenerateResult): string {
  const parts = [`новых ${r.added}`, `без изменений ${r.matched}`];
  if (r.connectionsAdded) parts.push(`связей +${r.connectionsAdded}`);
  if (r.candidates) parts.push(`кандидатов ${r.candidates}`);
  if (r.markedMissing) parts.push(`нет в коде ${r.markedMissing}`);
  if (r.unmarked) parts.push(`вернулись в код ${r.unmarked}`);
  if (r.skippedDeleted) parts.push(`не пересозданы (удалены вами) ${r.skippedDeleted}`);
  return `Собрано: ${parts.join(' · ')}`;
}

function summaryTitle(r: ArchitectureGenerateResult): string | undefined {
  const lines: string[] = [];
  if (r.missing?.length) lines.push(`Нет в коде: ${r.missing.join(', ')}`);
  if (r.candidatesSkipped?.length) lines.push(`Не взяты в кандидаты: ${r.candidatesSkipped.join(', ')}`);
  return lines.length ? lines.join('\n') : undefined;
}

// Плашка между шапкой и холстом: сводка прохода 1 и/или статус агента.
// Тон — info; отказы агента и его ошибка — warning
export function BuildBanner({ s, projectId, isMobile, onDismiss }: {
  s: ArchState; projectId: string; isMobile: boolean; onDismiss: () => void;
}) {
  const agent = useAgentBuild(projectId, s.agent?.taskId ?? null);
  const personas = usePersonas();
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  // Крестик у незакрытой задачи: сама она из стора задач никуда не денется, прячем
  // её строку до смены задачи или фазы
  const [hidden, setHidden] = useState<string | null>(null);
  const hideKey = agent.taskId ? `${agent.taskId}:${agent.phase}` : null;
  const code = s.agent?.error ?? null;
  const personaId = s.agent?.personaId ?? agent.personaId;
  const persona = personaId ? personas.find(p => p.id === personaId) : undefined;

  let agentLine: string | null = null;
  let warn = false;
  let icon = <Bot size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />;
  if (code === 'agent_unavailable') {
    agentLine = 'Агентная сборка недоступна — модель собрана без агента.'; warn = true;
  } else if (code === 'launch_failed') {
    agentLine = 'Задача для агента создана, но исполнитель не стартовал — запустите её из карточки.'; warn = true;
  } else if (code === 'build_in_progress' && agent.phase === 'running') {
    agentLine = 'Сборка уже идёт — агент ещё не закончил прошлую. Холст обновляется по ходу его правок.';
  } else if (agent.phase === 'running') {
    const who = persona ? `«${persona.name}»` : 'исполнитель без персоны (персоны-архитектора в проекте нет)';
    agentLine = `Агент собирает архитектуру — ${who}. Правки появятся на холсте сами.`;
    icon = <Loader size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />;
  } else if (agent.phase === 'done') {
    agentLine = 'Агент закончил — итог в задаче.';
    icon = <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />;
  } else if (agent.phase === 'failed') {
    agentLine = 'Агент остановился, не закончив, — подробности в задаче.'; warn = true;
  } else if (agent.phase === 'stalled') {
    agentLine = 'Задача сборки не закрыта — перезапустите или закройте её, иначе новая сборка с агентом не стартует.'; warn = true;
  } else if (code) {
    agentLine = 'Агентная сборка не запустилась — модель собрана без агента.'; warn = true;
  }
  if (agentLine && !code && hideKey && hidden === hideKey) agentLine = null;
  const rebuildError = rebuildErrorOf(s);
  if (rebuildError) warn = true;
  if (warn) icon = <AlertTriangle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />;

  const summary = s.lastBuild ? summaryText(s.lastBuild) : null;
  if (!agentLine && !summary && !rebuildError) return null;
  // Незавершённую агентную сборку не прячем крестиком — она сама себя закроет
  const dismissible = agent.phase !== 'running';

  return (
    <div style={{
      flexShrink: 0, display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
      padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm,
      background: warn ? C.warningBg : C.infoBg, color: warn ? C.warningText : C.info,
    }}>
      {agentLine || rebuildError ? icon : <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
      <div style={{ flex: 1, minWidth: isMobile ? 0 : 200, display: 'flex', flexDirection: 'column', gap: 2 }}>
        {rebuildError && <span>Пересобрать не удалось: {rebuildError}. На холсте прежняя модель.</span>}
        {agentLine && <span>{agentLine}</span>}
        {summary && (
          <span title={s.lastBuild ? summaryTitle(s.lastBuild) : undefined}
            style={{ fontSize: FS.xs, opacity: agentLine ? 0.85 : 1 }}>{summary}</span>
        )}
      </div>
      {agent.taskId && agentLine && (
        <Button variant="ghost" size="xs" onClick={() => openTaskInSection({ id: agent.taskId!, projectId })}>
          Открыть задачу
        </Button>
      )}
      {dismissible && (
        <IconButton size="xs" title="Скрыть" onClick={() => { setHidden(hideKey); onDismiss(); }}>
          <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      )}
    </div>
  );
}
