// Х-W4-79: пустое состояние центра ребёнка (урок не выбран). Вместо взрослого
// «С чего начнём? / Новый чат» — «Выбери урок или начни новый» и «Новый урок».
// «Новый урок» здесь — та же единственная точка создания урока (Б9b), что в доке
// ProjectRail: childApi.createLesson(предмет) → dock.onLessonCreated(sessionId);
// взрослое создание чата ребёнку не отдаётся.

import { useState } from 'react';
import { BookOpen, Plus } from 'lucide-react';
import { Button } from '../../components/ui';
import { EmptyState } from '../../components/ui/EmptyState';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { showToast } from '../../lib/toast';
import { childApi } from './api';
import type { ChildSubjectsDock } from '../projects/ProjectRail';

export function ChildCenterEmpty({ dock }: { dock?: ChildSubjectsDock }) {
  const [creating, setCreating] = useState(false);
  const subjectId = dock?.activeSubjectId ?? null;

  async function newLesson() {
    if (!subjectId || creating) return;   // двойной тап: второй игнорируется, пока первый в полёте
    setCreating(true);
    try {
      const r = await childApi.createLesson(subjectId);
      dock?.onLessonCreated?.(r.sessionId);
    } catch (e) {
      const msg = (e as { body?: { message?: string } }).body?.message ?? 'Сейчас урок не получится начать. Попробуй чуть позже.';
      showToast('Новый урок', msg);
    } finally {
      setCreating(false);
    }
  }

  return (
    <div data-testid="child-center-empty" style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <EmptyState
        icon={<BookOpen size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
        title="Выбери урок или начни новый"
        action={(
          <Button
            variant="primary" size="md" glow loading={creating} disabled={!subjectId}
            dataTestId="child-center-new-lesson" style={{ minWidth: 40 }}
            leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            onClick={() => { void newLesson(); }}
          >
            Новый урок
          </Button>
        )}
      />
    </div>
  );
}
