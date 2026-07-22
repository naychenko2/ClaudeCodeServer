// Панель «Артефакты сессии»: вкладки по категориям артефактов, derived из ленты чата.
// Контент вкладок вынесен в переиспользуемые секции components/artifacts/* — их же
// использует новый интерфейс workspace-cc-panels (рельса + стек панелей).
import { useState } from 'react';
import { FileText, ChevronRight, ChevronDown } from 'lucide-react';
import type { Session } from '../types';
import { C, FONT, R } from '../lib/design';
import { PillSwitch } from './Toolbar';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useSessionArtifacts } from '../hooks/useSessionArtifacts';
import { getTaskById } from '../lib/tasks';
import { resolveChatOrigin } from '../lib/chatOrigin';
import { ChatOriginBadge } from './ChatOriginBadge';
import { PANEL_KEYS, PANEL_META, panelBadge, type PanelKey } from './artifacts/meta';
import { PlanSection } from './artifacts/PlanSection';
import { TodosSection } from './artifacts/TodosSection';
import { NotesSection } from './artifacts/NotesSection';
import { CommentsSection } from './artifacts/CommentsSection';
import { AgentsSection } from './artifacts/AgentsSection';
import { FilesSection } from './artifacts/FilesSection';
import { LinksSection } from './artifacts/LinksSection';
import { ContextSection } from './artifacts/ContextSection';

interface Props {
  sessionId: string | null;
  // В чат-режиме проекта нет: projectId/rootPath/onOpenFile отсутствуют, вкладка «Файлы» скрывается.
  projectId?: string;
  rootPath?: string;
  onOpenFile?: (path: string) => void;
  onClose: () => void;
  isMobile?: boolean;
  // Собеседник-персона текущего чата — показывает вкладку «Контекст персоны» (①-L2a)
  personaId?: string | null;
  // Текущий чат — для резолва контекста происхождения (задача/автоматизация) в шапке
  // панели и заголовка выполняемой задачи во вкладке «Задачи»
  session?: Session | null;
}

export function ArtifactsPanel({ sessionId, projectId, rootPath, onOpenFile, onClose, isMobile, personaId, session }: Props) {
  // Задача, ради которой запущен чат (для заголовка/счётчика вкладки «Задачи») —
  // резолвится напрямую из стора задач по Session.taskId, без сетевого запроса
  const executingTask = session?.taskId ? getTaskById(session.taskId) : null;
  const executingTaskTitle = executingTask?.title ?? null;
  // Контекст происхождения (задача/автоматизация) — единый баннер в шапке панели
  const origin = session ? resolveChatOrigin(session) : null;
  const artifacts = useSessionArtifacts(sessionId, projectId, rootPath, executingTaskTitle);
  const { plans, todos, notes, comments, agents, workflows, files, links } = artifacts;
  // Чат-режим (без проекта): файлы открывать некуда — вкладку «Файлы» не показываем.
  const isChat = !projectId;

  // Вкладки — только непустые, в каноническом порядке PANEL_KEYS; видимость и
  // счётчики — из общего panelBadge (единый источник со счётчиками рельсы).
  const badgeOpts = { executingTask: !!executingTask, personaId, isChat };
  const tabs: { value: PanelKey; label: string }[] = PANEL_KEYS
    .map(key => ({ key, b: panelBadge(key, artifacts, badgeOpts) }))
    .filter(x => x.b.visible)
    .map(x => ({ value: x.key, label: x.b.badge ? `${PANEL_META[x.key].title} · ${x.b.badge}` : PANEL_META[x.key].title }));

  const [active, setActive] = useState<PanelKey>('plan');
  const activeKey: PanelKey | undefined = tabs.some(t => t.value === active) ? active : tabs[0]?.value;
  const isEmpty = tabs.length === 0;

  return (
    <div style={{ width: '100%', height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel, overflow: 'hidden' }}>
      {/* Шапка */}
      <div style={{
        flexShrink: 0, height: 52, display: 'flex', alignItems: 'center', gap: 8,
        padding: '0 10px 0 14px', borderBottom: `1px solid ${C.border}`,
      }}>
        <span style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, flex: 1 }}>
          Артефакты сессии
        </span>
        <button
          onClick={onClose}
          title="Скрыть панель"
          style={{ width: 30, height: 30, border: 'none', borderRadius: R.md, background: 'transparent', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
        >
          {isMobile
            ? <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            : <ChevronRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        </button>
      </div>

      {/* Баннер происхождения (задача/автоматизация) — виден независимо от активной вкладки */}
      {origin && (
        <div style={{
          flexShrink: 0, padding: '8px 14px', borderBottom: `1px solid ${C.border}`,
          background: origin.tone === 'info' ? C.infoBg : C.warningBg,
        }}>
          <ChatOriginBadge origin={origin} style={{ background: 'transparent', padding: 0, fontSize: 12 }} />
        </div>
      )}

      {isEmpty ? (
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 12, padding: 32, textAlign: 'center' }}>
          <div style={{ width: 56, height: 56, borderRadius: R.xxl, background: C.bgPanel, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <FileText size={24} strokeWidth={ICON_STROKE} color={C.accent} />
          </div>
          <div style={{ fontFamily: FONT.serif, fontSize: 16, color: C.textHeading }}>Пока ничего не менялось</div>
          <span style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, lineHeight: 1.5, maxWidth: 260 }}>
            Здесь появятся план, задачи, агенты, файлы и ссылки по ходу разговора.
          </span>
        </div>
      ) : (
        <>
          {/* Переключатель вкладок (только непустые) */}
          <div style={{ flexShrink: 0, padding: '8px 16px', borderBottom: `1px solid ${C.border}` }}>
            <PillSwitch<PanelKey> value={activeKey!} options={tabs} onChange={setActive} fill isMobile={isMobile} />
          </div>

          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            {activeKey === 'plan' && <PlanSection plans={plans} projectId={projectId} />}
            {activeKey === 'todos' && <TodosSection todos={todos} />}
            {activeKey === 'notes' && <NotesSection notes={notes} />}
            {activeKey === 'comments' && <CommentsSection comments={comments} />}
            {activeKey === 'agents' && <AgentsSection agents={agents} workflows={workflows} />}
            {activeKey === 'files' && <FilesSection files={files} onOpenFile={onOpenFile} />}
            {activeKey === 'links' && <LinksSection links={links} />}
            {activeKey === 'context' && personaId && <ContextSection personaId={personaId} sessionId={sessionId} />}
          </div>
        </>
      )}
    </div>
  );
}
