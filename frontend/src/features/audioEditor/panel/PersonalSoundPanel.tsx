// Панель «Звук из Личного» личного чата: список звуковых файлов папки «Личного» (дерево личных файлов
// чата) и вход в контекст хода. «Работать с этим» делает файл основным звуком, «В контекст» кладёт его
// референсом с ролью «Кусок склейки» (роль берёт вид основного объекта — rolesFor).

import { useCallback, useEffect, useState } from 'react';
import { FileAudio, Target } from 'lucide-react';
import {
  Button, ContextAddButton, EmptyState, GenerationPanel, request, showToast, C, FS, SP, ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { audioRefOfPath } from '../context/opener';
import { workWithInContext } from '../context/work';
import { PERSONAL_SCOPE, personalFilesPath, personalSoundFiles, type PersonalFileEntry } from '../scope';

export const PERSONAL_SOUND_KEY = 'personal-sound';

const PROJECT_FILE_KIND = 'project-file';

export function PersonalSoundPanel({ ctx, layout = 'column' }: { ctx: WorkspacePanelDefCtx; layout?: 'column' | 'sheet' }) {
  const { sessionId } = ctx;
  const [files, setFiles] = useState<PersonalFileEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    if (!sessionId) return;
    setError(null);
    request<PersonalFileEntry[]>(`${personalFilesPath(sessionId)}/tree?path=`, { live: true })
      .then(list => setFiles(personalSoundFiles(list)))
      .catch((e: Error) => setError(e.message || 'Не удалось получить файлы «Личного»'));
  }, [sessionId]);

  useEffect(() => { setFiles(null); load(); }, [load]);

  const work = async (path: string) => {
    if (!sessionId) return;
    try {
      const ref = await audioRefOfPath(PERSONAL_SCOPE, sessionId, path);
      if (ref) await workWithInContext(sessionId, String(ref.ref.threadId), null, !ctx.isMobile);
    } catch (e) {
      showToast('Не удалось взять файл в работу', (e as Error).message, 'error');
    }
  };

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
            {sessionId && <ContextAddButton sessionId={sessionId} projectId={null} candidate={{ kind: PROJECT_FILE_KIND, ref: { path: f.path } }} size="sm" />}
          </div>
        ))}
      </div>
    </GenerationPanel>
  );
}
