// Обязательные онбординги (фича default-personas-onboarding): полноэкранный гейт
// первого входа (личная дефолт-персона) и гейт проекта (персона-руководитель).
// Если персоны зоны уже есть — первым идёт шаг выбора готовой персоны, интервью
// стартует только по кнопке «Создать новую в разговоре» (без персон — сразу интервью).
// Внутри — существующий чат-стек (ChatPanel/useSession) поверх сессии из
// POST /api/onboarding/*/start. Пропустить нельзя; детерминированный выход из гейта:
// «Начать заново» (новая сессия) и fallback «Создать персону из разговора»
// (ai/quick-create по транскрипту + подтверждение + make-default REST-ом).
// Гейт снимается ПО КОНЦУ ХОДА (result после onboarding_completed) или кнопкой
// «Перейти в систему» — не размонтируем чат посреди стрима и приветствия персоны.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ArrowRight, RotateCcw, Sparkles, UserRound, Users } from 'lucide-react';
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
import { bumpPersonas, ensurePersonasLoaded, personaLabel, usePersonas } from '../../lib/personas';
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

// Вводная плашка в пустой ленте онбординга: объясняет, что сейчас произойдёт,
// пока первый ход мастера (kickoff-затравка) в пути. Идёт как greetingBubble —
// чисто визуальная: уходит с первой репликой и не мелькает при резюме
// прерванного интервью (там в ленте уже есть история). Иконка и подложка — те
// же, что в шапке гейта, чтобы связь «плашка сверху ⇆ чат» читалась.
function IntroPlaque({ kind, isMobile }: { kind: 'user' | 'project'; isMobile: boolean }) {
  const title = kind === 'user' ? 'Давайте познакомимся' : 'Расскажите о проекте';
  const body = kind === 'user'
    ? 'Сейчас помощник задаст несколько вопросов — о вас и о том, чем вы занимаетесь. По ответам появится ваш личный ассистент: он останется с вами в системе и будет помнить контекст.'
    : 'Ваш ассистент задаст несколько вопросов о проекте. По ответам появится руководитель проекта — персона, которая знает контекст и ведёт команду.';
  return (
    <div style={{
      flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
      padding: isMobile ? SP.lg : SP.xl,
    }}>
      <div style={{
        maxWidth: 420, width: '100%',
        padding: isMobile ? SP.lg : SP.xl,
        background: C.accentLight, border: `1px solid ${C.border}`, borderRadius: R.xxl,
        display: 'flex', flexDirection: 'column', gap: SP.md,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, color: C.accent }}>
          <Sparkles size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          <div style={{ fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
            {title}
          </div>
        </div>
        <div style={{ fontSize: FS.md, lineHeight: 1.55, color: C.textPrimary }}>
          {body}
        </div>
      </div>
    </div>
  );
}

// Тексты шага выбора готовой персоны (он идёт ПЕРЕД интервью, когда персоны зоны уже есть)
const PICK_TEXTS = {
  user: {
    title: 'Кто будет вашим ассистентом?',
    subtitle: 'У вас уже есть персоны — выберите одну, она станет ассистентом по умолчанию. Или создайте новую: помощник задаст несколько вопросов и соберёт её.',
    confirm: 'Сделать ассистентом',
    create: 'Создать новую в разговоре',
  },
  project: {
    title: 'Кто ведёт этот проект?',
    subtitle: 'Выберите руководителя из персон проекта — он будет вести чаты и предлагать командные механики. Или создайте нового в коротком разговоре.',
    confirm: 'Назначить руководителем',
    create: 'Создать нового в разговоре',
  },
} as const;

// Строка персоны в списке выбора — общая для шага выбора и модалки-страховки
function PersonaChoiceRow({ p, selected, busy, disabled, onClick }: {
  p: Persona;
  selected?: boolean;
  busy?: boolean;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <button
      disabled={disabled}
      onClick={onClick}
      onMouseEnter={e => { if (!disabled && !selected) e.currentTarget.style.background = C.accentLight; }}
      onMouseLeave={e => { e.currentTarget.style.background = selected ? C.accentLight : 'transparent'; }}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', textAlign: 'left',
        padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg, fontFamily: FONT.sans,
        background: selected ? C.accentLight : 'transparent',
        border: `1px solid ${selected ? C.accent : C.border}`,
        cursor: disabled ? 'default' : 'pointer',
        opacity: disabled && !busy && !selected ? 0.55 : 1,
        transition: 'background 0.15s, opacity 0.15s, border-color 0.15s',
      }}
    >
      <PersonaAvatar persona={p} size={32} />
      <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
        <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {personaLabel(p)}
        </span>
        {p.description && (
          <span style={{ fontSize: FS.xs, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {p.description}
          </span>
        )}
      </span>
      {busy && <div className="tool-spinner" style={{ width: ICON_SIZE.sm, height: ICON_SIZE.sm, flexShrink: 0 }} />}
    </button>
  );
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
  // Страховка «Выбрать из существующих»: готовая персона зоны вместо интервью
  const [pickOpen, setPickOpen] = useState(false);
  const [pickingId, setPickingId] = useState<string | null>(null);

  // Шаг выбора готовой персоны — ПЕРЕД интервью; интервью стартует по явной кнопке
  const [interviewing, setInterviewing] = useState(false);
  const [pickChoiceId, setPickChoiceId] = useState<string | null>(null);

  // Кандидаты зоны гейта: личный — глобальные персоны, проектный — персоны проекта.
  // Шаг выбора и кнопка-страховка показываются, только когда парк не пуст
  const allPersonas = usePersonas();
  const [personasReady, setPersonasReady] = useState(false);
  useEffect(() => { void ensurePersonasLoaded().finally(() => setPersonasReady(true)); }, []);
  const candidates = useMemo(
    () => allPersonas.filter(p => kind === 'user'
      ? p.scope === 'global'
      : p.scope === 'project' && p.projectId === project?.id),
    [allPersonas, kind, project?.id],
  );
  // Пока список персон не загружен — не решаем: иначе на первом кадре candidates пуст
  // и интервью успевало бы стартовать мимо шага выбора
  const showPicker = personasReady && candidates.length > 0 && !interviewing;

  const load = useCallback(() => {
    setError(null);
    start()
      .then(setSession)
      .catch(e => setError(e instanceof Error ? e.message : 'Не удалось запустить онбординг'));
  }, [start]);
  // Сессию интервью заводим ровно один раз и только когда шаг выбора пройден или не нужен:
  // лишняя сессия = лишний ход модели. Перезапуск идёт через restart(), не через этот эффект
  const startedRef = useRef(false);
  useEffect(() => {
    if (startedRef.current || !personasReady || showPicker) return;
    startedRef.current = true;
    load();
  }, [personasReady, showPicker, load]);

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

  // Выбор существующей персоны: назначаем дефолтом и сразу выходим из гейта.
  // Права/профиль дефолта персона не получает — это гарантирует бэкенд
  // (OnboardingCreatedPersonaId сессии остаётся пустым), фронт ничего не досевает
  const pickExisting = async (p: Persona) => {
    if (pickingId) return;
    setPickingId(p.id);
    try {
      await api.personas.makeDefault(p.id);
      await refreshMe();
      bumpPersonas();
      onDone();
    } catch (e) {
      showToast('Онбординг', e instanceof Error ? e.message : 'Не удалось назначить персону по умолчанию');
      setPickingId(null);
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
        // На узком экране три кнопки-страховки не влезают в ряд с заголовком —
        // переносим их на вторую строку вместо overflow
        flexWrap: 'wrap', rowGap: SP.sm,
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
          {/* На шаге выбора подзаголовок про интервью неуместен — экран объясняет себя сам */}
          {!isMobile && !showPicker && (
            <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>{subtitle}</div>
          )}
        </div>
        {/* Страховки от «незавершаемого» интервью */}
        {!completed && session && (
          <>
            {candidates.length > 0 && (
              <Button variant="ghost" size="sm"
                leftIcon={<Users size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                title="Не проходить интервью — назначить дефолтом одну из уже существующих персон"
                onClick={() => setPickOpen(true)}>
                {isMobile ? 'Выбрать' : 'Выбрать из существующих'}
              </Button>
            )}
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

      {/* Шаг выбора готовой персоны (когда персоны зоны есть) — иначе чат интервью */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', position: 'relative', overflowY: 'auto' }}>
        {showPicker ? (
          <div style={{
            flex: 1, display: 'flex', alignItems: isMobile ? 'flex-start' : 'center', justifyContent: 'center',
            padding: isMobile ? SP.lg : SP.xl,
          }}>
            <div style={{
              width: '100%', maxWidth: 480,
              display: 'flex', flexDirection: 'column', gap: SP.lg,
            }}>
              <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, color: C.accent }}>
                  <Users size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                  <div style={{ fontFamily: FONT.serif, fontSize: isMobile ? FS.xl : FS.h2, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
                    {PICK_TEXTS[kind].title}
                  </div>
                </div>
                <div style={{ fontSize: FS.md, lineHeight: 1.55, color: C.textSecondary }}>
                  {PICK_TEXTS[kind].subtitle}
                </div>
              </div>

              <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                {candidates.map(p => (
                  <PersonaChoiceRow
                    key={p.id}
                    p={p}
                    selected={pickChoiceId === p.id}
                    busy={pickingId === p.id}
                    disabled={!!pickingId}
                    onClick={() => setPickChoiceId(p.id)}
                  />
                ))}
              </div>

              <div style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: SP.sm }}>
                <Button
                  variant="primary" size="md" glow
                  fullWidth={isMobile}
                  disabled={!pickChoiceId}
                  loading={!!pickingId}
                  leftIcon={<ArrowRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                  onClick={() => {
                    const p = candidates.find(c => c.id === pickChoiceId);
                    if (p) void pickExisting(p);
                  }}
                >
                  {PICK_TEXTS[kind].confirm}
                </Button>
                <Button
                  variant="ghost" size="md"
                  fullWidth={isMobile}
                  disabled={!!pickingId}
                  leftIcon={<Sparkles size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                  onClick={() => setInterviewing(true)}
                >
                  {PICK_TEXTS[kind].create}
                </Button>
              </div>
            </div>
          </div>
        ) : session ? (
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
            <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading }}>Не удалось запустить онбординг</div>
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

      {/* Выбор существующей персоны зоны — детерминированная развилка вместо интервью */}
      {pickOpen && (
        <Modal
          title={kind === 'user' ? 'Кто станет основным собеседником?' : 'Кто будет руководителем проекта?'}
          subtitle={kind === 'user'
            ? 'Новые чаты будут начинаться с ним. Остальные помощники останутся на месте — их можно позвать в любой момент.'
            : 'Он ведёт чаты проекта и знает контекст целиком. Остальная команда остаётся на месте.'}
          onClose={() => { if (!pickingId) setPickOpen(false); }}
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, maxHeight: 320, overflowY: 'auto' }}>
            {candidates.map(p => (
              <PersonaChoiceRow
                key={p.id}
                p={p}
                busy={pickingId === p.id}
                disabled={!!pickingId}
                onClick={() => void pickExisting(p)}
              />
            ))}
          </div>
        </Modal>
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
