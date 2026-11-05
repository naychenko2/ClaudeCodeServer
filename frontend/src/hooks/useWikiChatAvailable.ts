import { useEffect } from 'react';
import { useFeature, FLAGS } from '../lib/featureFlags';
import { useSlotItem } from '../lib/subsystems/registryCore';
import type { ChatWikiMenuCtx } from '../lib/subsystems/registryCore';
import { ensureWikiLoaded, useWikiTree } from '../lib/wiki';

// Доступно ли действие «Вики» в этом чате: флаг llm-wiki, чат в проекте, роль не отказана,
// «Заметки» дали вклад меню и вики владельца заведена. Пока дерево не загружено — false:
// пункт скрывают, а не показывают серым. Дерево догружается только когда всё остальное уже
// сошлось, поэтому пользователь без флага (или без подсистемы) не делает ни одного запроса вики.
// Доступность считается от текущего projectId: запоздалый ответ дерева чат вне проекта
// доступным не делает.
export function useWikiChatAvailable(projectId: string | null | undefined, denied: boolean): boolean {
  const flagOn = useFeature(FLAGS.llmWiki);
  const menu = useSlotItem<ChatWikiMenuCtx>('chat-header-action', 'wiki-chat-menu');
  const tree = useWikiTree();
  const ready = flagOn && !!projectId && !denied && !!menu;
  useEffect(() => {
    if (ready) void ensureWikiLoaded();
  }, [ready]);
  return ready && tree?.initialized === true;
}
