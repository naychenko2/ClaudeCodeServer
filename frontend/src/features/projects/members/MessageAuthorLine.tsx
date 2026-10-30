// Подпись автора над чужим пузырём в общем проекте (S2a-F1): UserAvatar 18, имя, время.
// Полный список участников нужен только ради имён, поэтому грузится здесь, а не в ленте,
// и только когда подпись вообще нужна (чужое сообщение в общем проекте).

import { useContext, useEffect, useState } from 'react';
import { C, FONT, FS, SP } from '../../../lib/design';
import { api } from '../../../lib/api';
import { readStoredUserId } from '../../../lib/offline';
import { onProjectMembersChanged } from '../../../lib/signalr';
import { formatPostTime, formatPostTimeFull } from '../../../lib/postTime';
import { UserAvatar } from '../../../components/ui/UserAvatar';
import { ChatProjectContext } from '../../../components/chat/contexts';
import type { ProjectMembersView } from '../../../types';
import { AUTHOR_GONE_LABEL, resolveMessageAuthor } from './messageAuthor';

// Список на проект общий для всех подписей ленты: иначе каждый чужой пузырь ходил бы за ним сам
const viewCache = new Map<string, Promise<ProjectMembersView>>();

// Свежие загрузки в полёте: N подписей на одно project_members_changed делят один запрос
const freshInflight = new Map<string, Promise<ProjectMembersView>>();

function loadView(projectId: string, fresh = false): Promise<ProjectMembersView> {
  if (fresh) {
    const running = freshInflight.get(projectId);
    if (running) return running;
  }
  let p = viewCache.get(projectId);
  if (!p || fresh) {
    p = api.projects.members.list(projectId);
    viewCache.set(projectId, p);
    // Сбой не кэшируем: следующая подпись попробует снова
    p.catch(() => { if (viewCache.get(projectId) === p) viewCache.delete(projectId); });
    if (fresh) {
      const mine = p;
      freshInflight.set(projectId, mine);
      const done = () => { if (freshInflight.get(projectId) === mine) freshInflight.delete(projectId); };
      mine.then(done, done);
    }
  }
  return p;
}

// Автор чужого сообщения человека в общем проекте; null — подпись не нужна (личный проект,
// нет senderUserId, свой). Список участников грузится только когда автор нужен
export function useMessageAuthor(senderUserId?: string) {
  const project = useContext(ChatProjectContext);
  const me = readStoredUserId();
  const needed = !!project && resolveMessageAuthor(project, senderUserId, me, null) !== null;
  const [view, setView] = useState<ProjectMembersView | null>(null);
  const projectId = project?.id;

  useEffect(() => {
    if (!needed || !projectId) return;
    let alive = true;
    const pull = (fresh: boolean) => {
      loadView(projectId, fresh).then(v => { if (alive) setView(v); }).catch(() => { /* имя останется запасным */ });
    };
    pull(false);
    const off = onProjectMembersChanged(({ projectId: id }) => { if (id === projectId) pull(true); });
    return () => { alive = false; off(); };
  }, [needed, projectId]);

  return resolveMessageAuthor(project, senderUserId, me, view);
}

export function MessageAuthorLine({ senderUserId, ts }: { senderUserId?: string; ts?: number }) {
  const author = useMessageAuthor(senderUserId);
  if (!author) return null;
  const time = formatPostTime(ts);
  return (
    <div data-testid="message-author" style={{
      display: 'flex', alignItems: 'center', gap: SP.xs, maxWidth: '100%', minWidth: 0,
      fontFamily: FONT.sans, fontSize: FS.xs,
    }}>
      <UserAvatar userId={author.userId} displayName={author.name} size={18} gone={author.gone} />
      <span style={{
        fontWeight: 600, color: author.gone ? C.textMuted : C.textSecondary,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0,
      }}>
        {author.name}
      </span>
      {author.gone && <span style={{ color: C.textMuted, whiteSpace: 'nowrap' }}>· {AUTHOR_GONE_LABEL}</span>}
      {time && <span title={formatPostTimeFull(ts) ?? undefined} style={{ color: C.textMuted, whiteSpace: 'nowrap' }}>{time}</span>}
    </div>
  );
}
