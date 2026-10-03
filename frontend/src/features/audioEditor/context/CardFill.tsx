// Наполнение контекста с карточек ленты (ADR-023, 2з-3). При флаге composer-context-row карточка версии звука
// показывает «Работать с этой» и «В контекст ▾» вместо «Обработать ▾», а бейдж «В работе» берёт из контекста
// чата, а не из фокуса нитей.

import { Target } from 'lucide-react';
import {
  Button, ContextAddButton, FLAGS, ICON_SIZE, ICON_STROKE, useChatContext, useFeature, useIsMobile,
  type ChatContextPrimary,
} from 'aihome_shell/kit';
import type { AudioThread } from '../api';
import { isPersonalScope } from '../scope';
import { audioRefOf, pickInContext, workWithInContext } from './work';
import { AUDIO_KIND } from './state';

// Версия, которую держит основной объект: явная в ref, иначе текущая версия нити
function primaryVersion(primary: ChatContextPrimary, thread: AudioThread): string | null {
  const v = primary.ref.versionId;
  return typeof v === 'string' ? v : thread.currentVersionId ?? null;
}

export interface CardFill {
  // Флаг включён: карточка живёт по контексту
  on: boolean;
  // Эта карточка (нить и версия) — основной объект контекста
  working: boolean;
  // Основной объект выбрал агент: бейдж «в работе ✦»
  byAgent: boolean;
  // Нить этой карточки — основной объект (любая её версия)
  threadWorking: boolean;
  work: () => Promise<boolean>;
  pick: () => Promise<void>;
}

export function useCardFill(sessionId: string, thread: AudioThread, versionId: string): CardFill {
  const on = useFeature(FLAGS.composerContextRow);
  const { primary } = useChatContext(on ? sessionId : null);
  const mobile = useIsMobile();
  const threadWorking = on && primary?.kind === AUDIO_KIND && primary.ref.threadId === thread.id;
  const working = threadWorking && primaryVersion(primary!, thread) === versionId;
  // Текущую версию основной объект не закрепляет: после запуска текущей станет новая версия, и чипы действий
  // (стемы, «Свести») пойдут за ней. Закрепляется только прежняя версия — её выбрали осознанно
  const pinned = versionId === thread.currentVersionId ? null : versionId;
  return {
    on,
    working,
    byAgent: working && primary?.by === 'agent',
    threadWorking,
    work: () => workWithInContext(sessionId, thread.id, pinned, !mobile),
    pick: () => pickInContext(sessionId, thread.id, pinned, working),
  };
}

// Нить в работе для пунктирной карточки черновика: при флаге — из контекста чата, иначе из фокуса нитей
export function useFocusedThreadId(sessionId: string | null, focus: string | null): string | null {
  const on = useFeature(FLAGS.composerContextRow);
  const { primary } = useChatContext(on ? sessionId : null);
  if (!on) return focus;
  return primary?.kind === AUDIO_KIND && typeof primary.ref.threadId === 'string' ? primary.ref.threadId : null;
}

// «Работать с этой» и «В контекст ▾» (или плашка «В контексте · роль»). Объект в работе кнопок не показывает:
// сам себя референсом положить нельзя
export function CardContextActions({ sessionId, projectId, thread, versionId, fill }: {
  sessionId: string; projectId: string; thread: AudioThread; versionId: string; fill: CardFill;
}) {
  if (fill.working) return null;
  const candidate = audioRefOf(thread.id, versionId);
  return (
    <>
      <Button size="sm" variant="secondary" leftIcon={<Target size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        title="Звук станет основным в контексте хода: чипы действий и панель «Контекст»"
        onClick={() => { void fill.work(); }}>
        Работать с этой
      </Button>
      <ContextAddButton sessionId={sessionId} projectId={isPersonalScope(projectId) ? null : projectId} candidate={candidate} size="sm" />
    </>
  );
}
