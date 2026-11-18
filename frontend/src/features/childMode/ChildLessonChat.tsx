// KM-F8: чат урока на холсте (макет kids-shell-v2.html, «Урок: шапка, лента, композер»).
// Шапка-hero с бейджами предмета и «Помощник · программа», лента ChatItemView, индикатор
// ожидания, чипы лесенки «Дай подсказку» → 2 → 3 и детский композер. Холст и рамку панелей
// ставит оболочка (KM-F10), здесь — только колонка чтения.
//
// Шапку рисуем сами, а не ChatHeaderBar: тот несёт взрослые действия (переименование,
// удаление, теги, баланс провайдера), ручки которых ребёнку закрыты allow-list'ом (403).
//
// Т3d: шапка по макету Д1 (docs/mockups/kids-tutors.html, «Урок»): аватар репетитора
// и его имя вместо бейджа названия, метка предмета — бейдж с глифом из палитры ключа.
// `subjectClosed` — вместо композера спокойная панель «Этот предмет закрыт» (дефолтный
// вариант разреза; ждёт ответа Андрея на вопрос 2). `subject = null` — закрытый или
// до-предметный чат: шапка без метки предмета. На мобиле шапка урока скрыта целиком
// (.m .chathead): имя и метку несёт родительский хаб (.mobt) — это оболочка, не этот файл.
//
// Запросы урока и строка allow-list ребёнка (ADR-041, «Существующие ручки и методы»):
//  • GET  api/chats/{id}                      — рефреш состояния сессии в useSession → allow-list
//  • GET  api/chats/{id}/history              — история чата (useSession) → allow-list
//  • GET  api/chats/{id}/attachments/raw      — <img> миниатюры вложения в пузыре (№12) → allow-list
//  • POST api/child/chats/{id}/attachments    — загрузка вложения (№8) → /api/child/*
//  • PUT  api/child/chats/{id}/voice          — «Читать ответы вслух» (№5) → /api/child/*
//  • хаб /hubs/session: JoinSession, SendMessage(attachedPaths), LeaveSession → п.7 таблицы И1
//  • PUT api/chats/{id}/read и прочие ручки чата — НЕ вызываются (ребёнку 403).
// Загрузка и озвучка привязаны к sessionId: ответ после смены чата отбрасывается.

import { useCallback, useEffect, useRef, useState } from 'react';
import { speak, stopSpeaking } from '../../lib/tts';
import { turnText } from '../../lib/turnSpeechStream';
import { Bot, Lightbulb, Lock, X } from 'lucide-react';
import { C, CHAT_GUTTER_MOBILE, CHAT_MAX_W, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { useSession } from '../../hooks/useSession';
import { useOnline } from '../../hooks/useOnline';
import { Badge } from '../../components/ui/Badge';
import { Chip } from '../../components/ui/Chip';
import { IconButton } from '../../components/ui/IconButton';
import { Notice } from '../../components/ui/Notice';
import { WaitingIndicator } from '../../components/ui/WaitingIndicator';
import { Composer } from '../../components/Composer';
import { ChatItemView } from '../../components/chat/ChatItemView';
import { isWidgetShow } from '../../components/chat/WidgetView';
import { ChatSessionContext, ChildFeedContext } from '../../components/chat/contexts';
import { childApi, ChildAttachmentRejectedError } from './api';
import { TutorMark } from './ChildHome';
import { subjectColors } from './subjectPalette';
import type { ChatItem } from '../../types';
import type { ChildStatus, ChildSubjectTutor, ChildSubjectView } from './types';

// Лесенка подсказок: чип отправляет свою фразу как сообщение (макет, ASK).
const LADDER = ['Дай подсказку', 'Покажи похожий пример', 'Разбери со мной один шаг'] as const;

const MB = 1024 * 1024;

// Сбой хода: сырой текст провайдера («Invalid API key», «проверьте API-ключ») ребёнку не показываем —
// он остаётся в логе сервера. Вместо него — один спокойный текст на «ты».
export const CHILD_TURN_ERROR_TEXT = 'Помощник сейчас не может ответить. Попробуй чуть позже или позови взрослого.';

// Детская лента — белый список видов: реплики ребёнка и тьютора и успешный итог хода идут
// в ChatItemView как есть; сбой хода целиком (result с ошибкой API, error, rate_limit…) —
// плашка CHILD_TURN_ERROR_TEXT. Ошибка отдельного инструмента (tool_use с isError) плашки
// НЕ даёт: технический сбой одного инструмента ребёнку не показываем (К8b, §8 п.5).
// Вызов виджета (widget_show) без ошибки идёт в ленту на своём месте — ChatItemView сам
// отдаст его в WidgetView (К8b, «Детский режим»: репетитору доступны виджеты). Всё
// остальное, включая будущие виды, не показывается.
function childFeed(items: ChatItem[]): { item: ChatItem; i: number; plate: boolean }[] {
  const out: { item: ChatItem; i: number; plate: boolean }[] = [];
  items.forEach((item, i) => {
    switch (item.kind) {
      case 'user_message':
      case 'text':
        out.push({ item, i, plate: false });
        return;
      case 'result':
        out.push({ item, i, plate: item.subtype !== 'success' });
        return;
      case 'error':
      case 'error_group':
      case 'rate_limit':
      case 'provider_limit':
      case 'model_switched':
      case 'work_loop_stopped':
      case 'hands_notice':
        out.push({ item, i, plate: true });
        return;
      case 'tool_use':
        // Виджет репетитора (схема/наглядный рисунок) — в ленту на месте своего вызова;
        // сбой любого инструмента — мимо ленты и без плашки (К8b, §8 п.5).
        if (isWidgetShow(item.name) && !item.isError) out.push({ item, i, plate: false });
        return;
      default:
    }
  });
  return out;
}

interface Props {
  sessionId: string;
  // Предмет чата с сервера (Т3c): view — живой предмет; null — закрытый или
  // до-предметный чат (шапка без метки предмета)
  subject: ChildSubjectView | null;
  // Предмет выключен или удалён (И1-бис): чат только для чтения
  subjectClosed?: boolean;
  title: string;
  status: ChildStatus;
  // Сколько ступеней лесенки уже пройдено (0..3) — панель «Как мы учимся» (KM-F7) рисует по нему
  onHintStep?: (step: number) => void;
  // Озвучка ответов включена на сервере к моменту открытия чата
  initialVoiceMode?: boolean;
}

// Предмет шапки: цвет — из палитры ключа (Т3b). null — закрытый/до-предметный чат:
// метки предмета нет.
interface HeaderSubject { label: string; glyph: string; bg: string; fg: string; tutor: ChildSubjectTutor | null }
function headerSubject(subject: ChildSubjectView | null): HeaderSubject | null {
  if (!subject) return null;
  return { label: subject.label, glyph: subject.glyph || '?', ...subjectColors(subject.color), tutor: subject.tutor };
}

interface Attached { path: string; name: string }

const noop = () => {};

function rejectText(e: unknown, maxBytes: number): string {
  if (e instanceof ChildAttachmentRejectedError) {
    switch (e.code) {
      case 'unsupported_type': return 'Такой файл я не открою. Подойдёт фото, скриншот или PDF.';
      case 'too_large':
        return `Файл слишком большой — можно до ${Math.max(1, Math.round(maxBytes / MB))} МБ. Сфотографируй одну страницу, так будет лучше видно.`;
      case 'pdf_no_text': return 'В этом PDF я не вижу текста. Сфотографируй страницу — так я её прочитаю.';
      case 'empty_file': return 'Этот файл пустой. Выбери другой.';
    }
  }
  return 'Не получилось загрузить файл. Попробуй ещё раз.';
}

export function ChildLessonChat({ sessionId, subject, subjectClosed = false, title, status, onHintStep, initialVoiceMode = false }: Props) {
  const isMobile = useIsMobile();
  const online = useOnline();
  const view = headerSubject(subject);
  const { items, isWaiting, send, interrupt } = useSession(sessionId);

  // Состояния привязаны к чату: ключ — sessionId, чужой чат их не видит
  const [att, setAtt] = useState<{ sid: string; list: Attached[] }>({ sid: sessionId, list: [] });
  const [err, setErr] = useState<{ sid: string; text: string } | null>(null);
  const [ladder, setLadder] = useState<{ sid: string; step: number }>({ sid: sessionId, step: 0 });
  const [voice, setVoice] = useState<{ sid: string; on: boolean }>({ sid: sessionId, on: initialVoiceMode });
  const attached = att.sid === sessionId ? att.list : [];
  const error = err && err.sid === sessionId ? err.text : null;
  const step = ladder.sid === sessionId ? ladder.step : 0;
  const voiceOn = voice.sid === sessionId ? voice.on : initialVoiceMode;

  // Актуальный чат — для отбрасывания ответов, ушедших с прежнего
  const curSid = useRef(sessionId);
  useEffect(() => { curSid.current = sessionId; }, [sessionId]);

  // Озвучка ответов (голосового ввода нет): ход закончился — читаем его текст целиком.
  // Смена урока и размонтирование обрывают звук; выключение переключателя — тоже.
  const wasWaiting = useRef(false);
  useEffect(() => {
    const finished = wasWaiting.current && !isWaiting;
    wasWaiting.current = isWaiting;
    if (!finished || !voiceOn) return;
    const text = turnText(items);
    if (text) void speak(text, undefined, sessionId);
  }, [isWaiting, voiceOn, items, sessionId]);
  useEffect(() => { if (!voiceOn) stopSpeaking(); }, [voiceOn]);
  useEffect(() => () => stopSpeaking(), [sessionId]);

  const fileRef = useRef<HTMLInputElement>(null);
  const endRef = useRef<HTMLDivElement>(null);
  useEffect(() => { endRef.current?.scrollIntoView?.({ block: 'end' }); }, [items.length, isWaiting]);

  const uploadFiles = useCallback((files: File[]) => {
    const sid = sessionId;
    for (const file of files) {
      void childApi.uploadAttachment(sid, file).then(
        r => {
          if (curSid.current !== sid) return; // ответ после смены чата — в новый чат не идёт
          setAtt(prev => {
            const list = prev.sid === sid ? prev.list : [];
            return list.some(a => a.path === r.path) ? { sid, list } : { sid, list: [...list, { path: r.path, name: r.name }] };
          });
          setErr(prev => (prev && prev.sid === sid ? null : prev));
        },
        e => {
          if (curSid.current !== sid) return;
          setErr({ sid, text: rejectText(e, status.maxAttachmentBytes) });
        },
      );
    }
  }, [sessionId, status.maxAttachmentBytes]);

  const toggleVoice = useCallback((next: boolean) => {
    const sid = sessionId;
    return childApi.voice(sid, next).then(
      r => { if (curSid.current === sid) setVoice({ sid, on: r.voiceMode }); },
      () => { if (curSid.current === sid) setErr({ sid, text: 'Не получилось переключить озвучку. Попробуй ещё раз.' }); },
    );
  }, [sessionId]);

  const sendMessage = useCallback((text: string, paths: string[]) => {
    void send(text, paths);
    setAtt({ sid: sessionId, list: [] });
  }, [send, sessionId]);

  const askHint = (i: number) => {
    void send(LADDER[i], []);
    const next = i + 1;
    setLadder({ sid: sessionId, step: next });
    onHintStep?.(next);
  };

  const feed = childFeed(items);
  const labels = Object.fromEntries(attached.map(a => [a.path, a.name]));
  const gutter = isMobile ? CHAT_GUTTER_MOBILE : SP.xl;
  const col = { width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto', boxSizing: 'border-box', padding: `0 ${gutter}px` } as const;

  return (
    <ChatSessionContext.Provider value={sessionId}>
      <section data-child-lesson="" style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
        {/* Макет Д1: на мобиле шапка урока скрыта (.m .chathead) — имя репетитора и метку
            предмета несёт родительский хаб (.mobt), это оболочка. */}
        {!isMobile && (
        <header style={{ ...col, paddingTop: SP.lg, paddingBottom: SP.md, display: 'flex', alignItems: 'center', gap: SP.md }}>
          {view?.tutor && <TutorMark tutor={view.tutor} size={52} />}
          <div style={{ minWidth: 0 }}>
            <h1 style={{
              margin: 0, fontFamily: FONT.serif, fontSize: FS.h1, fontWeight: 500, lineHeight: 1.25,
              color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{view?.tutor?.name ?? title}</h1>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.xs }}>
              {view && (
                <Badge tone="neutral">
                  <span aria-hidden style={{
                    width: 22, height: 22, borderRadius: R.sm, flexShrink: 0, background: view.bg, color: view.fg,
                    display: 'grid', placeItems: 'center', fontFamily: FONT.serif, fontSize: FS.xs, fontWeight: 700,
                  }}>{view.glyph}</span>
                  {view.label}
                </Badge>
              )}
              {subjectClosed && <Badge tone="neutral" icon={<Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>закрыт</Badge>}
              <Badge tone="info" icon={<Bot size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>Помощник · программа</Badge>
            </div>
          </div>
        </header>
        )}

        {/* К8b (§8 п.5): детская лента провайдит ChildFeedContext=true — markdown реплик
            не несёт ссылок наружу и внешних картинок (MarkdownContent читает его сам). */}
        <ChildFeedContext.Provider value={true}>
        <div data-child-feed="" style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
          <div style={{ ...col, display: 'flex', flexDirection: 'column', gap: SP.md, fontSize: FS.lg, paddingBottom: SP.lg }}>
            {feed.map(({ item, i, plate }, k) => plate ? (
              // Подряд идущие сбои сворачиваем в одну плашку
              feed[k - 1]?.plate ? null : (
                <div key={i} data-child-turn-error="">
                  <Notice tone="warning" icon={Lightbulb} role="alert">{CHILD_TURN_ERROR_TEXT}</Notice>
                </div>
              )
            ) : (
              <ChatItemView
                key={i}
                item={item}
                index={i}
                online={online}
                streaming={isWaiting && i === items.length - 1}
                onToggleThinking={noop}
                onAllowPermission={noop}
                onDenyPermission={noop}
                onAllowAlways={noop}
                onAnswerQuestion={noop}
                onRespondPlan={noop}
                onSwitchMode={noop}
                onRetry={noop}
                onInterrupt={() => { void interrupt(); }}
              />
            ))}
            {isWaiting && <WaitingIndicator />}
            <div ref={endRef} />
          </div>
        </div>
        </ChildFeedContext.Provider>

        <div style={{ ...col, paddingBottom: SP.md, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {error && (
            <Notice
              tone="warning"
              icon={Lightbulb}
              role="alert"
              action={
                <IconButton size="xs" ariaLabel="Скрыть" onClick={() => setErr(null)}>
                  <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </IconButton>
              }
            >
              {error}
            </Notice>
          )}
          {subjectClosed ? (
            /* Закрытый предмет (И1-бис): композера и лесенки нет — чат только для чтения.
               Спокойная панель по макету Д1 (.closedbar); кнопка «К предметам» в макете —
               это навигация оболочки (рейл/сайдбар/PillSwitch), не колонка чтения. */
            <div data-child-closed="" role="status" style={{
              display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `${SP.md}px ${SP.lg}px`,
              borderRadius: R.xxl, border: `1px solid ${C.border}`, background: C.bgPanel,
            }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>
                <span aria-hidden style={{ width: 32, height: 32, flexShrink: 0, borderRadius: R.md, background: C.bgInset, color: C.textSecondary, display: 'grid', placeItems: 'center' }}>
                  <Lock size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
                </span>
                Этот предмет закрыт
              </div>
              <div style={{ fontSize: FS.md, color: C.textSecondary, lineHeight: 1.5 }}>
                Переписку можно перечитать, а новые сообщения сюда не уйдут. Хочешь продолжить — спроси папу.
              </div>
            </div>
          ) : (
            <>
              {step < LADDER.length && (
                <div role="group" aria-label="Подсказки" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
                  <Chip variant="toggle" large={isMobile} onClick={() => askHint(step)}>
                    {step > 0 && <span style={{ fontWeight: 700, marginRight: SP.xs }}>{step + 1}</span>}
                    {LADDER[step]}
                  </Chip>
                  {step < LADDER.length - 1 && (
                    <Chip variant="toggle" large={isMobile} disabled>
                      <span style={{ fontWeight: 700, marginRight: SP.xs }}>{step + 2}</span>
                      {LADDER[step + 1]}
                    </Chip>
                  )}
                </div>
              )}
              <input
                ref={fileRef}
                type="file"
                data-child-file=""
                hidden
                multiple
                accept={status.attachmentTypes.join(',')}
                onChange={e => {
                  const files = Array.from(e.target.files ?? []);
                  e.target.value = '';
                  if (files.length) uploadFiles(files);
                }}
              />
              <Composer
                variant="child"
                sessionId={sessionId}
                isMobile={isMobile}
                offline={!online}
                mode="default"
                onModeChange={noop}
                isGenerating={isWaiting}
                onSend={(text, paths) => sendMessage(text, paths)}
                onStop={() => { void interrupt(); }}
                onAttach={() => fileRef.current?.click()}
                onAttachFiles={uploadFiles}
                attachments={attached.map(a => a.path)}
                attachmentLabels={labels}
                onRemoveAttachment={path => setAtt({ sid: sessionId, list: attached.filter(a => a.path !== path) })}
                voiceMode={voiceOn}
                onToggleVoiceMode={toggleVoice}
              />
            </>
          )}
          <p data-child-disclosure="" style={{ margin: 0, textAlign: 'center', fontSize: FS.xs, color: C.textMuted }}>
            Помощник — компьютерная программа
          </p>
        </div>
      </section>
    </ChatSessionContext.Provider>
  );
}
