// Наполнение контекста с карточек ленты (ADR-023, 2к-2). Карточки нити и версии показывают «Работать с этой»
// и «В контекст ▾», а бейдж «В работе» берут из контекста чата, а не из фокуса нитей.

import { Target } from 'lucide-react';
import {
  Button, ContextAddButton, ICON_SIZE, ICON_STROKE, useChatContext, useIsMobile,
  type ChatContextPrimary,
} from 'aihome_shell/kit';
import { isPersonalScope } from '../scope';
import type { ImageThread } from '../thread/threadsApi';
import { IMAGE_KIND } from './state';
import { imageRefOf, pickInContext, workWithInContext } from './work';

// Версия, которую держит основной объект: явная в ref, иначе текущая версия нити
function primaryVersion(primary: ChatContextPrimary, thread: ImageThread): string | null {
  const v = primary.ref.versionId;
  return typeof v === 'string' ? v : thread.currentVersionId ?? null;
}

export interface CardFill {
  // Эта карточка (нить и версия) — основной объект контекста
  working: boolean;
  // Основной объект выбрал агент: бейдж «В работе ✦»
  byAgent: boolean;
  // Нить этой карточки — основной объект (любая её версия): рамка стопки и «Сохранить» по ней
  threadWorking: boolean;
  work: () => Promise<boolean>;
  pick: () => Promise<void>;
}

// versionId = null — карточка всей нити (стопка), версия не важна
export function useCardFill(sessionId: string, thread: ImageThread, versionId: string | null): CardFill {
  const { primary } = useChatContext(sessionId);
  const mobile = useIsMobile();
  const threadWorking = primary?.kind === IMAGE_KIND && primary.ref.threadId === thread.id;
  const working = threadWorking && (versionId === null || primaryVersion(primary!, thread) === versionId);
  return {
    working,
    byAgent: working && primary?.by === 'agent',
    threadWorking,
    work: () => workWithInContext(sessionId, thread.id, versionId, !mobile),
    pick: () => pickInContext(sessionId, thread.id, versionId, working),
  };
}

// Нить в работе для лент-якорей — из контекста чата
export function useFocusedThreadId(sessionId: string): string | null {
  const { primary } = useChatContext(sessionId);
  return primary?.kind === IMAGE_KIND && typeof primary.ref.threadId === 'string' ? primary.ref.threadId : null;
}

// «Работать с этой» и «В контекст ▾» (или плашка «В контексте · роль»). Объект в работе кнопок не показывает:
// сам себя референсом положить нельзя
export function CardContextActions({ sessionId, projectId, thread, versionId, fill, size = 'xs' }: {
  sessionId: string; projectId: string; thread: ImageThread; versionId: string | null; fill: CardFill; size?: 'xs' | 'sm';
}) {
  if (fill.working) return null;
  const candidate = imageRefOf(thread.id, versionId);
  return (
    <>
      <Button size={size} variant="secondary" leftIcon={<Target size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        title="Картинка станет основной в контексте хода: чипы действий и панель «Контекст»"
        onClick={() => { void fill.work(); }}>
        Работать с этой
      </Button>
      <ContextAddButton sessionId={sessionId} projectId={isPersonalScope(projectId) ? null : projectId} candidate={candidate} size={size} />
    </>
  );
}
