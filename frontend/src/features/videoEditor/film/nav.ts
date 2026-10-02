// Открыть файл проекта в просмотре (и подсветить его в «Файлах»): тот же снимок навигации, что пишет
// хост, — WorkspacePage применяет его по popstate. Вне проекта просмотра файлов нет — молча ничего
import { getNav, navPush } from 'aihome_shell/kit';

export function openProjectFile(path: string, revealInTree = false): boolean {
  const nav = getNav();
  if (nav?.screen !== 'project' || !nav.project) return false;
  const next = { ...nav, file: path, view: 'chat' as const, task: null, revealInTree };
  navPush(next);
  window.dispatchEvent(new PopStateEvent('popstate', { state: next }));
  return true;
}
