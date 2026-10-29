// Метка «общий проект» — плашка info рядом с именем проекта: «общий» на своём проекте
// (если в нём есть участники) или «проект {имя владельца}» на чужом.
//
// Зачем отдельный компонент: метка нужна в четырёх местах (строка, карточка, виджет,
// палитра — спецификация shared-projects-v2-proposal.md п.6), и решение «общий ли
// это проект, чей, что писать» обязано говориться везде одинаково. Здесь только метка:
// права участников решает бэк (ProjectAccess), фронт показывает то, что сказано в данных.
//
// Источник данных: Project.members/myRole — резервированные поля S1-F2: бэк их пока
// не отдаёт, поэтому сегодня «свой/чужой» определяется по ownerId против меня. Имени
// владельца в DTO проекта нет — его приносит вызывающий из того, что есть у него;
// имени нет — говорим «чужой проект». Имя берётся как есть: его падеж — забота
// вызывающего, здесь нет русской склонялки.

import { Users } from 'lucide-react';
import { C } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { ICON_PROPS, ICON_SIZE } from './ui/icons';
import { Badge } from './ui/Badge';
import type { ProjectMember, ProjectRoleView } from '../types';

// Поля проекта, которые метка читает. Берём только их: компоненту не нужен весь DTO —
// довольно того, что есть у вызывающего (S1-F5).
export interface SharedProjectData {
  ownerId?: string | null;
  members?: ProjectMember[] | null;
  myRole?: ProjectRoleView;
}

export interface SharedBadgeProps {
  project: SharedProjectData;
  // Кому принадлежит взгляд: без него свой и чужой по ownerId не различить.
  // Не задан — проект считаем своим (старые данные, где членства не было).
  currentUserId?: string | null;
  // Имя владельца — из данных проекта, его приносит вызывающий. Пусто — «чужой проект».
  ownerName?: string | null;
  // xs — плотные строки списков (спека п.6), sm — шапки и формы.
  size?: 'xs' | 'sm';
}

type Kind = 'own' | 'foreign';

// Какого рода проект: null — не общий, метки нет.
function kindOf(project: SharedProjectData, currentUserId?: string | null): Kind | null {
  // myRole авторитетна, когда есть (резервированное поле S1-F2, следующий шаг бэка)
  if (project.myRole === 'editor' || project.myRole === 'reader') return 'foreign';
  if (project.myRole === 'owner') return ownKind(project);
  // myRole бэк не отдаёт — различаем по владельцу.
  if (currentUserId && project.ownerId) {
    return project.ownerId === currentUserId ? ownKind(project) : 'foreign';
  }
  // Ни владельца, ни меня: старые данные без членства — проект свой.
  return ownKind(project);
}

// Свой проект общий, только если в нём есть участники; без них метки нет.
function ownKind(project: SharedProjectData): Kind | null {
  return (project.members?.length ?? 0) > 0 ? 'own' : null;
}

/**
 * Метка общего проекта. Не общий проект — не рендерится вовсе (null), а не пустая плашка.
 * На мобиле остаётся один значок: смысл несут title/aria-label (свой текст «общий» там
 * нечитаем без плашки, поэтому подсказка — полная фраза).
 */
export function SharedBadge({ project, currentUserId, ownerName, size = 'xs' }: SharedBadgeProps) {
  // Хук до любого выхода: когда метка появляется/исчезает от обновления данных,
  // порядок хуков не должен съехать
  const isMobile = useIsMobile();
  const kind = kindOf(project, currentUserId);
  if (!kind) return null;

  const name = ownerName?.trim();
  const text = kind === 'foreign' ? (name ? `проект ${name}` : 'чужой проект') : 'общий';
  const label = kind === 'foreign' ? text : 'общий проект';

  if (isMobile) {
    // Значок один и без плашки — он заметнее строкового: берем sm, не xs
    return (
      <span title={label} aria-label={label} role="img" style={{
        display: 'inline-flex', flexShrink: 0, color: C.info,
        minWidth: ICON_SIZE.sm, minHeight: ICON_SIZE.sm,
      }}>
        <Users {...ICON_PROPS} size={ICON_SIZE.sm} />
      </span>
    );
  }

  return (
    <Badge tone="info" size={size} icon={<Users {...ICON_PROPS} size={ICON_SIZE.xs} />}>
      {text}
    </Badge>
  );
}
