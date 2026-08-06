// Обязательные онбординги (фича default-personas-onboarding): полноэкранный гейт
// первого входа (личная дефолт-персона) и гейт проекта (персона-руководитель).
// Внутри — существующий чат-стек (ChatPanel/useSession) поверх сессии из
// POST /api/onboarding/*/start. Пропустить нельзя; детерминированный выход из гейта:
// «Начать заново» (новая сессия) и fallback «Создать персону из разговора»
// (ai/quick-create по транскрипту + подтверждение + make-default REST-ом).
// Гейт снимается ПО КОНЦУ ХОДА (result после onboarding_completed) или кнопкой
// «Перейти в систему» — не размонтируем чат посреди стрима и приветствия персоны.

import { useCallback, useEffect, useRef, useState } from 'react';
import { ArrowRight, RotateCcw, Sparkles, UserRound } from 'lucide-react';
import type { Persona, Project, Session } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP, Z } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, Modal, ModalActions, ConfirmDialog } from '../../components/ui';
import { CanvasBackdrop } from '../../components/ui/CanvasBackdrop';
import { ChatPanel } from '../../components/ChatPanel';
import { onMessage } from '../../lib/signalr';
import { showToast } from '../../lib/toast';
import { useIsMobile } from '../../lib/breakpoints';
import { refreshMe } from '../../lib/defaultPersona';
import { bumpPersonas, personaLabel } from '../../lib/personas';
import { PersonaAvatar } from '../personas/PersonaAvatar';

// Транскрипт сессии → текстовый промпт для quick-create (fallback «из разговора»).
// История приходит сырыми записями бэка; берём только реплики человека и ассистента.
function transcriptToPrompt(raw: unknown[], kind: 'user' | 'project'): string {
  const lines: string[] = [];
  for (const entry of raw) {
    const e = entry as { kind?: string; text?: string } | null;
    if (!e?.text) continue;
    if (e.kind === 'user_message') lines.push(`Пользователь: ${e.text}`);
    else if (e.kind === 'text') lines.push(`Интервьюер: ${e.text}`);
  }
  const talk = lines.join('\n').slice(0, 12_000);
  const goal = kind === 'user'
    ? 'личного ассистента-координатора пользователя (его персону по умолчанию)'
    : 'персону-руководителя этого проекта';
  return `По этому разговору-интервью придумай ${goal}: имя, роль, характер, приветствие.\n\n${talk}`;
}

// Общий каркас онбординг-чата: старт/резюм сессии, чат-стек, завершение, страховки
function OnboardingChatShell({ kind, title, subtitle, project, start, onDone }: {
  kind: 'user' | 'project';
  title: string;
  subtitle: string;
  project?: Project;
  start: () => Promise<Session>;
  onDone: () => void;
}) {
  const isMobile = useIsMobile();
  const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  // Онбординг завершён (дефолт назначен из сессии) — ждём конца хода или кнопки
  const [completed, setCompleted] = useState(false);
  const completedRef = useRef(false);
  const [restartConfirm, setRestartConfirm] = useState(false);
  const [restarting, setRestarting] = useState(false);
  // Fallback «Создать персону из разговора»: черновик на подтверждении
  const [draft, setDraft] = useState<Persona | null>(null);
  const [drafting, setDrafting] = useState(false);

  const load = useCallback(() => {
    setError(null);
    start()
      .then(setSession)
      .catch(e => setError(e instanceof Error ? e.message : 'Не удалось запустить онбординг'));
  }, [start]);
  useEffect(() => { load(); }, [load]);

  // Завершение: onboarding_completed помечает (и освежает me/персон), гейт снимаем
  // по концу хода (result) — приветствие персоны успевает прозвучать в этом же чате
  const sessionId = session?.id;
  useEffect(() => {
    if (!sessionId) return;
    return onMessage(msg => {
      if (msg.sessionId !== sessionId) return;
      if (msg.type === 'onboarding_completed') {
        completedRef.current = true;
        setCompleted(true);
        void refreshMe();
        bumpPersonas();
      } else if (msg.type === 'result' && completedRef.current) {
        onDone();
      }
    });
  }, [sessionId, onDone]);

  // «Начать заново»: старую сессию удаляем штатным DELETE, бэкенд создаст новую
  const restart = async () => {
    if (!session || restarting) return;
    setRestarting(true);
    try {
      if (session.projectId) await api.sessions.delete(session.projectId, session.id);
      else await api.chats.delete(session.id);
      setSession(null);
      load();
    } catch (e) {
      showToast('Онбординг', e instanceof Error ? e.message : 'Не удалось перезапустить интервью');
    } finally {
      setRestarting(false);
      setRestartConfirm(false);
    }
  };

  // Fallback: персона из транскрипта разговора (quick-create) + подтверждение
  const createFromTalk = async () => {
    if (!session || drafting) return;
    setDrafting(true);
    try {
      const raw = session.projectId
        ? await api.sessions.getHistory(session.projectId, session.id)
        : await api.chats.getHistory(session.id);
      const persona = await api.personas.quickCreate({
        prompt: transcriptToPrompt(raw, kind),
        scope: kind === 'project' ? 'project' : 'global',
        projectId: kind === 'project' ? project?.id : undefined,
      });
      setDraft(persona);
    } catch (e) {
      showToast('Онбординг', e instanceof Error ? e.message : 'Не удалось создать персону из разговора');
    } finally {
      setDrafting(false);
    }
  };

  // Подтверждение черновика: назначаем дефолтом REST-ом и выходим из гейта
  const confirmDraft = async () => {
    if (!draft) return;
    try {
      await api.personas.makeDefault(draft.id);
      await refreshMe();
      bumpPersonas();
      setDraft(null);
      onDone();
    } catch (e) {
      showToast('Онбординг', e instanceof Error ? e.message : 'Не удалось назначить персону по умолчанию');
    }
  };
  // Отказ от черновика — удаляем, интервью продолжается
  const discardDraft = async () => {
    if (!draft) return;
    try { await api.personas.remove(draft.id); } catch { /* черновик остался в разделе персон */ }
    bumpPersonas();
    setDraft(null);
  };

  return (
    <div style={{
      position: 'fixed', inset: 0, zIndex: Z.modal, background: C.bgMain,
      fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden',
    }}>
      <CanvasBackdrop />
      {/* Шапка гейта: что происходит и зачем; навигации и «Пропустить» нет намеренно */}
      <div style={{
        flex: 'none', display: 'flex', alignItems: 'center', gap: SP.md,
        padding: isMobile ? `${SP.md}px ${SP.lg}px` : `${SP.lg}px ${SP.xl}px`,
        borderBottom: `1px solid ${C.border}`, position: 'relative',
      }}>
        <div style={{
          width: 38, height: 38, borderRadius: R.lg, background: C.accentLight, color: C.accent,
          display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        }}>
          <Sparkles size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontFamily: FONT.serif, fontSize: isMobile ? FS.lg : FS.xl, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
            {title}
          </div>
          {!isMobile && (
            <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>{subtitle}</div>
          )}
        </div>
        {/* Страховки от «незавершаемого» интервью */}
        {!completed && session && (
          <>
            <Button variant="ghost" size="sm" loading={drafting}
              leftIcon={<UserRound size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              title="Создать персону по уже состоявшемуся разговору, не дожидаясь финала интервью"
              onClick={() => void createFromTalk()}>
              {isMobile ? 'Из разговора' : 'Создать персону из разговора'}
            </Button>
            <Button variant="ghost" size="sm" loading={restarting}
              leftIcon={<RotateCcw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              onClick={() => setRestartConfirm(true)}>
              Начать заново
            </Button>
          </>
        )}
        {completed && (
          <Button variant="primary" size="sm" glow
            leftIcon={<ArrowRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            onClick={onDone}>
            Перейти в систему
          </Button>
        )}
      </div>

      {/* Чат интервью — существующий чат-стек поверх онбординг-сессии */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', position: 'relative' }}>
        {session ? (
          <ChatPanel
            key={session.id}
            session={session}
            project={project}
            isMobile={isMobile}
            onSessionUpdated={setSession}
            attachedFiles={attachedFiles}
            onAttachedFilesChange={setAttachedFiles}
          />
        ) : error ? (
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: SP.md, padding: SP.xl, textAlign: 'center' }}>
            <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading }}>Не удалось запустить онбординг</div>
            <div style={{ fontSize: FS.sm, color: C.textSecondary, maxWidth: 420 }}>{error}</div>
            <Button variant="primary" size="md" onClick={load}>Повторить</Button>
          </div>
        ) : (
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, fontSize: FS.sm }}>
            Готовим интервью…
          </div>
        )}
      </div>

      {restartConfirm && (
        <ConfirmDialog
          title="Начать интервью заново?"
          subtitle="Текущий разговор будет удалён, интервью начнётся с чистого листа."
          confirmLabel="Начать заново"
          confirmVariant="danger"
          onConfirm={restart}
          onCancel={() => setRestartConfirm(false)}
        />
      )}

      {/* Подтверждение персоны, созданной из разговора */}
      {draft && (
        <Modal
          title="Персона готова"
          subtitle="Проверьте, кто получился по разговору — она станет персоной по умолчанию."
          onClose={() => void discardDraft()}
          footer={
            <ModalActions
              confirmLabel="Сделать персоной по умолчанию"
              cancelLabel="Не подходит"
              onConfirm={() => void confirmDraft()}
              onCancel={() => void discardDraft()}
            />
          }
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.md }}>
            <PersonaAvatar persona={draft} size={56} />
            <div style={{ minWidth: 0 }}>
              <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>
                {personaLabel(draft)}
              </div>
              {draft.description && (
                <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>{draft.description}</div>
              )}
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// Гейт первого входа: обязательное интервью «Мастера настройки» → личная дефолт-персона
export function OnboardingPage({ onDone }: { onDone: () => void }) {
  return (
    <OnboardingChatShell
      kind="user"
      title="Знакомство"
      subtitle="Короткое интервью: расскажите о себе и своих задачах — по нему появится ваш личный ассистент. Это обязательный шаг, он проходится один раз."
      start={() => api.onboarding.startUser()}
      onDone={onDone}
    />
  );
}

// Гейт проекта: интервью личной дефолт-персоны → персона-руководитель проекта
export function ProjectOnboardingGate({ project, onDone }: { project: Project; onDone: () => void }) {
  return (
    <OnboardingChatShell
      kind="project"
      title={`Знакомство с проектом «${project.name}»`}
      subtitle="Расскажите о проекте — по интервью появится его руководитель, персона по умолчанию для чатов проекта. Рабочее пространство откроется после завершения."
      project={project}
      start={() => api.onboarding.startProject(project.id)}
      onDone={onDone}
    />
  );
}
