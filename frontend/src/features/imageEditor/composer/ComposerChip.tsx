// Чип пометок над полем ввода (слот composer-chip): «hero.png · 1 пометка ✕». Пометки
// из редактора уходят со следующим сообщением в любом режиме, ✕ — не прикладывать.
// Здесь же живёт попап «Редактор»: композер смонтирован всё время, пока открыт чат, а
// карточка в ленте может уехать из виду. По той же причине чип — точка входа в чат для
// полосы «Картинки»: чип монтируется один раз на чат, а карточек в ленте много.

import { useEffect } from 'react';
import { Brush } from 'lucide-react';
import { Chip, FLAGS, ICON_SIZE, ICON_STROKE, SP, useFeature, useIsMobile } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { EditorModal } from '../editor/EditorModal';
import { enterScope, isPersonalScope } from '../scope';
import { plural } from '../format';
import { threadName } from '../thread/model';
import { enterChat, getEditor, getThreadMarks, openEditor, setThreadMarks, useThreads } from '../thread/threadStore';
import { usePrefs } from '../thread/prefs';
import { getStoredImageMode, effectiveImageMode, useImageModeVersion } from '../thread/modeState';
import { threadHasImage } from '../thread/useThreadLaunch';
import { IMAGE_COMPOSER_MODE } from './imageMode';
import { editChoice, editPickOf } from '../panel/panelOp';
import { IMAGE_COMPOSER_MODE } from './imageMode';

// Кисть живёт только в режиме поля ввода «Картинка»: в «Чат», «Звук» и прочих она лишняя
export const brushModeActive = (modeId: string | null | undefined) => modeId === IMAGE_COMPOSER_MODE;

export function ImageComposerChip({ ctx }: { ctx: ComposerChipCtx }) {
  const { sessionId } = ctx;
  // Личный чат вне проекта: ctx.projectId = null, область — personal
  const projectId = enterScope(ctx.projectId, sessionId);
  const state = useThreads(projectId, sessionId);
  useEffect(() => { if (sessionId) enterChat(sessionId); }, [sessionId]);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const editor = getEditor();
  const n = thread ? getThreadMarks(thread.id).marks.length : 0;
  // Панель v5: у «Изменить» над картинкой — кисть, редактор открывается сразу с ней.
  // Отметок на телефоне нет, а с отметками чип ниже и так ведёт в редактор
  const v5 = useFeature(FLAGS.imagePanelV5);
  useImageModeVersion();
  const mobile = useIsMobile();
  const prefs = usePrefs(projectId);
  const brush = v5 && brushModeActive(ctx.modeId) && !mobile && !!thread && n === 0 && threadHasImage(thread)
    && effectiveImageMode(getStoredImageMode(sessionId), true) === 'edit'
    && editPickOf(editChoice(prefs).op) === 'edit';
  return (
    <>
      {brush && sessionId && (
        <span data-image-brush="" style={{ display: 'inline-flex', paddingTop: SP.sm }}>
          <Chip dashed leading={<Brush size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            title="Отметить место кистью — откроется редактор картинки"
            onClick={() => openEditor(sessionId, thread.id, null, { tool: 'mask' })}>
            Отметить
          </Chip>
        </span>
      )}
      {thread && n > 0 && (
        <span style={{ display: 'inline-flex', paddingTop: SP.sm }}>
        <Chip leading={<Brush size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} maxW={260}
          title={isPersonalScope(projectId)
            ? 'Пометки уйдут со следующим запуском в режиме «Картинка». Нажмите, чтобы открыть редактор'
            : 'Пометки уйдут со следующим сообщением. Нажмите, чтобы открыть редактор'}
          onClick={() => { if (sessionId) openEditor(sessionId, thread.id); }}
          onRemove={() => setThreadMarks(thread.id, [], null)}>
          {threadName(thread)} · {n} {plural(n, 'пометка', 'пометки', 'пометок')}
        </Chip>
        </span>
      )}
      {sessionId && editor?.sessionId === sessionId && (
        <EditorModal projectId={projectId} sessionId={sessionId} threadId={editor.threadId} versionId={editor.versionId} startTool={editor.tool} />
      )}
    </>
  );
}
