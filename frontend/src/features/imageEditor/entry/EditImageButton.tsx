// Кнопка «Редактировать» под просмотром картинки проекта: тот же вход, что пункт дерева
// «Редактировать картинку». Без флага image-editor кнопки нет.

import { Pencil } from 'lucide-react';
import { Button, FLAGS, ICON_SIZE, ICON_STROKE, useFeature } from 'aihome_shell/kit';
import { isEditableImage } from '../format';
import { openFromTree } from './openFromTree';

export function EditImageButton({ projectId, projectName, path }: { projectId: string; projectName: string; path: string }) {
  const enabled = useFeature(FLAGS.imageEditor);
  if (!enabled || !isEditableImage(path)) return null;
  return (
    <Button size="sm" variant="secondary" leftIcon={<Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      onClick={e => { e.stopPropagation(); void openFromTree({ projectId, projectName, target: { kind: 'edit', path } }); }}>
      Редактировать
    </Button>
  );
}
