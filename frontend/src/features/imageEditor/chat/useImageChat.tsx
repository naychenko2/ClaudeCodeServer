// Чат картинки в редакторе (ADR-018 §1, §3, §6): поиск чата файла при открытии, «Новый
// чат», «Открыть в полном чате», привязка к файлу, снимок холста при отправке. Сам чат
// рисует ядро (ctx.ImageChat) — здесь только его пропсы.

import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import { ExternalLink, Image as ImageIcon, Link2, MessageSquarePlus } from 'lucide-react';
import {
  Button, C, FS, SP, ICON_SIZE, ICON_STROKE, api as appApi, onMessage, personaLabel, showToast, usePersonas,
} from 'aihome_shell/kit';
import type { ImageChatSlotProps, ImageChatPrepared } from '../../../lib/subsystems/registryCore';
import type { Session } from '../../../types';
import type { ImageEditorApi } from '../api';
import type { Mark } from '../marks';
import { plural, splitPath } from '../format';
import { canvasRevision, canvasSnapshot } from './snapshot';

// Подсказки пустой ленты — из макета image-editor-v2
const SUGGESTIONS = ['Убери вон ту лампу и сделай вечер', 'Придумай промпт для обложки блога'];

// Открыть чат проекта тем же каналом, что диплинки: воркспейс подхватит ключ
export function openProjectChat(projectId: string, sessionId: string) {
  sessionStorage.setItem('cc_pending_project_chat', `${projectId}|${sessionId}`);
  window.dispatchEvent(new Event('cc-pending-project-chat'));
}

export interface ImageChatCanvas {
  imgRef: RefObject<HTMLImageElement | null>;
  marks: Mark[];
  size: { w: number; h: number } | null;
  stepId: string | null;
}

export function useImageChat({ api, projectId, sourcePath, openSessionId, canvas, chatVisible, onClose, onOpenPath }: {
  api: ImageEditorApi;
  projectId: string;
  // null — картинки в проекте нет (с компьютера, «Нарисовать»): чата нет
  sourcePath: string | null;
  // Чат, с которым редактор открыли (карточка чата в списке)
  openSessionId?: string | null;
  canvas: ImageChatCanvas;
  // Чат на экране: на телефоне — открытая шторка
  chatVisible: boolean;
  onClose: () => void;
  onOpenPath: (path: string) => void;
}) {
  const [session, setSession] = useState<Session | null>(null);
  const [continued, setContinued] = useState<Session[]>([]);
  // Чат открыт по ссылке, а его файл — другой: файл удалён или перемещён мимо редактора
  const [strayPath, setStrayPath] = useState<string | null>(null);
  const [lastSent, setLastSent] = useState<string | null>(null);
  const [snapOn, setSnapOn] = useState(true);
  const [unread, setUnread] = useState(false);
  const personas = usePersonas();

  // Поиск — один раз на открытие: дальше чат идёт за редактором сам (сохранение переносит его)
  const openPath = useRef(sourcePath);
  useEffect(() => {
    const path = openPath.current;
    if (!path) return;
    let alive = true;
    const byLink = openSessionId
      ? appApi.chats.get(openSessionId).then(s => (s.imageChat ? s : null)).catch(() => null)
      : Promise.resolve(null);
    void Promise.all([byLink, api.findChats(projectId, path).catch(() => null)]).then(([linked, found]) => {
      if (!alive) return;
      const chat = linked ?? found?.current ?? null;
      setContinued(found && !found.current ? found.continued : []);
      if (!chat) return;
      setSession(chat);
      if (chat.imageChat && chat.imageChat.currentPath !== path) setStrayPath(chat.imageChat.currentPath);
      showToast(`Открыт чат этой картинки: «${chat.name}»`, '', 'info');
    });
    return () => { alive = false; };
  }, [api, projectId, openSessionId]);

  const sessionId = session?.id ?? null;

  // Последний отправленный снимок живёт и в состоянии на сервере — переживает перезагрузку
  useEffect(() => {
    if (!sessionId) return;
    let alive = true;
    api.getChatState(projectId, sessionId)
      .then(s => { if (alive && s.lastSentRevision) setLastSent(prev => prev ?? s.lastSentRevision ?? null); })
      .catch(() => {});
    return () => { alive = false; };
  }, [api, projectId, sessionId]);

  // Точка «новое» у кнопки шторки: ход закончился, пока чат не на экране
  const visibleRef = useRef(chatVisible);
  useEffect(() => { visibleRef.current = chatVisible; });
  useEffect(() => {
    if (!sessionId) return;
    return onMessage(m => {
      const msg = m as { sessionId?: string; type?: string };
      if (msg.sessionId === sessionId && msg.type === 'result' && !visibleRef.current) setUnread(true);
    });
  }, [sessionId]);

  const fileName = sourcePath ? splitPath(sourcePath).name : '';
  const { marks, size, stepId } = canvas;
  const revision = sourcePath && size && stepId ? canvasRevision(sourcePath, stepId, marks, size) : null;
  const changed = !!revision && revision !== lastSent;

  const snapshot = useMemo(() => (revision ? {
    label: changed
      ? `${fileName}${marks.length ? ` · ${marks.length} ${plural(marks.length, 'пометка', 'пометки', 'пометок')}` : ''}`
      : `${fileName} · без изменений`,
    changed, on: snapOn, onToggle: setSnapOn,
  } : null), [revision, changed, fileName, marks.length, snapOn]);

  // Снимок уходит, только если холст изменился с прошлого сообщения. Не менялся — пометка
  // attached=false, по ней лента пишет «холст не менялся — снимок не приложен»
  const live = useRef({ revision, lastSent, snapOn, canvas, fileName });
  useEffect(() => { live.current = { revision, lastSent, snapOn, canvas, fileName }; });
  const prepareSend = useCallback(async (sid: string, text: string, paths: string[]): Promise<ImageChatPrepared> => {
    const { revision: rev, lastSent: sent, snapOn: on, canvas: cv, fileName: name } = live.current;
    const img = cv.imgRef.current;
    if (!on || !rev || !cv.size) return { text, paths, snapshot: null };
    if (rev === sent) return { text, paths, snapshot: { revision: rev, attached: false } };
    if (!img) return { text, paths, snapshot: null };
    const blob = await canvasSnapshot(img, cv.marks, cv.size.w, cv.size.h);
    if (!blob) return { text, paths, snapshot: null };
    const stem = name.replace(/\.[^.]+$/, '') || 'картинка';
    const { path } = await appApi.chats.uploadFile(sid, new File([blob], `${stem}-снимок.png`, { type: 'image/png' }), projectId);
    setLastSent(rev);
    return { text, paths: [...paths, path], snapshot: { revision: rev, attached: true } };
  }, [projectId]);

  const createChat = useCallback(async (personaId?: string) => {
    if (!sourcePath) throw new Error('Картинки нет в проекте');
    return api.createChat(projectId, { sourcePath, personaId: personaId ?? null });
  }, [api, projectId, sourcePath]);

  const newChat = () => {
    if (!session) return;
    showToast(`Новый чат по ${fileName}. Прежний «${session.name}» остался в списке чатов проекта`, '', 'info');
    setSession(null);
    setLastSent(null);
    setSnapOn(true);
  };

  const bindHere = async () => {
    if (!session || !sourcePath) return;
    try {
      setSession(await api.setChatPath(projectId, session.id, { path: sourcePath }));
      setStrayPath(null);
    } catch (e) {
      showToast(`Не удалось привязать чат: ${(e as Error).message}`, '', 'error');
    }
  };

  const persona = session?.personaId ? personas.find(p => p.id === session.personaId) : null;
  const who = persona ? personaLabel(persona) : 'Claude';
  const ic = (I: typeof ImageIcon) => <I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

  const leadIn = sourcePath ? (
    <div data-image-chat-lead="" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>
      <div style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start' }}>
        <span style={{ display: 'inline-flex', paddingTop: 2 }}>{ic(ImageIcon)}</span>
        <span style={{ minWidth: 0, overflowWrap: 'anywhere' }}>
          Чат привязан к <b style={{ color: C.textPrimary }}>{sourcePath}</b>. {who} видит картинку, пометки, образцы и промпт — и может сам запустить генерацию.
        </span>
      </div>
      {strayPath && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, alignItems: 'flex-start' }}>
          <span style={{ color: C.textSecondary }}>Файл удалён или перемещён: чат был привязан к {strayPath}</span>
          <Button size="sm" variant="secondary" leftIcon={ic(Link2)} onClick={() => { void bindHere(); }}>Привязать чат к этому файлу</Button>
        </div>
      )}
      {!session && continued[0]?.imageChat && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, alignItems: 'flex-start' }}>
          <span style={{ color: C.textSecondary }}>Разговор об этой картинке продолжился на {continued[0].imageChat.currentPath}</span>
          <Button size="sm" variant="ghost" onClick={() => onOpenPath(continued[0].imageChat!.currentPath)}>Открыть</Button>
        </div>
      )}
      {session && (
        <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
          <Button size="sm" variant="ghost" leftIcon={ic(MessageSquarePlus)} title="Новый чат по этой картинке" onClick={newChat}>Новый чат</Button>
          <Button size="sm" variant="ghost" leftIcon={ic(ExternalLink)} title="Открыть в полном чате"
            onClick={() => { openProjectChat(projectId, session.id); onClose(); }}>
            Открыть в полном чате
          </Button>
        </div>
      )}
    </div>
  ) : null;

  const props: ImageChatSlotProps | null = sourcePath ? {
    projectId, sourcePath, sessionId, draftKey: `image:${projectId}:${sourcePath}`, leadIn,
    prepareSend, createChat, onSessionChange: setSession, snapshot, suggestions: SUGGESTIONS,
  } : null;

  return {
    sessionId, props, unread: unread && !chatVisible, markRead: () => setUnread(false),
    // Для состояния на сервере и меток «✦ … Claude»: ревизия холста и собеседник чата
    revision, who: persona?.name ?? 'Claude', hasPersona: !!persona,
  };
}
