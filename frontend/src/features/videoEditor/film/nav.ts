// Открыть файл проекта в просмотре (и подсветить его в «Файлах»): снимок навигации того же вида, что
// пишет хост, — WorkspacePage применяет его по popstate. Вне проекта просмотра файлов нет — false
import { api, getNav, navPush, parseHash, type NavSnapshot } from 'aihome_shell/kit';

export async function openProjectFile(path: string, revealInTree = false): Promise<boolean> {
  const nav = getNav();
  const projectId = nav?.screen === 'project' ? nav.project?.id : parseHash()?.screen === 'project' ? parseHash()?.projectId : undefined;
  if (!projectId) return false;
  const project = nav?.project?.id === projectId ? nav.project : (await api.projects.list().catch(() => [])).find(p => p.id === projectId);
  if (!project) return false;
  // Без chatId: снимок с чатом заново открыл бы чат, и тот закрыл бы файл
  const next: NavSnapshot = { screen: 'project', project, file: path, view: 'chat', task: null, revealInTree };
  navPush(next);
  window.dispatchEvent(new PopStateEvent('popstate', { state: next }));
  return true;
}
