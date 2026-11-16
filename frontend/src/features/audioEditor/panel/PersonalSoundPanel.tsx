// Панель «Звук из Личного» личного чата: список звуковых файлов папки «Личного» (дерево личных файлов
// чата) и вход в контекст хода. «Работать с этим» делает файл основным звуком, «В контекст» кладёт его
// референсом личного вида «personal-file» (Ф-9) с ролью «Кусок склейки». Пока основного звука нет,
// кнопки нет: роль «Кусок склейки» принимает только звуковой основной объект, и сервер такую роль
// при другом основном не примет (400 role_not_accepted).

import { useEffect, useState } from 'react';
import { FileAudio, Target } from 'lucide-react';
import {
  Button, ContextAddButton, EmptyState, GenerationPanel, request, showToast, useChatContext,
  C, FS, SP, ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { audioRefOfPath } from '../context/opener';
import { personalFileCandidate, personalFileRoles } from '../context/roles';
import { workWithInContext } from '../context/work';
import { personalFilesPath, personalSoundFiles, type PersonalFileEntry } from '../scope';

export const PERSONAL_SOUND_KEY = 'personal-sound';

export function PersonalSoundPanel({ ctx, layout = 'column' }: { ctx: WorkspacePanelDefCtx; layout?: 'column' | 'sheet' }) {
  const { sessionId } = ctx;
  const [files, setFiles] = useState<PersonalFileEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [reloadKey, setReloadKey] = useState(0);
  const load = () => setReloadKey(k => k + 1);

  useEffect(() => {
    if (!sessionId) return;
    // Поздний ответ прежнего чата не должен перезаписать список нового
    let stale = false;
    setFiles(null);
    setError(null);
    request<PersonalFileEntry[]>(`${personalFilesPath(sessionId)}/tree?path=`, { live: true })
      .then(list => { if (!stale) setFiles(personalSoundFiles(list)); })
      .catch((e: Error) => { if (!stale) setError(e.message || 'Не удалось получить файлы «Личного»'); });
    return () => { stale = true; };
  }, [sessionId, reloadKey]);

  const work = async (path: string) => {
    if (!sessionId) return;
    try {
      const ref = await audioRefOfPath(null, sessionId, path);
      if (ref) await workWithInContext(sessionId, String(ref.ref.threadId), null, !ctx.isMobile);
    } catch (e) {
      showToast('Не удалось взять файл в работу', (e as Error).message, 'error');
    }
  };

  // Кнопка «В контекст» есть только когда основной объект — звук: роль «Кусок склейки» принимает
  // лишь звуковой основной объект, и при другом основном сервер ответил бы 400 role_not_accepted
  const state = useChatContext(sessionId);
  const canAddToContext = !!sessionId && personalFileRoles(state.primary?.kind).length > 0;

  return (
    <GenerationPanel
      title="Звук из Личного"
      icon={<FileAudio size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      footContent={<span style={{ fontSize: FS.sm, color: C.textSecondary }}>Звуковые файлы папки «Личное»</span>}
      onClose={ctx.onClose}
      layout={layout}
    >
      <div data-personal-sound="" style={{ paddingTop: SP.sm, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        {error && (
          <EmptyState compact inline icon={<FileAudio size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />} title="Файлы не получены" subtitle={error}
            action={<Button size="sm" variant="secondary" onClick={load}>Повторить</Button>} />
        )}
        {!error && files === null && <span style={{ fontSize: FS.sm, color: C.textMuted }}>Загрузка…</span>}
        {!error && files?.length === 0 && (
          <EmptyState compact inline icon={<FileAudio size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />} title="Звуковых файлов нет"
            subtitle="Сохраните звук в «Личное» или загрузите файл — wav, mp3, flac, ogg." />
        )}
        {files?.map(f => (
          <div key={f.path} data-personal-sound-file={f.path}
            style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', minWidth: 0 }}>
            <span title={f.path}
              style={{ flex: '1 1 140px', minWidth: 0, fontSize: FS.sm, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {f.path}
            </span>
            <Button size="sm" variant="secondary" leftIcon={<Target size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              title="Звук станет основным в контексте хода" onClick={() => { void work(f.path); }}>
              Работать с этим
            </Button>
            {canAddToContext && sessionId && (
              <ContextAddButton sessionId={sessionId} projectId={null}
                candidate={personalFileCandidate(f.path)} size="sm" />
            )}
          </div>
        ))}
      </div>
    </GenerationPanel>
  );
}
