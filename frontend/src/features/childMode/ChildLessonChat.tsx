// KM-F8: чат урока на холсте (макет kids-shell-v2.html, «Урок: шапка, лента, композер»).
// Шапка-hero с бейджами предмета и «Помощник · программа», лента ChatItemView, индикатор
// ожидания, чипы лесенки «Дай подсказку» → 2 → 3 и детский композер. Холст и рамку панелей
// ставит оболочка (KM-F10), здесь — только колонка чтения.
//
// Шапку рисуем сами, а не ChatHeaderBar: тот несёт взрослые действия (переименование,
// удаление, теги, баланс провайдера), ручки которых ребёнку закрыты allow-list'ом (403).
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
import { Bot, Lightbulb, X } from 'lucide-react';
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
import { ChatSessionContext } from '../../components/chat/contexts';
import { childApi, ChildAttachmentRejectedError } from './api';
import { CHILD_SUBJECTS, type ChildSubjectId } from './ChildHome';
import type { ChildStatus } from './types';

// Лесенка подсказок: чип отправляет свою фразу как сообщение (макет, ASK).
const LADDER = ['Дай подсказку', 'Покажи похожий пример', 'Разбери со мной один шаг'] as const;

const MB = 1024 * 1024;

// Сбой хода: сырой текст провайдера («Invalid API key», «проверьте API-ключ») ребёнку не показываем —
// он остаётся в логе сервера. Вместо него — один спокойный текст на «ты».
export const CHILD_TURN_ERROR_TEXT = 'Помощник сейчас не может ответить. Попробуй чуть позже или позови взрослого.';

interface Props {
  sessionId: string;
  subjectId: ChildSubjectId;
  title: string;
  status: ChildStatus;
  // Сколько ступеней лесенки уже пройдено (0..3) — панель «Как мы учимся» (KM-F7) рисует по нему
  onHintStep?: (step: number) => void;
  // Озвучка ответов включена на сервере к моменту открытия чата
  initialVoiceMode?: boolean;
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

export function ChildLessonChat({ sessionId, subjectId, title, status, onHintStep, initialVoiceMode = false }: Props) {
  const isMobile = useIsMobile();
  const online = useOnline();
  const subject = CHILD_SUBJECTS.find(s => s.id === subjectId);
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

  const labels = Object.fromEntries(attached.map(a => [a.path, a.name]));
  const gutter = isMobile ? CHAT_GUTTER_MOBILE : SP.xl;
  const col = { width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto', boxSizing: 'border-box', padding: `0 ${gutter}px` } as const;

  return (
    <ChatSessionContext.Provider value={sessionId}>
      <section data-child-lesson="" style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
        <header style={{ ...col, paddingTop: SP.lg, paddingBottom: SP.md, display: 'flex', alignItems: 'center', gap: SP.md }}>
          {subject && !isMobile && (
            <span aria-hidden style={{
              width: 44, height: 44, borderRadius: R.xl, flexShrink: 0, background: subject.bg, color: subject.fg,
              display: 'flex', alignItems: 'center', justifyContent: 'center', fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 600,
            }}>{subject.glyph}</span>
          )}
          <div style={{ minWidth: 0 }}>
            <h1 style={{
              margin: 0, fontFamily: FONT.serif, fontSize: isMobile ? FS.h1 : FS.display, fontWeight: 500, lineHeight: 1.25,
              color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{title}</h1>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.xs }}>
              {subject && <Badge tone="neutral">{subject.label}</Badge>}
              <Badge tone="info" icon={<Bot size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>Помощник · программа</Badge>
            </div>
          </div>
        </header>

        <div data-child-feed="" style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
          <div style={{ ...col, display: 'flex', flexDirection: 'column', gap: SP.md, fontSize: FS.lg, paddingBottom: SP.lg }}>
            {items.map((item, i) => (item.kind === 'error' || item.kind === 'error_group') ? (
              // Подряд идущие сбои сворачиваем в одну плашку
              items[i - 1]?.kind === 'error' || items[i - 1]?.kind === 'error_group' ? null : (
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
          <p data-child-disclosure="" style={{ margin: 0, textAlign: 'center', fontSize: FS.xs, color: C.textMuted }}>
            Помощник — компьютерная программа
          </p>
        </div>
      </section>
    </ChatSessionContext.Provider>
  );
}
