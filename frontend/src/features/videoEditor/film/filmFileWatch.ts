// Клик по .film в дереве «Файлов» (макет v7): просмотр файла открывается как обычно, а панель «Видео»
// встаёт на вкладку «Фильм» с этим фильмом. Дерево открывает файл снимком навигации (navPush шлёт
// NAV_CHANGE_EVENT) — его и слушаем, правок в ядре не нужно. Один и тот же файл подряд не открывает
// панель повторно: закрытую человеком панель возвращает только новый клик по другому файлу.

import { getNav, NAV_CHANGE_EVENT } from 'aihome_shell/kit';
import { openFilmPanel } from '../scene/actions';

export const isFilmFile = (path: string | null | undefined): path is string => !!path && /\.film$/i.test(path);

let last: string | null = null;

export function watchFilmFiles(sessionId: string): () => void {
  const on = () => {
    const nav = getNav();
    const file = nav?.screen === 'project' ? nav.file ?? null : null;
    if (file === last) return;
    last = file;
    if (isFilmFile(file)) openFilmPanel(sessionId, file);
  };
  window.addEventListener(NAV_CHANGE_EVENT, on);
  window.addEventListener('popstate', on);
  return () => { window.removeEventListener(NAV_CHANGE_EVENT, on); window.removeEventListener('popstate', on); };
}
