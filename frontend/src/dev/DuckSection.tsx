// Витрина дизайн-системы — секция «Утка правой рельсы».
//
// Настоящая PanelRail с набором кнопок как в воркспейсе и утка последней кнопкой
// столбца, перед «…» (PanelRail.tail): видно, что по стилю она от соседей не отличается.
// Финал отдельной кнопкой: честный путь до него — три побега по ~10 с.
import { useState } from 'react';
import {
  Bird, Bot, BookOpenText, Contact, FolderTree, GitCompare, Lightbulb, ListTodo, MonitorPlay, User, Users,
  type LucideIcon,
} from 'lucide-react';
import { C, FS, SP } from '../lib/design';
import { Button, Island, IslandHeader, PanelRail, type RailItem } from '../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { RailDuck, DuckFinale } from '../features/duck/RailDuck';

const item = (key: string, title: string, Icon: LucideIcon, badge?: number): RailItem =>
  ({ key, title, Icon, active: false, badge, onClick: () => {} });

const GROUPS: RailItem[][] = [
  [
    item('files', 'Файлы', FolderTree),
    item('changes', 'Изменения', GitCompare, 8),
    item('tasks', 'Задачи', ListTodo),
    item('docs', 'Документация', BookOpenText),
    item('dossiers', 'История решений', Lightbulb),
    item('team', 'Команда', Users),
    item('characters', 'Персонажи', Contact),
  ],
  [item('video', 'Видео', MonitorPlay)],
  [item('agents', 'Агенты', Bot), item('context', 'Персона', User)],
];

export function DuckSection() {
  const [finale, setFinale] = useState(false);
  return (
    <Island>
      <IslandHeader
        icon={<Bird size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />}
        title="Утка правой рельсы"
        badge="features/duck"
      />
      <div style={{ padding: SP.lg, display: 'flex', alignItems: 'stretch', gap: SP.xl, flexWrap: 'wrap' }}>
        <div style={{ flex: 1, minWidth: 200, display: 'flex', flexDirection: 'column', gap: SP.md, alignItems: 'flex-start' }}>
          <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
            Утка — последняя кнопка столбца правой рельсы, перед ящиком «…». Клик — кряк и реплика.
            4 клика за 3 с — злится и уплывает за край окна. 3 побега за 5 минут — финал с роликом.
          </div>
          <Button onClick={() => setFinale(true)}>Показать финал</Button>
        </div>
        <div id="duck-rail-demo" style={{ display: 'flex', height: 560 }}>
          <PanelRail
            side="right"
            hat="Панели"
            groups={GROUPS}
            overflow={{ items: [], badge: 7 }}
            tail={<RailDuck />}
          />
        </div>
      </div>
      {finale && <DuckFinale onClose={() => setFinale(false)} />}
    </Island>
  );
}
