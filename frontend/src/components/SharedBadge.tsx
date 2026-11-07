// Метка «общий проект» — плашка info рядом с именем проекта: «общий» на своём проекте
// (если в нём есть участники) или «проект {имя владельца}» на чужом.
//
// Зачем отдельный компонент: метка нужна в четырёх местах (строка, карточка, виджет,
// палитра — спецификация shared-projects-v2-proposal.md п.6), и решение «общий ли
// это проект, чей, что писать» обязано говориться везде одинаково. Здесь только метка:
// права участников решает бэк (ProjectAccess), фронт показывает то, что сказано в данных.
//
// Источник данных — поля DTO проекта (S1-2b, ProjectsController.WithCount → ProjectMembersView.Summary):
// members — сводка { count, first }, myRole — роль вызывающего, ownerUsername/ownerDisplayName —
// имя владельца. У обычного проекта, где вызывающий владелец, бэк отдаёт все эти поля null —
// метки нет. Имя берётся как есть: его падеж — забота вызывающего, здесь нет русской склонялки.

import { Users } from 'lucide-react';
import { C } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { ICON_PROPS, ICON_SIZE } from './ui/icons';
import { Badge } from './ui/Badge';
import type { ProjectMembersSummary, ProjectRoleView } from '../types';

// Поля проекта, которые метка читает. Берём только их: компоненту не нужен весь DTO —
// довольно того, что есть у вызывающего (S1-F5).
export interface SharedProjectData {
  ownerId?: string | null;
  // Имя владельца из данных проекта (Summary): «проект {displayName}», запасной — username.
  ownerDisplayName?: string | null;
  ownerUsername?: string | null;
  // Состав участников — сводка из DTO, не список: «есть ли участники» решается по count.
  members?: ProjectMembersSummary | null;
  myRole?: ProjectRoleView;
}

export interface SharedBadgeProps {
  project: SharedProjectData;
  // Кому принадлежит взгляд: без него свой и чужой по ownerId не различить и любой
  // чужой проект сочтётся своим. Обязателен: вызывающий обязан назвать взгляд (ревью M-37).
  currentUserId: string | null;
  // xs — плотные строки списков (спека п.6), sm — шапки и формы.
  size?: 'xs' | 'sm';
}

type Kind = 'own' | 'foreign';

// Какого рода проект: null — не общий, метки нет.
function kindOf(project: SharedProjectData, currentUserId: string | null): Kind | null {
  // myRole авторитетна, когда есть (Summary отдаёт её для общих проектов)
  if (project.myRole === 'editor' || project.myRole === 'reader') return 'foreign';
  if (project.myRole === 'owner') return ownKind(project);
  // myRole нет (старый бэк): различаем по владельцу.
  if (currentUserId && project.ownerId) {
    return project.ownerId === currentUserId ? ownKind(project) : 'foreign';
  }
  // Ни владельца, ни меня: старые данные без членства — проект свой.
  return ownKind(project);
}

// Свой проект общий, только если в нём есть участники; без них метки нет.
// Состав — сводка бэка: «есть» это count > 0 (ревью M-2).
function ownKind(project: SharedProjectData): Kind | null {
  return (project.members?.count ?? 0) > 0 ? 'own' : null;
}

/**
 * Метка общего проекта. Не общий проект — не рендерится вовсе (null), а не пустая плашка.
 * На мобиле остаётся один значок: смысл несут title/aria-label (свой текст «общий» там
 * нечитаем без плашки, поэтому подсказка — полная фраза).
 */
export function SharedBadge({ project, currentUserId, size = 'xs' }: SharedBadgeProps) {
  // Хук до любого выхода: когда метка появляется/исчезает от обновления данных,
  // порядок хуков не должен съехать
  const isMobile = useIsMobile();
  const kind = kindOf(project, currentUserId);
  if (!kind) return null;

  // Имя — из данных проекта (ревью M-1): displayName, запасной — username; нет обоих — «чужой проект».
  const name = project.ownerDisplayName?.trim() || project.ownerUsername?.trim();
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
