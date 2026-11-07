// Стопка аватаров участников в крошке проекта (S1-F6, карта v2 п.9): до трёх кружков
// с нахлёстом и «+N». Клик ведёт в настройки проекта на «Участники». Данные — сводка
// project.members ({ count, first }); полного списка здесь нет и не нужно.

import { C, FS } from '../../../lib/design';
import { UserAvatar } from '../../../components/ui/UserAvatar';
import { PressableArea } from '../../../components/ui/PressableArea';
import type { Project } from '../../../types';

const MAX_SHOWN = 3;
// Нахлёст кружков и кольцо цветом подложки шапки (карта v2, «Стопка аватаров»)
const OVERLAP = -5;

interface Props {
  project: Pick<Project, 'members'>;
  onOpen: () => void;
}

export function MembersStack({ project, onOpen }: Props) {
  const summary = project.members;
  const count = summary?.count ?? 0;
  if (!summary || count <= 0) return null;
  const shown = summary.first.slice(0, MAX_SHOWN);
  const more = count - shown.length;
  const title = `Участники проекта — ${count}`;
  return (
    <PressableArea
      onPress={onOpen}
      title={title}
      aria-label={title}
      feedback
      data-testid="members-stack"
      style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0, padding: 3, borderRadius: 999 }}
    >
      {shown.map((m, i) => (
        <UserAvatar
          key={m.userId}
          userId={m.userId}
          displayName={m.displayName}
          username={m.username}
          size={20}
          style={{ marginLeft: i === 0 ? 0 : OVERLAP, boxShadow: `0 0 0 2px ${C.bgMain}`, position: 'relative', zIndex: MAX_SHOWN - i }}
        />
      ))}
      {more > 0 && (
        <span style={{ marginLeft: 6, fontSize: FS.xs, color: C.textMuted, fontWeight: 600 }} data-testid="members-stack-more">
          +{more}
        </span>
      )}
    </PressableArea>
  );
}
