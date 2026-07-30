import { useState } from 'react';
import { Plus, Search } from 'lucide-react';
import { C } from '../../lib/design';
import type { Project } from '../../types';
import { Button, PanelHeaderSlot } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { SidebarProjectSwitcher } from './SidebarProjectSwitcher';
import { ProjectPalette } from './ProjectPalette';
import { openNewProjectFlow } from './useAllProjects';

// Панель «Проекты» воркспейса: сменить проект, не уходя из него. Содержимое —
// та же строка-переключатель, что раньше жила шапкой внутри панели «Чаты»
// (иконки проектов, чип активного с настройками, перетаскивание и пины).
//
// Поиск и создание — в шапке карточки (PanelHeaderSlot), а не в содержимом:
// это действия панели, и им место в её шапке, рядом с крестиком, как у «Задач».
// Создание уводит в раздел «Проекты» с открытым диалогом — мастер проекта
// требует места, которого в узкой панели нет.
export function ProjectsPanel({ project, onOpenSettings }: {
  project: Project;
  onOpenSettings: () => void;
}) {
  const [paletteOpen, setPaletteOpen] = useState(false);
  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: C.bgWhite }}>
      {/* Две кнопки одной высоты: нейтральный поиск и акцентное создание. Поиск
          открывает палитру поверх центра — она умеет и переход к проекту, и
          «Все проекты», и создание. */}
      <PanelHeaderSlot>
        <Button
          variant="ghost" size="xs" title="Перейти к проекту"
          leftIcon={<Search size={13} strokeWidth={ICON_STROKE} />}
          onClick={() => setPaletteOpen(true)}
        >
          Найти
        </Button>
        <Button
          variant="primary" size="xs" title="Новый проект"
          leftIcon={<Plus size={13} strokeWidth={ICON_STROKE} />}
          onClick={openNewProjectFlow}
        >
          Проект
        </Button>
      </PanelHeaderSlot>

      <div style={{ padding: '8px 10px', flexShrink: 0 }}>
        <SidebarProjectSwitcher project={project} onOpenSettings={onOpenSettings} hideSearch />
      </div>

      {paletteOpen && <ProjectPalette currentProjectId={project.id} onClose={() => setPaletteOpen(false)} />}
    </div>
  );
}
