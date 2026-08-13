// Знакомство (фича default-personas-onboarding): личное интервью дорабатывает уже
// провижнутую заготовку-ассистента «Ассистент» (вторую персону не создаёт), проектное —
// собирает руководителя проекта с нуля. Больше не гейт: overlay-страница поверх обычной
// навигации (маршрут — App.tsx, событие OPEN_INTRO_EVENT, план §4, п.4.5), выйти можно
// в любой момент кнопкой «Позже» — разговор сохранится и продолжится с этого же места.
// Внутри — существующий чат-стек (ChatPanel/useSession) поверх сессии из
// POST /api/onboarding/*/start. Гейт снимается ПО КОНЦУ ХОДА (result после
// onboarding_completed) или кнопкой «Перейти в систему» — не размонтируем чат посреди
// стрима и приветствия персоны.

import { useCallback, useEffect, useRef, useState } from 'react';
import { ArrowLeft, ArrowRight, Info, RotateCcw, Sparkles, UserRound } from 'lucide-react';
import type { AuthState, Persona, Project, Session } from '../../types';
import { api } from '../../lib/api';
import { C, CHAT_MAX_W, FONT, FS, R, SP, Z } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, Modal, ModalActions, ConfirmDialog } from '../../components/ui';
import { PageCanvas } from '../../components/ui/PageCanvas';
import { HubHeader } from '../../components/HubHeader';
import type { HubTabValue } from '../../components/HubTabs';
import { ChatPanel } from '../../components/ChatPanel';
import { onMessage } from '../../lib/signalr';
import { showToast } from '../../lib/toast';
import { useIsMobile } from '../../lib/breakpoints';
import { refreshMe } from '../../lib/defaultPersona';
import { bumpPersonas, personaLabel } from '../../lib/personas';
import { PersonaAvatar } from '../personas/PersonaAvatar';

// Событие открытия оверлея знакомства: без detail — личное, с { projectId } — проектное.
// Диспетчеры — карточка-приглашение в «Персонах» и строка в настройках проекта (волна 5);
// слушатель — App.tsx (план §4, п.4.5).
export const OPEN_INTRO_EVENT = 'cc-open-intro';

// Транскрипт сессии → текстовый промпт для quick-create (fallback «из разговора»,
// только для проектного знакомства — у него нет готовой заготовки для доработки).
// История приходит сырыми записями бэка; берём только реплики человека и ассистента.
function transcriptToPrompt(raw: unknown[]): string {
  const lines: string[] = [];
  for (const entry of raw) {
    const e = entry as { kind?: string; text?: string } | null;
    if (!e?.text) continue;
    if (e.kind === 'user_message') lines.push(`Пользователь: ${e.text}`);
    else if (e.kind === 'text') lines.push(`Интервьюер: ${e.text}`);
  }
  const talk = lines.join('\n').slice(0, 12_000);
  return `По этому разговору-интервью придумай персону-руководителя этого проекта: имя, роль, характер, приветствие.\n\n${talk}`;
}

const PLAQUE_TEXTS = {
  user: {
    title: 'Давайте познакомимся',
    body: 'Ваш ассистент уже работает — просто пока без имени. Сейчас несколько вопросов о вас и ваших делах: по ответам он получит имя, характер и аватар и будет помнить контекст.',
    footnote: 'Новое имя появится и в чатах, которые вы уже с ним начали. Прервать можно в любой момент — разговор продолжится с этого же места.',
  },
  project: {
    title: 'Расскажите о проекте',
    body: 'Пока у проекта нет руководителя, чаты ведёт ваш личный ассистент — без командных механик. Несколько вопросов о проекте, и у него появится руководитель: персона, которая знает контекст и ведёт команду.',
    footnote: 'Прервать можно в любой момент — разговор продолжится с этого же места.',
  },
} as const;

// Вводная плашка в пустой ленте: объясняет, что сейчас произойдёт, пока первый ход
// мастера (kickoff-затравка) в пути. Идёт как greetingBubble — чисто визуальная: уходит
// с первой репликой и не мелькает при резюме прерванного интервью (там в ленте уже есть
// история). Разбита на обещание и сноску — оговорки не должны стоять вровень с приглашением.
function IntroPlaque({ kind, isMobile }: { kind: 'user' | 'project'; isMobile: boolean }) {
  const texts = PLAQUE_TEXTS[kind];
  return (
    <div style={{
      flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
      padding: isMobile ? SP.lg : SP.xl,
    }}>
      <div style={{
        maxWidth: 460, width: '100%',
        padding: isMobile ? SP.lg : SP.xl,
        background: C.accentLight, border: `1px solid ${C.border}`, borderRadius: R.xxl,
        display: 'flex', flexDirection: 'column', gap: SP.md,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, color: C.accent }}>
          <Sparkles size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          <div style={{ fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
            {texts.title}
          </div>
        </div>
        <div style={{ fontSize: FS.md, lineHeight: 1.55, color: C.textPrimary }}>
          {texts.body}
        </div>
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: SP.sm,
          paddingTop: SP.md, borderTop: `1px solid ${C.accentMuted}`,
          fontSize: FS.sm, lineHeight: 1.5, color: C.textMuted,
        }}>
          <Info size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 2 }} />
          <span>{texts.footnote}</span>
        </div>
      </div>
    </div>
  );
}

// Общий каркас чата знакомства: старт/резюм сессии, чат-стек, завершение, страховки.
// kind='user' дорабатывает существующую заготовку-ассистента через apply-transcript;
// kind='project' создаёт персону-руководителя с нуля через quick-create (заготовки нет).
function IntroChatShell({ kind, title, subtitle, project, start, onDone }: {
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
  // Знакомство завершено (дефолт назначен) — ждём конца хода или кнопки
  const [completed, setCompleted] = useState(false);
  const completedRef = useRef(false);
  const [restartConfirm, setRestartConfirm] = useState(false);
  const [restarting, setRestarting] = useState(false);
  // Личное знакомство: «Применить итоги разговора» дорабатывает заготовку сервером,
  // без предпросмотра черновика — финал приходит тем же broadcast'ом, что и обычное
  // завершение интервью (эффект на sessionId ниже переводит гейт в completed).
  const [applyConfirm, setApplyConfirm] = useState(false);
  // Причина отказа «Применить итоги»: живёт в самом диалоге, чтобы отказ нельзя было
  // не заметить — диалог остаётся открытым с текстом и кнопкой «Повторить»
  const [applyError, setApplyError] = useState<string | null>(null);
  const [applying, setApplying] = useState(false);
  // Проектное знакомство: «Создать персону из разговора» — старый путь через quick-create
  // (заготовки-руководителя не существует, применять итоги не к чему) + подтверждение черновика.
  const [draft, setDraft] = useState<Persona | null>(null);
  const [drafting, setDrafting] = useState(false);

  const load = useCallback(() => {
    setError(null);
    start()
      .then(setSession)
      .catch(e => setError(e instanceof Error ? e.message : 'Не удалось запустить знакомство'));
  }, [start]);
  // Сессию знакомства заводим ровно один раз при монтировании; перезапуск — через restart()
  const startedRef = useRef(false);
  useEffect(() => {
    if (startedRef.current) return;
    startedRef.current = true;
    load();
  }, [load]);

  // Завершение: onboarding_completed помечает (и освежает me/персон) — источник события
  // не важен: и конец обычного интервью, и apply-transcript шлют один и тот же тип.
  // Гейт снимаем по концу хода (result), чтобы приветствие персоны успело прозвучать.
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
      showToast('Знакомство', e instanceof Error ? e.message : 'Не удалось перезапустить интервью');
    } finally {
      setRestarting(false);
      setRestartConfirm(false);
    }
  };

  // Личное знакомство: применить итоги разговора к заготовке, не дожидаясь конца интервью
  const applyTranscript = async () => {
    if (applying) return;
    setApplying(true);
    setApplyError(null);
    try {
      await api.onboarding.applyTranscript();
      setApplyConfirm(false);
    } catch (e) {
      // Диалог при отказе НЕ закрываем и пишем причину прямо в нём. Тоста мало: он
      // мелькает, а на экране после закрытия диалога ничего не меняется — человек решает,
      // что кнопка не работает (QA поймал это как молчаливый отказ на 502). Заодно
      // переводим технические ответы шлюза на человеческий: «Bad Gateway» и обрыв по
      // таймауту значат одно и то же — сервер не успел, стоит повторить.
      const status = (e as Error & { status?: number }).status;
      const raw = e instanceof Error ? e.message : '';
      setApplyError(
        status === 404
          ? 'Нечего применять: разговор ещё не начат или ассистент уже настроен.'
          : status !== undefined && status >= 500 || !raw || /gateway|timeout|failed to fetch/i.test(raw)
            ? 'Сервер не успел собрать ассистента по разговору. Попробуйте ещё раз или продолжите интервью — знакомство не потеряно.'
            : raw,
      );
    } finally {
      setApplying(false);
    }
  };

  // Проектное знакомство: персона из транскрипта разговора (quick-create) + подтверждение
  const createFromTalk = async () => {
    if (!session || drafting) return;
    setDrafting(true);
    try {
      const raw = session.projectId
        ? await api.sessions.getHistory(session.projectId, session.id)
        : await api.chats.getHistory(session.id);
      const persona = await api.personas.quickCreate({
        prompt: transcriptToPrompt(raw),
        scope: 'project',
        projectId: project?.id,
      });
      setDraft(persona);
    } catch (e) {
      showToast('Знакомство', e instanceof Error ? e.message : 'Не удалось создать персону из разговора');
    } finally {
      setDrafting(false);
    }
  };

  // Подтверждение черновика: назначаем дефолтом REST-ом и выходим
  const confirmDraft = async () => {
    if (!draft) return;
    try {
      await api.personas.makeDefault(draft.id);
      await refreshMe();
      bumpPersonas();
      setDraft(null);
      onDone();
    } catch (e) {
      showToast('Знакомство', e instanceof Error ? e.message : 'Не удалось назначить персону по умолчанию');
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
      flex: 1, minHeight: 0,
      display: 'flex', flexDirection: 'column', overflow: 'hidden',
    }}>
      {/* Шапка: что происходит и зачем; «Позже» — детерминированный выход в любой момент.
          Обычная навигация хаба выше (HubHeader из IntroChatPage/ProjectIntroChatPage) —
          уйти можно и кликом по любому разделу, «Позже» — явная подсказка того же выхода. */}
      <div style={{
        flex: 'none', display: 'flex', alignItems: 'center', gap: SP.md,
        // На узком экране кнопки-страховки не влезают в ряд с заголовком —
        // переносим их на вторую строку вместо overflow
        flexWrap: 'wrap', rowGap: SP.sm,
        // Геометрия как у обычной шапки чата (ChatHeaderBar): ширина шапки = колонке
        // ленты, иначе заголовок уезжает от центрированного чата
        width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto', boxSizing: 'border-box',
        padding: isMobile ? `${SP.md}px ${SP.lg}px` : `${SP.lg}px ${SP.xl}px`,
        borderBottom: `1px solid ${C.border}`,
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
            {kind === 'user' ? (
              <Button variant="ghost" size="sm" loading={applying}
                leftIcon={<UserRound size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                title="Применить то, что уже рассказано, не дожидаясь конца интервью"
                onClick={() => setApplyConfirm(true)}>
                {isMobile ? 'Применить итоги' : 'Применить итоги разговора'}
              </Button>
            ) : (
              <Button variant="ghost" size="sm" loading={drafting}
                leftIcon={<UserRound size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                title="Создать персону по уже состоявшемуся разговору, не дожидаясь финала интервью"
                onClick={() => void createFromTalk()}>
                {isMobile ? 'Из разговора' : 'Создать персону из разговора'}
              </Button>
            )}
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
        {/* «Позже» — не зависит от session/completed: выйти можно и пока идёт загрузка,
            и при ошибке старта. Разговор сохранён, продолжится с этого же места. */}
        <span style={{ width: 1, height: 20, background: C.border, flexShrink: 0 }} />
        <Button variant="ghost" size="sm"
          leftIcon={<ArrowLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          title="Выйти — разговор сохранится и продолжится с этого места"
          onClick={onDone}>
          Позже
        </Button>
      </div>

      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', position: 'relative', overflowY: 'auto' }}>
        {session ? (
          <ChatPanel
            key={session.id}
            session={session}
            project={project}
            isMobile={isMobile}
            onSessionUpdated={setSession}
            attachedFiles={attachedFiles}
            onAttachedFilesChange={setAttachedFiles}
            greetingBubble={<IntroPlaque kind={kind} isMobile={isMobile} />}
          />
        ) : error ? (
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: SP.md, padding: SP.xl, textAlign: 'center' }}>
            <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading }}>Не удалось запустить знакомство</div>
            <div style={{ fontSize: FS.sm, color: C.textSecondary, maxWidth: 420 }}>{error}</div>
            <Button variant="primary" size="md" onClick={load}>Повторить</Button>
          </div>
        ) : (
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, fontSize: FS.sm }}>
            Открываем интервью…
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

      {applyConfirm && (
        <ConfirmDialog
          title="Применить итоги разговора?"
          subtitle={applyError
            ?? 'Ассистент получит имя, характер и аватар по уже сказанному, не дожидаясь конца интервью.'}
          confirmLabel={applyError ? 'Повторить' : 'Применить'}
          onConfirm={applyTranscript}
          onCancel={() => { setApplyConfirm(false); setApplyError(null); }}
        />
      )}

      {/* Подтверждение персоны, созданной из разговора (только проектное знакомство) */}
      {draft && (
        <Modal
          title="Персона готова"
          subtitle="Проверьте, кто получился по разговору — она станет руководителем проекта."
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

interface IntroPageProps {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
  onDone: () => void;
}

// Личное знакомство: интервью дорабатывает уже провижнутую заготовку-ассистента.
// Overlay-страница по образцу ProductHistory: обычная навигация хаба сверху — видно,
// что мир на месте, уйти можно кликом по любому разделу (тот же жест закрывает overlay).
export function IntroChatPage({ auth, onLogout, onHubTab, onDone }: IntroPageProps) {
  return (
    <PageCanvas style={{ position: 'fixed', inset: 0, zIndex: Z.modal }}>
      <HubHeader value="home" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <IntroChatShell
        kind="user"
        title="Знакомство"
        subtitle="Несколько вопросов — и ваш ассистент получит имя, характер и ваш контекст. Выйти можно в любой момент."
        start={() => api.onboarding.startUser()}
        onDone={onDone}
      />
    </PageCanvas>
  );
}

// Проектное знакомство: интервью собирает персону-руководителя проекта
export function ProjectIntroChatPage({ project, auth, onLogout, onHubTab, onDone }: IntroPageProps & { project: Project }) {
  return (
    <PageCanvas style={{ position: 'fixed', inset: 0, zIndex: Z.modal }}>
      <HubHeader value="home" onTab={onHubTab} auth={auth} onLogout={onLogout} project={project} />
      <IntroChatShell
        kind="project"
        title={`Знакомство с проектом «${project.name}»`}
        subtitle="Расскажите о проекте — по интервью появится его руководитель, персона по умолчанию для чатов проекта."
        project={project}
        start={() => api.onboarding.startProject(project.id)}
        onDone={onDone}
      />
    </PageCanvas>
  );
}
