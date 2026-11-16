// Блок «Вопросы к вам» экрана этапа и области этапа страницы фичи (F13, макет stage-screen.html
// S13–S15, feature-page.html P44/P45). Открытый вопрос — карточка: тег «вопрос», аватар, «Глеб ·
// 12.11 17:20 · ждёт 35 мин», текст, поле ответа, «Ответить». Отвеченный — строка «отвечено».
// Нет вопросов — пустое состояние.
// Вопрос и ответ пишет исполнитель/ревьюер в markdown — на экране показываем плоский текст через
// markdownToPlain (как в chat/AskQuestionView): служебные символы разметки («**», обратные кавычки)
// читателю не нужны, полный блочный рендер коротким строкам не нужен.
// Ответ пишет человека на сервер, поэтому:
//  · отправка одна на вопрос: guard — модульный Set по id вопроса, а не состояние экземпляра
//    (два быстрых клика и два экземпляра блока на один вопрос шлют один POST);
//  · ответ, пришедший после ухода с этапа (размонтирование), не применяется и не перезапрашивает экран;
//  · отказ POST не бросает исключение наружу — ошибка показывается под полем.

import { useEffect, useRef, useState } from 'react';
import { CheckCircle2 } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, FONT, FS, R, SP, TB } from '../../lib/design';
import { Badge } from '../ui/Badge';
import { Button } from '../ui/Button';
import { EmptyState } from '../ui/EmptyState';
import { TextArea } from '../ui/Field';
import { api } from '../../lib/api';
import { formatDuration } from '../../lib/chatGroups';
import { personaInitials } from '../../lib/personas';
import { markdownToPlain } from '../../lib/markdownPlain';
import { SEC_LABEL } from './FeatureStageArtifacts';
import { formatClock, formatDate } from './FeatureStatsBits';
import type { StageQuestion } from '../../types/featureStage';

// Идущие отправки: ключ — id вопроса (общий на все экземпляры блока).
const inFlight = new Set<string>();

// Подпись ответа: пустой text — «Ответили в чате» (ответ пришёл сообщением, текста в событии нет).
// Текст ответа — markdown, разметку снимаем тем же markdownToPlain, что и у вопроса.
export function answerText(q: StageQuestion): string {
  const t = markdownToPlain(q.answer?.text ?? '');
  return t ? `«${t}»` : 'Ответили в чате';
}

function whenLine(q: StageQuestion): string {
  const date = [formatDate(q.askedAt), formatClock(q.askedAt)].filter(Boolean).join(' ');
  const wait = q.waitedMinutes != null ? `ждёт ${formatDuration(q.waitedMinutes)}` : '';
  return [q.askedBy.name, date, wait].filter(Boolean).join(' · ');
}

function OpenCard({ q, taskId, isMobile, onAnswered }: { q: StageQuestion; taskId: string; isMobile: boolean; onAnswered: () => void }) {
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  const send = () => {
    const value = text.trim();
    if (!value || inFlight.has(q.id)) return;
    inFlight.add(q.id);
    setSending(true);
    setError(null);
    // Исполнитель промиса синхронный: вызов уходит сразу, синхронное исключение api станет отказом
    new Promise<unknown>(resolve => resolve(api.tasks.answerStageQuestion(taskId, q.id, value)))
      .then(() => { if (alive.current) onAnswered(); })
      .catch((e: unknown) => {
        if (!alive.current) return;
        setError((e instanceof Error && e.message) || 'Не удалось отправить ответ');
      })
      .finally(() => {
        inFlight.delete(q.id);
        if (alive.current) setSending(false);
      });
  };

  return (
    <div data-testid={`stage-question-${q.id}`} style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0,
      padding: SP.sm, background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <Badge tone="warning" size="xs">вопрос</Badge>
        <span aria-hidden style={{
          width: 20, height: 20, borderRadius: R.full, background: C.bgSelected, color: C.textSecondary,
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center', fontSize: FS.xs, fontWeight: 600,
        }}>{personaInitials(q.askedBy.name)}</span>
        <span data-testid="stage-question-when" style={{ flex: '1 1 160px', minWidth: 0, fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>{whenLine(q)}</span>
      </div>
      <div style={{ fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{markdownToPlain(q.text)}</div>
      <TextArea value={text} onChange={setText} minHeight={56} autoGrow disabled={sending} isMobile={isMobile}
        placeholder="Ваш ответ — уйдёт исполнителю и ревьюеру" />
      {error && <div data-testid="stage-question-error" role="alert" style={{ fontSize: FS.xs, color: C.dangerText }}>{error}</div>}
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
        <Button size="sm" variant="primary" dataTestId="stage-question-send" loading={sending}
          disabled={!text.trim()} style={isMobile ? { minHeight: TB.iconHitMobile } : undefined} onClick={send}>
          Ответить
        </Button>
      </div>
    </div>
  );
}

function AnsweredRow({ q }: { q: StageQuestion }) {
  return (
    <div data-testid={`stage-question-${q.id}`} style={{
      display: 'flex', alignItems: 'flex-start', gap: SP.sm, flexWrap: 'wrap', minWidth: 0,
      padding: SP.sm, background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
    }}>
      <Badge tone="success" size="xs">отвечено</Badge>
      <span data-testid="stage-question-answered-text" style={{ flex: '1 1 200px', minWidth: 0, fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans, overflowWrap: 'anywhere' }}>
        {q.askedBy.name}: «{markdownToPlain(q.text)}» — {answerText(q)}
      </span>
    </div>
  );
}

export function FeatureStageQuestions({ taskId, questions, isMobile, onAnswered, showEmpty = true }: {
  taskId: string; questions: StageQuestion[]; isMobile: boolean; onAnswered: () => void; showEmpty?: boolean;
}) {
  if (questions.length === 0 && !showEmpty) return null;
  return (
    <section data-testid="stage-questions" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={SEC_LABEL}>Вопросы к вам</div>
      {questions.length === 0 ? (
        <div data-testid="stage-questions-empty">
          <EmptyState compact inline icon={<CheckCircle2 size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
            title="Вопросов к вам нет"
            subtitle="Если исполнитель или ревьюер спросит — вопрос появится здесь и на странице фичи." />
        </div>
      ) : (
        questions.map(q => q.status === 'open'
          ? <OpenCard key={q.id} q={q} taskId={taskId} isMobile={isMobile} onAnswered={onAnswered} />
          : <AnsweredRow key={q.id} q={q} />)
      )}
    </section>
  );
}
