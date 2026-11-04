// «Капсула идущих ходов» — компактный список идущих ходов общего проекта (S2a-F2).
//
// Зачем: в шапке проекта участник должен видеть, кто из участников сейчас работает
// и в каком чате, — но чужой приватный чат при этом остаётся приватным: из него
// показываем только факт хода (решение 28.10 в спецификации
// docs/mockups/shared-projects-v2-proposal.md, п.12: «Мария работает в приватном
// чате» без имени чата и без перехода).
//
// Поведение:
// - 0 ходов — компонент ничего не рисует (null);
// - чужой приватный чат — строка «X работает в приватном чате»: Lock + текст,
//   без ссылки и без названия чата, курсор обычный;
// - остальные ходы — строка: UserAvatar (18 — размер строк по спецификации v2,
//   20 — по запросу встраивающей стороны) + имя + название чата со ссылкой.
//
// Переход — openChatById (единственная точка правды для открытия чата по id:
// сама определяет, проект это чат или вне проекта). href строится из контекста
// проекта, если он передан, — иначе глобальный #/chats/{id}; та же схема, что у
// плашки «Ветка от …» в ChatItemView.
//
// Компонент самостоятельный: хуков и своего состояния нет, в HubHeader его
// встраивает S2a-F3. Токены — только C.*/FS/SP/R/FONT; строки живут на 360
// (тексты с многоточием, минимальная ширина 0).

import { type CSSProperties } from 'react';
import { Lock } from 'lucide-react';
import { C, FONT, FS, SP } from '../../../lib/design';
import { UserAvatar } from '../../../components/ui';
import { openChatById } from '../../../lib/openChat';

export interface RunningTurn {
  chatId: string;
  chatTitle: string;
  userId: string;
  displayName: string;
  // Чужой приватный чат: название чата и переход скрыты от участников,
  // виден только факт идущего хода
  isPrivateOfOther: boolean;
}

export interface RunningTurnsPillProps {
  turns: RunningTurn[];
  // Проект, к которому относится капсула: с ним href ссылки проект-скоуп
  // (#/project/{pid}/chat/{id}), без него — глобальный #/chats/{id}.
  // Сам переход — openChatById в обоих случаях.
  projectId?: string;
  // Размер аватара по спецификации v2: 18 — строки (по умолчанию), 20 — стопки
  avatarSize?: 18 | 20;
}

// Контейнер: колонка строк, тянется по ширине родителя, на 360 — тот же список
const LIST: CSSProperties = {
  display: 'flex', flexDirection: 'column', gap: SP.xxs, minWidth: 0, maxWidth: '100%',
};
const ROW: CSSProperties = {
  display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0, padding: `${SP.xxs}px 0`,
};
// Имя участника — как подпись автора в ленте (FS.xs 600 textSecondary), короткое,
// не тянется: обрезается первым, чтобы название чата осталось читаемым
const NAME: CSSProperties = {
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary,
  flexShrink: 0, maxWidth: '40%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};
const SEP: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 };
// Название чата — текстовая ссылка (accent + подчёркивание, как ссылки в MarkdownViewer)
const TITLE: CSSProperties = {
  fontFamily: FONT.sans, fontSize: FS.sm, color: C.accent, textDecoration: 'underline',
  textUnderlineOffset: 2, cursor: 'pointer', minWidth: 0,
  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};
// Строка приватного хода — обычный текст без перехода (курсор не подсказывает клик)
const PRIVATE_TEXT: CSSProperties = {
  fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary,
  minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};

export function RunningTurnsPill({ turns, projectId, avatarSize = 18 }: RunningTurnsPillProps) {
  // Пустой список — ничего не рисуем (хуков здесь нет, ранний return безопасен)
  if (turns.length === 0) return null;
  return (
    <div data-testid="running-turns" style={LIST}>
      {turns.map((t) => {
        if (t.isPrivateOfOther) {
          return (
            <div key={t.chatId} style={ROW}>
              <Lock size={12} strokeWidth={2} color={C.textMuted} style={{ flexShrink: 0 }} />
              <span style={PRIVATE_TEXT}>{t.displayName} работает в приватном чате</span>
            </div>
          );
        }
        const href = projectId
          ? `#/project/${encodeURIComponent(projectId)}/chat/${encodeURIComponent(t.chatId)}`
          : `#/chats/${encodeURIComponent(t.chatId)}`;
        return (
          <div key={t.chatId} style={ROW}>
            <UserAvatar userId={t.userId} displayName={t.displayName} size={avatarSize} />
            <span style={NAME}>{t.displayName}</span>
            <span style={SEP} aria-hidden>·</span>
            <a
              href={href}
              style={TITLE}
              title={`Открыть чат: ${t.chatTitle}`}
              onClick={(e) => { e.preventDefault(); void openChatById(t.chatId); }}
            >
              {t.chatTitle}
            </a>
          </div>
        );
      })}
    </div>
  );
}
