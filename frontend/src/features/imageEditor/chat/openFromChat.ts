// Открыть редактор из ленты полного чата или из списка чатов: у чата картинки есть свой файл
// (imageChat.currentPath), имя проекта нужно шапке редактора.

import { api as appApi, getFlag, FLAGS, showToast } from 'aihome_shell/kit';
import type { Session } from '../../../types';
import { openImageEditor } from '../ImageEditorEntry';

let projectNames: Promise<Map<string, string>> | null = null;

function projectName(projectId: string): Promise<string> {
  projectNames ??= appApi.projects.list()
    .then(list => new Map(list.map(p => [p.id, p.name])))
    .catch(() => { projectNames = null; return new Map<string, string>(); });
  return projectNames.then(m => m.get(projectId) ?? '');
}

export interface OpenChatExtras {
  prompt?: string;
  job?: { jobId: string; count: number };
}

export function openImageChat(session: Session, extras?: OpenChatExtras): boolean {
  const path = session.imageChat?.currentPath;
  if (!path || !session.projectId || !getFlag(FLAGS.imageEditor)) return false;
  const projectId = session.projectId;
  void projectName(projectId).then(name => openImageEditor({
    projectId, projectName: name, target: { kind: 'edit', path }, sessionId: session.id,
    initialPrompt: extras?.prompt, showJob: extras?.job,
  }));
  return true;
}

export async function openImageChatById(sessionId: string, extras?: OpenChatExtras): Promise<void> {
  const session = await appApi.chats.get(sessionId).catch(() => null);
  if (!session || !openImageChat(session, extras)) showToast('Не удалось открыть редактор: у чата нет картинки', '', 'error');
}
