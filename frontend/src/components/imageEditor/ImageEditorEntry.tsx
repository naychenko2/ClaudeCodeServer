// Вход в редактор (экран 1 макета): «Редактировать» у картинки и «Нарисовать картинку»
// у папки. Без флага image-editor входа нет вовсе — кнопка не рендерится.
// Редактор открывается слоем поверх раскладки проекта.

import { useState } from 'react';
import { Pencil, Sparkles } from 'lucide-react';
import { Button } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ISLAND, Z } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { ImageEditor, type ImageEditorTarget } from './ImageEditor';
import { isEditableImage } from './format';

interface EntryProps {
  projectId: string;
  projectName: string;
  target: ImageEditorTarget;
  onShowInFiles?: (path: string) => void;
  size?: 'xs' | 'sm';
}

export function ImageEditorEntryButton({ projectId, projectName, target, onShowInFiles, size = 'sm' }: EntryProps) {
  const enabled = useFeature(FLAGS.imageEditor);
  const [open, setOpen] = useState(false);
  if (!enabled) return null;
  if (target.kind === 'edit' && !isEditableImage(target.path)) return null;
  const Icon = target.kind === 'edit' ? Pencil : Sparkles;
  return (
    <>
      <Button size={size} variant={target.kind === 'edit' ? 'secondary' : 'ghost'}
        leftIcon={<Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        onClick={e => { e.stopPropagation(); setOpen(true); }}>
        {target.kind === 'edit' ? 'Редактировать' : 'Нарисовать картинку'}
      </Button>
      {open && (
        <ImageEditorLayer projectId={projectId} projectName={projectName} target={target}
          onShowInFiles={onShowInFiles} onClose={() => setOpen(false)} />
      )}
    </>
  );
}

// Слой на весь экран: редактор на месте рабочей области проекта
export function ImageEditorLayer({ onClose, ...props }: Omit<EntryProps, 'size'> & { onClose: () => void }) {
  const enabled = useFeature(FLAGS.imageEditor);
  const mobile = useIsMobile();
  if (!enabled) return null;
  return (
    <div style={{
      position: 'fixed', inset: 0, zIndex: Z.overlay, background: ISLAND.canvas,
      padding: mobile ? 0 : ISLAND.pad, display: 'flex', flexDirection: 'column',
    }}>
      <ImageEditor {...props} onClose={onClose}
        onShowInFiles={props.onShowInFiles ? path => { onClose(); props.onShowInFiles?.(path); } : undefined} />
    </div>
  );
}
