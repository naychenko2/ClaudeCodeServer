// Клик по .film в дереве «Файлов» (макет v7): просмотр файла открывается как обычно, а панель «Видео»
// встаёт на вкладку «Фильм» с этим фильмом. Вклад file-viewer-toolbar рисуется под просмотром любого
// файла — для чужих расширений он пустой. Повторно панель открывается только кнопкой.

import { useEffect } from 'react';
import { Film } from 'lucide-react';
import { Button, FLAGS, ICON_SIZE, ICON_STROKE, useFeature } from 'aihome_shell/kit';
import { openFilmPanel } from '../scene/actions';

const opened = new Set<string>();

export const isFilmFile = (path: string) => /\.film$/i.test(path);

export function FilmFileOpener({ path }: { path: string }) {
  const on = useFeature(FLAGS.videoEditor);
  const film = on && isFilmFile(path);
  useEffect(() => {
    if (!film || opened.has(path)) return;
    opened.add(path);
    openFilmPanel(null, path);
  }, [film, path]);
  useEffect(() => () => { opened.delete(path); }, [path]);
  if (!film) return null;
  return (
    <Button size="sm" variant="secondary" leftIcon={<Film size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => openFilmPanel(null, path)}>
      Открыть в панели «Видео»
    </Button>
  );
}
