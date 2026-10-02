// Переключатель «Создать / Править» — зеркало в полосе и в панели (макет image-panel-v5,
// вариант 1). «Создать» при выбранной картинке заводит черновик «Новая картинка»; «Править»
// без картинки приглушён и спрашивает «Что править?»: картинки чата свежими сверху, загрузка
// с компьютера и файлы проекта (только в проекте). Детали — из общего слоя кита.

import { useRef, useState } from 'react';
import type { RefObject } from 'react';
import { FolderOpen, Pencil, Sparkles, Upload } from 'lucide-react';
import {
  GenerationModeSwitch, GenerationPickMenu, gitRelTime, ICON_SIZE, ICON_STROKE, useGenerationSheet,
  type GenerationModeOption, type GenerationPickExtra, type GenerationPickRow,
} from 'aihome_shell/kit';
import { ProjectImagePicker } from '../PanelSections';
import { isPersonalScope } from '../scope';
import { editFileByHuman, editThreadByHuman, editUploadByHuman, setImageModeByHuman } from '../thread/actions';
import { getLastEdited, type ImageMode } from '../thread/modeState';
import { threadHasImage } from '../thread/model';
import { IMAGE_PICK_MARK, IMAGE_PICK_TITLE, imagePickMenu } from '../thread/pickMenu';
import type { ImageThread } from '../thread/threadsApi';
import { activeSrc } from '../thread/useThreadLaunch';

export const MODE_ICON: Record<ImageMode, typeof Sparkles> = { create: Sparkles, edit: Pencil };
export const MODE_LABEL: Record<ImageMode, string> = { create: 'Создать', edit: 'Править' };

export function imageModeOptions(thread: ImageThread | null): GenerationModeOption<ImageMode>[] {
  const muted = !threadHasImage(thread);
  return [
    { value: 'create', label: MODE_LABEL.create, icon: MODE_ICON.create, title: 'Новая картинка по тексту, персонажу и образцам' },
    {
      value: 'edit', label: MODE_LABEL.edit, icon: MODE_ICON.edit,
      ...(muted ? { muted: true, title: 'Выбрать картинку этого чата для правки' } : { title: 'Править выбранную картинку' }),
    },
  ];
}

const ic = (I: typeof Upload) => <I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

export function ImageModeSwitch({ projectId, sessionId, mode, thread, threads, isMobile, compact, quiet, bar }: {
  projectId: string;
  sessionId: string | null;
  mode: ImageMode;
  thread: ImageThread | null;
  threads: readonly ImageThread[];
  isMobile?: boolean;
  compact?: boolean;
  quiet?: boolean;
  // Полоса: меню встаёт над ней целиком, а не над сегментом
  bar?: RefObject<HTMLElement | null>;
}) {
  const personal = isPersonalScope(projectId);
  const [menuAt, setMenuAt] = useState<DOMRect | null>(null);
  const [picker, setPicker] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  // Шторка телефона: выбор в меню её не поднимает — человек пишет промпт в поле ввода
  const how = useGenerationSheet() ? 'none' : 'auto';
  const close = () => setMenuAt(null);
  const open = (seg: DOMRect) => {
    const b = bar?.current?.getBoundingClientRect();
    setMenuAt(b ? new DOMRect(seg.left, b.top, seg.width, b.height) : seg);
  };
  const model = menuAt
    ? imagePickMenu(threads, { ago: gitRelTime, focusId: thread?.id ?? null, lastEditedId: getLastEdited(sessionId), personal })
    : null;
  const rows: GenerationPickRow[] = (model?.rows ?? []).map(r => {
    const t = threads.find(x => x.id === r.id);
    return {
      id: r.id, name: r.name, sub: r.sub || undefined, thumb: (t && activeSrc(projectId, t)) || undefined,
      icon: ic(Sparkles), mark: r.last ? IMAGE_PICK_MARK : undefined,
    };
  });
  const pick = (id: string) => {
    close();
    if (sessionId) void editThreadByHuman(projectId, sessionId, id, how);
  };
  const extras: GenerationPickExtra[] = (model?.extras ?? []).map(k => (k === 'upload'
    ? { key: k, label: 'С компьютера…', icon: ic(Upload), onClick: () => { close(); input.current?.click(); } }
    : { key: k, label: 'Из файлов проекта…', icon: ic(FolderOpen), onClick: () => { close(); setPicker(true); } }));
  return (
    <>
      <span data-images-mode-switch="" style={{ display: 'inline-flex', flexShrink: 0 }}>
        <GenerationModeSwitch<ImageMode>
          value={mode}
          options={imageModeOptions(thread)}
          onChange={m => { if (sessionId) void setImageModeByHuman(projectId, sessionId, m); }}
          onMutedClick={(_, a) => open(a)}
          compact={compact}
          quiet={quiet}
          isMobile={isMobile}
        />
      </span>
      {menuAt && model && (
        <GenerationPickMenu
          title={IMAGE_PICK_TITLE}
          subtitle="Картинки этого чата, свежие сверху"
          rows={rows}
          onPick={pick}
          emptyText="В этом чате пока нет картинок"
          emptyHint={personal ? 'Нарисуйте новую в «Создать»' : 'Нарисуйте новую в «Создать» или возьмите картинку ниже'}
          extras={extras}
          footer={model.footer}
          onClose={close}
          anchor={menuAt}
          fullWidth={isMobile}
          isMobile={isMobile}
        />
      )}
      {!personal && (
        <input ref={input} type="file" accept="image/png,image/jpeg,image/webp" hidden data-images-pick-upload=""
          onChange={e => {
            const f = e.target.files?.[0];
            e.target.value = '';
            if (f && sessionId) void editUploadByHuman(projectId, sessionId, f, how);
          }} />
      )}
      {picker && !personal && (
        <ProjectImagePicker projectId={projectId} taken={[]} pickOnClick title="Что править: картинка из проекта" confirmLabel="Править"
          onClose={() => setPicker(false)}
          onPick={p => { setPicker(false); if (sessionId) void editFileByHuman(projectId, sessionId, p, how); }} />
      )}
    </>
  );
}
