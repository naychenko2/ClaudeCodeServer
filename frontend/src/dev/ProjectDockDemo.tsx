// Витрина — док проектов у левой кромки: голый столбец НАД рельсой панелей.
// Собран из тех же примитивов, что и ProjectRail (RailHat, RailIconButton media,
// ProjectIcon), но на выдуманных проектах: живому доку нужен бэкенд, а витрина
// работает без него. Выбранный — цвет и кольцо, остальные спят до наведения;
// полоска у кромки — под курсором и у непрочитанного; плюс — палитра проектов.
import { useState } from 'react';
import { BookOpenText, FolderTree, GitCompare, ListTodo, Bot, User, Plus, type LucideIcon } from 'lucide-react';
import { C, FS, ISLAND, R, SP } from '../lib/design';
import { Island, IslandHeader, PanelRail, RailHat, RailIconButton, type RailItem } from '../components/ui';
import { ICON_STROKE } from '../components/ui/icons';
import { ProjectIcon } from '../features/projects/ProjectIcon';
import { STATUS_COLOR } from '../lib/projectActivity';
import type { Project } from '../types';

const item = (key: string, title: string, Icon: LucideIcon, active = false, badge?: number): RailItem =>
  ({ key, title, Icon, active, badge, onClick: () => {} });

const GROUPS: RailItem[][] = [
  [
    item('files', 'Файлы', FolderTree, true),
    item('changes', 'Изменения', GitCompare, false, 8),
    item('tasks', 'Задачи', ListTodo),
    item('docs', 'Документация', BookOpenText),
  ],
  [item('agents', 'Агенты', Bot), item('context', 'Персона', User)],
];

type Mark = 'active' | 'unread' | undefined;
const PROJECTS: { p: Project; mark?: Mark; waiting?: boolean }[] = [
  { p: { id: 'proto-ccs', name: 'ClaudeCodeServer' } as Project, mark: 'active' },
  { p: { id: 'proto-dify', name: 'Dify stand' } as Project, waiting: true },
  { p: { id: 'proto-kino', name: 'Kino bot' } as Project, mark: 'unread' },
  { p: { id: 'proto-wall', name: 'Wiki notes' } as Project },
  { p: { id: 'proto-shop', name: 'Shop front' } as Project },
];

// Полоска у кромки: высота говорит состояние. Цвет нейтральный — акцент на ней
// занял бы второе место на экране рядом с главным действием.
const PILL_H = { hover: 10, unread: 6 } as const;

function EdgeDockIcon({ p, mark, waiting, railHover }: { p: Project; mark?: Mark; waiting?: boolean; railHover: boolean }) {
  const [hover, setHover] = useState(false);
  const active = mark === 'active';
  const muted = !railHover && !active;
  const pill = active ? undefined : hover ? 'hover' : mark === 'unread' ? 'unread' : undefined;
  return (
    <div style={{ position: 'relative', width: '100%', display: 'flex', justifyContent: 'center' }}>
      <span aria-hidden style={{
        position: 'absolute', left: 0, top: '50%', width: 3,
        height: pill ? PILL_H[pill] : 0, transform: 'translateY(-50%)',
        borderTopRightRadius: R.full, borderBottomRightRadius: R.full,
        background: C.textPrimary, opacity: pill === 'unread' ? 0.55 : 1,
        transition: 'height 0.15s ease-out',
      }} />
      <span style={{ position: 'relative', display: 'flex' }}>
        <RailIconButton side="left" label={p.name} variant="media" active={active && !muted} onHoverChange={setHover} onClick={() => {}}>
          <ProjectIcon project={p} size={32} radius={R.md} muted={muted} />
        </RailIconButton>
        {waiting && (
          <span style={{
            position: 'absolute', right: -2, top: -2, width: 8, height: 8, borderRadius: R.full,
            background: STATUS_COLOR.waiting, border: `2px solid ${C.bgMain}`, boxSizing: 'content-box',
            pointerEvents: 'none',
          }} />
        )}
      </span>
    </div>
  );
}

function EdgeDock() {
  const [railHover, setRailHover] = useState(false);
  return (
    <div
      onMouseEnter={() => setRailHover(true)}
      onMouseLeave={() => setRailHover(false)}
      style={{ width: 40, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 6, paddingTop: 4 }}
    >
      <RailHat side="left" label="Проекты" title="Переключение проектов" />
      {PROJECTS.map(x => <EdgeDockIcon key={x.p.id} {...x} railHover={railHover} />)}
      <RailIconButton side="left" label="Перейти к проекту" onClick={() => {}}>
        <Plus size={17} strokeWidth={ICON_STROKE} />
      </RailIconButton>
    </div>
  );
}

function FakePanel() {
  return (
    <div style={{ flex: 1, minWidth: 0, padding: `0 ${SP.sm}px` }}>
      <Island>
        <IslandHeader title="Файлы" />
        <div style={{ padding: ISLAND.pad, fontSize: FS.sm, color: C.textMuted }}>содержимое панели</div>
      </Island>
    </div>
  );
}

export function ProjectDockDemo() {
  return (
    <div id="project-dock-demo" style={{
      height: 520, display: 'flex', alignItems: 'flex-start', paddingTop: SP.sm,
      background: C.bgMain, border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden',
    }}>
      <PanelRail
        side="left" hat="Панели" groups={GROUPS} overflow={{ items: [] }}
        header={() => <EdgeDock />}
      />
      <FakePanel />
    </div>
  );
}
