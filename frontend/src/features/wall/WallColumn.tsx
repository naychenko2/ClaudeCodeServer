// Колонка стены: остров с тонкой полосой-ярлыком (проект + статус + zoom) и
// ChatPanel в режиме embedded. Шапка ЧАТА — штатная (ChatHeaderBar внутри
// ChatPanel): колонка выглядит как настоящий чат, а не панель; ярлык сверху —
// не дубль шапки, а ответ на «чей это столбец» (на стене чаты РАЗНЫХ проектов,
// в самой шапке чата проекта нет). Фокус — акцентная рамка острова, ставится
// кликом по любому месту колонки.
//
// Осознанные срезы v2 (не баги): в колонку не проброшены skills/agents (пикеры
// навыков и агентов в композере пусты); onOpenFile дают только колонки С
// проектом (FileViewer требует project). Полная работа с проектом — zoom.
import { useState } from 'react';
import { Maximize2 } from 'lucide-react';
import type { Project, Session } from '../../types';
import { C, FONT, FS } from '../../lib/design';
import { Island, IconButton } from '../../components/ui';
import { ICON_SIZE } from '../../components/ui/icons';
import { ChatPanel } from '../../components/ChatPanel';
import { ProjectIcon } from '../projects/ProjectIcon';
import { chatStatus, focusChat, updateChat } from './wallStore';

export function WallColumn({ session, project, focused, onZoom, onOpenFile }: {
  session: Session;
  // undefined — чат вне проекта (это норма); null — проект чата не нашёлся (ошибка)
  project: Project | undefined | null;
  focused: boolean;
  onZoom: () => void;
  // Клик по файлу в ленте → оверлей стены; передаётся только колонкам с проектом
  onOpenFile?: (path: string) => void;
}) {
  const status = chatStatus(session);
  const busy = status === 'working' || status === 'waiting';
  // Вложения композера — per-колонка: загрузка кладёт файл в рабочую папку сессии
  // и возвращает путь сюда; заглушка-[] превращала бы скрепку в молчаливую потерю
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);

  return (
    <Island
      bg={C.bgMain}
      borderColor={focused ? C.accent : undefined}
      style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}
      // Фокус — капчур-фазой: клики внутри ленты/композера не должны глотаться по пути
      rootProps={{ onMouseDownCapture: () => focusChat(session.id) }}
    >
      {/* Полоса-ярлык колонки (~26px): проект, статус, zoom. Ниже — штатная шапка чата */}
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center', gap: 6,
        padding: '4px 8px', minHeight: 26, boxSizing: 'border-box',
      }}>
        {project && <ProjectIcon project={project} size={16} />}
        <span style={{
          flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {project === null ? 'проект недоступен' : project ? project.name : 'Чат вне проекта'}
          {busy && <span style={{ color: status === 'waiting' ? C.danger : C.warning }}> · {status === 'working' ? 'идёт ход' : 'ждёт вас'}</span>}
        </span>
        <IconButton size="sm" ariaLabel="Развернуть чат" title="Развернуть в полный вид" onClick={onZoom}>
          <Maximize2 size={ICON_SIZE.xs} strokeWidth={2} />
        </IconButton>
      </div>

      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        {project === null ? (
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 20, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, textAlign: 'center' }}>
            Проект этого чата недоступен — откройте его в разделе «Проекты» или уберите чат со стены.
          </div>
        ) : (
          <ChatPanel
            session={session}
            project={project ?? undefined}
            embedded
            attachedFiles={attachedFiles}
            onAttachedFilesChange={setAttachedFiles}
            onOpenFile={onOpenFile}
            // Смена модели/режима/цикла из колонки — снимок в сторе стены обязан обновиться
            onSessionUpdated={updateChat}
          />
        )}
      </div>
    </Island>
  );
}
