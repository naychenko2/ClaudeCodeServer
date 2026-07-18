import { useEffect, useState } from 'react';
import { Folder, MessageCircle } from 'lucide-react';
import type { Project, ProjectGroup } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R } from '../../lib/design';
import { projectColor, projectInitial } from '../../lib/tasks';
import type { HubTab } from '../../components/HubTabs';
import { AddProjectDialog } from '../projects/dialogs/AddProjectDialog';
import { WidgetCard, WidgetAction, WidgetEmpty, relTime } from './WidgetCard';

// «Проекты»: недавние проекты с активностью, клик открывает проект
// (через prop onOpenProject — Back вернет на дашборд).
export function ProjectsWidget({ onHubTab, onOpenProject }: {
  onHubTab: (t: HubTab) => void;
  onOpenProject: (p: Project) => void;
}) {
  const [projects, setProjects] = useState<Project[]>([]);
  const [newOpen, setNewOpen] = useState(false);
  const [groups, setGroups] = useState<ProjectGroup[]>([]);

  useEffect(() => {
    api.projects.list().then(setProjects).catch(() => {});
  }, []);

  // Группы нужны диалогу создания — грузим лениво при открытии
  const openNew = () => {
    api.projectGroups.list().then(setGroups).catch(() => {});
    setNewOpen(true);
  };

  const recent = [...projects]
    .sort((a, b) => (b.updatedAt ?? '').localeCompare(a.updatedAt ?? ''))
    .slice(0, 5);

  return (
    <WidgetCard
      icon={<Folder size={16} strokeWidth={2} />}
      title="Проекты"
      onCreate={openNew}
      createTitle="Новый проект"
      action={<WidgetAction label="Все проекты →" onClick={() => onHubTab('projects')} />}
    >
      {recent.length === 0
        ? <WidgetEmpty text="Проектов пока нет." />
        : (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {recent.map(p => {
              const color = projectColor(p.id);
              return (
                <button
                  key={p.id}
                  onClick={() => onOpenProject(p)}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
                    background: 'none', border: 'none', borderRadius: 8, padding: '7px 8px',
                    margin: '0 -8px', cursor: 'pointer', minWidth: 0,
                  }}
                  onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                  onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
                >
                  {/* Цветная плитка-инициал — мини-версия ProjectCard */}
                  <span style={{
                    width: 28, height: 28, borderRadius: R.md, flexShrink: 0,
                    display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                    background: color.soft, color: color.main,
                    fontFamily: FONT.serif, fontSize: 14, fontWeight: 600,
                  }}>
                    {projectInitial(p.name)}
                  </span>
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, flex: 1, minWidth: 0,
                    whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                  }}>
                    {p.name}
                  </span>
                  {typeof p.sessionCount === 'number' && p.sessionCount > 0 && (
                    <span style={{
                      display: 'inline-flex', alignItems: 'center', gap: 4, flexShrink: 0,
                      fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted,
                    }}>
                      <MessageCircle size={11} strokeWidth={2} />{p.sessionCount}
                    </span>
                  )}
                  <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
                    {relTime(p.updatedAt)}
                  </span>
                </button>
              );
            })}
          </div>
        )}
      {newOpen && (
        <AddProjectDialog
          groups={groups}
          onSuccess={p => { setNewOpen(false); onOpenProject(p); }}
          onClose={() => setNewOpen(false)}
        />
      )}
    </WidgetCard>
  );
}
