// Чат картинки для MF-модуля редактора (ADR-018 §10.3, вариант В): ядро отдаёт модулю
// готовый компонент через контекст слота `app-overlay`, чтобы в бандле модуля не было
// второй копии ChatPanel, SignalR и сторов.
//
// Пока чата нет — композер-заглушка; первое сообщение создаёт чат ручкой модуля
// (createChat), грузит вложения и снимок холста и монтирует настоящий ChatPanel
// (embedded + hideHeader) с отложенным сообщением.

import { useEffect, useState } from 'react';
import type { Project, Session } from '../../../types';
import type { ImageChatSlotProps } from '../../../lib/subsystems/registryCore';
import { api } from '../../../lib/api';
import { setDraft } from '../../../lib/drafts';
import { showToast } from '../../../lib/toast';
import { useIsMobile } from '../../../lib/breakpoints';
import { ChatPanel, type PendingChatSend } from '../../../components/ChatPanel';
import { SP } from '../../../lib/design';
import { ImageChatComposerStub, type StubSubmit } from './ImageChatComposerStub';
import { ImageChatSuggestions } from './ImageChatSuggestions';
import { SnapshotChip, SnapshotPickerRow } from './SnapshotChip';

export function ImageChatSlot(props: ImageChatSlotProps) {
  const { projectId, sessionId, draftKey, leadIn, snapshot, suggestions } = props;
  const isMobile = useIsMobile();
  const [project, setProject] = useState<Project | null>(null);
  const [loaded, setSession] = useState<Session | null>(null);
  // Чат сменили снаружи («Новый чат») — прежний больше не показываем
  const session = loaded && loaded.id === sessionId ? loaded : null;
  const [pending, setPending] = useState<PendingChatSend | undefined>();
  const [attached, setAttached] = useState<string[]>([]);
  const [creating, setCreating] = useState(false);
  // Сбой создания возвращает текст в черновик — композер перечитывает его перемонтированием
  const [stubKey, setStubKey] = useState(0);

  useEffect(() => {
    let alive = true;
    api.projects.list()
      .then(list => { if (alive) setProject(list.find(p => p.id === projectId) ?? null); })
      .catch(() => {});
    return () => { alive = false; };
  }, [projectId]);

  // Чат сменился снаружи («Новый чат», открытие другой картинки) — подтягиваем его
  useEffect(() => {
    if (!sessionId || loaded?.id === sessionId) return;
    let alive = true;
    api.chats.get(sessionId)
      .then(s => { if (alive) setSession(s); })
      .catch((e: Error) => { if (alive) showToast('Чат картинки', e.message, 'error'); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- грузим только по смене id
  }, [sessionId]);

  const adopt = (s: Session) => {
    setSession(s);
    props.onSessionChange(s);
  };

  // Первое сообщение: чат → вложения с компьютера → снимок холста → ChatPanel с pendingMessage.
  // Упало после создания — пустой чат найдётся при следующем открытии картинки (ADR-018 §1)
  const start = async ({ text, paths, files, personaId }: StubSubmit) => {
    setCreating(true);
    try {
      const s = await props.createChat(personaId);
      const uploaded: string[] = [];
      for (const f of files) {
        try { uploaded.push((await api.chats.uploadFile(s.id, f, projectId)).path); }
        catch { showToast('Вложение', `Не удалось загрузить ${f.name}`); }
      }
      const all = [...paths, ...uploaded];
      const prepared = await props.prepareSend(s.id, text, all)
        .catch(() => ({ text, paths: all, snapshot: null }));
      setPending({ text: prepared.text, attachedPaths: prepared.paths, imageSnapshot: prepared.snapshot });
      adopt(s);
    } catch (e) {
      setDraft(draftKey, text);
      setStubKey(k => k + 1);
      showToast('Чат картинки', `Не удалось начать чат: ${(e as Error).message}`, 'error');
    } finally {
      setCreating(false);
    }
  };

  if (!project) return null;

  if (!session) {
    if (sessionId) return null;
    return (
      <ImageChatComposerStub key={stubKey} project={project} draftKey={draftKey} leadIn={leadIn}
        suggestions={suggestions} snapshot={snapshot} isMobile={isMobile} busy={creating}
        onSubmit={s => { void start(s); }} />
    );
  }

  const sid = session.id;
  // Подсказка из пустой ленты открытого чата — тем же путём, что и отложенное сообщение
  const sendSuggestion = (text: string) => {
    void props.prepareSend(sid, text, [])
      .catch(() => ({ text, paths: [], snapshot: null }))
      .then(p => setPending({ text: p.text, attachedPaths: p.paths, imageSnapshot: p.snapshot }));
  };

  return (
    <div style={{ height: '100%', minHeight: 0, position: 'relative' }}>
      <ChatPanel
        key={sid}
        session={session}
        project={project}
        embedded
        hideHeader
        isMobile={isMobile}
        attachedFiles={attached}
        onAttachedFilesChange={setAttached}
        pendingMessage={pending}
        onPendingMessageSent={() => setPending(undefined)}
        onSessionUpdated={adopt}
        leadIn={<div style={{ paddingBottom: SP.sm }}>{leadIn}</div>}
        prepareSend={(text, paths) => props.prepareSend(sid, text, paths)}
        composerChips={snapshot?.on ? <SnapshotChip chip={snapshot} /> : undefined}
        attachExtra={snapshot && !snapshot.on ? close => <SnapshotPickerRow chip={snapshot} onDone={close} /> : undefined}
        greetingBubble={suggestions?.length && !pending
          ? <ImageChatSuggestions items={suggestions} onPick={sendSuggestion} />
          : <></>}
      />
    </div>
  );
}
