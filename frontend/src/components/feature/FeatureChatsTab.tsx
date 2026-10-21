// Вкладка «Чаты» страницы фичи (волна 3, Ф3; макет features-overview-3-final §4/§9а).
//
// Данные — ТОЛЬКО из `features/{id}` → `chats[]` (FeatureChatLink: id, kind, taskId,
// archived, personaId, taskRole, stepRef, lane, modelLabel, modelFull); бэк не меняем.
//
// Строка чата: аватар · имя персоны · роль (roleLabel; исполнитель — «Ш2 · фронт») ·
// чип модели (ModelChip: modelLabel, подсказка modelFull по наведению/фокусу/тапу) ·
// пометка «ключевой» (kind=key) · пара «чат · задача» (NavTargetPair: chatId=chat.id, taskId).
//
// Порядок: ключевой → куратор/оркестратор → исполнители по шагам плана.
// «Архивные · N»: архивные (archived=true) свёрнуты под одной строкой-разделом,
// раскрытие по клику (клавиатура, aria-expanded); N = число архивных; нет архивных — нет раздела.
// Пустые: нет чатов → «У фичи пока нет чатов»; чата нет в снимке → «чата нет» (неактивна).
// Счётчик вкладки — число НЕархивных чатов (считает FeaturePage.tsx, макет §4).

import { useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, R, FS, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { getPersonaById } from '../../lib/personas';
import { roleLabel } from '../../lib/roleLabel';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { ModelChip, ExecutorAvatar } from './FeatureTimelineBits';
import { NavTargetPair } from './NavTargetButton';
import type { FeaturePage } from '../../types/featureIndex';
import type { FeatureChatLink, FeatureChatKind } from '../../types/featureChats';

// Сила вида (макет: key > curator > orchestrator > executor) — порядок строк.
const KIND_RANK: Record<FeatureChatKind, number> = { key: 0, curator: 1, orchestrator: 2, executor: 3 };

// Ключевой чат: kind=key (контракт) ИЛИ id = keySessionId (бэк, ADR-031 §1: вид key в
// chats не отдаёт — ключевой определяется по keySessionId, не по kind).
function isKeyChat(c: FeatureChatLink, keySessionId: string | null): boolean {
  return c.kind === 'key' || (keySessionId != null && c.id === keySessionId);
}

// Сортировочный ключ строки: сила вида (ключевой → куратор → оркестратор → исполнитель).
function chatKindRank(c: FeatureChatLink, keySessionId: string | null): number {
  const kind: FeatureChatKind = isKeyChat(c, keySessionId) ? 'key' : c.kind;
  return KIND_RANK[kind];
}

// Номер шага плана из stepRef («Ш2», «D1c» → 2, 1); нет — в конец.
function stepNumber(ref: string | null): number {
  if (!ref) return Number.MAX_SAFE_INTEGER;
  const m = ref.match(/\d+/);
  return m ? parseInt(m[0], 10) : Number.MAX_SAFE_INTEGER;
}

// Порядок строк: сила вида, затем (у исполнителей) номер шага плана. Стабильная сортировка.
export function sortFeatureChats(chats: FeatureChatLink[], keySessionId: string | null): FeatureChatLink[] {
  return [...chats].sort((a, b) => {
    const ka = chatKindRank(a, keySessionId);
    const kb = chatKindRank(b, keySessionId);
    if (ka !== kb) return ka - kb;
    // Оба (или один) — исполнители: по номеру шага плана.
    if (a.kind === 'executor' || b.kind === 'executor') {
      return stepNumber(a.stepRef) - stepNumber(b.stepRef);
    }
    return 0;
  });
}

// Подсказка «ключевой» (макет §9а): что это за чат.
const KEY_TIP = 'Ключевой чат — здесь куратор ведёт фичу: сюда приходят доклады исполнителей и вопросы к вам. Его название не меняется, пока фича жива.';

// Строка чата (макет §9а, .chrow): аватар · имя · роль · чип модели · «ключевой» ·
// пара «чат · задача». Ключевой — фон accentLight; архивный — имя приглушено.
// Навигация: чат — openChatById (chat.id), задача — openTaskInSection (taskId);
// точка возврата «страница фичи → чат/задача» — recordFeatureReturn (Ф9b).
function ChatRow({ chat, isKey, isMobile, featureId }: {
  chat: FeatureChatLink;
  isKey: boolean;
  isMobile: boolean;
  featureId: string;
}) {
  const [hover, setHover] = useState(false);
  const persona = chat.personaId ? getPersonaById(chat.personaId) : undefined;
  const name = persona?.name ?? (chat.personaId ? chat.personaId : null);
  const role = roleLabel(chat.taskRole, chat.stepRef, chat.lane);

  const openChat = () => {
    recordFeatureReturn(featureId, { kind: 'chat', id: chat.id });
    void openChatById(chat.id);
  };
  const openTask = () => {
    if (!chat.taskId) return;
    recordFeatureReturn(featureId, { kind: 'task', id: chat.taskId });
    openTaskInSection({ id: chat.taskId });
  };

  return (
    <div
      data-testid={`chat-row-${chat.id}`}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap',
        margin: '0 -8px', padding: '0 8px',
        background: isKey ? C.accentLight : (hover ? C.bgSelected : 'transparent'),
        borderRadius: R.md,
        minHeight: isMobile ? 52 : 44,
      }}
    >
      <ExecutorAvatar personaId={chat.personaId} />
      {name && (
        <span
          data-testid="chat-name"
          style={{
            fontSize: FS.sm, fontWeight: isKey ? 700 : 600,
            color: chat.archived ? C.textSecondary : C.textHeading,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}
        >
          {name}
        </span>
      )}
      {role && (
        <span
          data-testid="chat-role"
          style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, background: C.bgSelected, borderRadius: R.sm, padding: '0 5px', whiteSpace: 'nowrap', flexShrink: 0 }}
        >
          {role}
        </span>
      )}
      {chat.modelLabel && <ModelChip label={chat.modelLabel} full={chat.modelFull} isMobile={isMobile} />}
      {isKey && (
        <span
          data-testid="chat-key"
          title={KEY_TIP}
          style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.accent, background: C.accentLight, borderRadius: R.sm, padding: '0 5px', whiteSpace: 'nowrap', flexShrink: 0 }}
        >
          ключевой
        </span>
      )}
      <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: 4 }}>
        <NavTargetPair
          chatId={chat.id}
          chatArchived={chat.archived}
          onOpenChat={openChat}
          chatTitle={chat.archived
            ? 'Чат в архиве — откроется для чтения. Страница фичи скроется, вернётся по плашке.'
            : 'Открыть чат. Страница фичи скроется, вернётся по плашке.'}
          showNoChat
          taskId={chat.taskId}
          onOpenTask={openTask}
          taskTitle="Открыть задачу. Страница фичи скроется, вернётся по плашке."
          isMobile={isMobile}
        />
      </span>
    </div>
  );
}

// Раздел «Архивные · N» (макет §9а, .archb): свёрнутые архивные чаты; раскрытие по
// клику/клавиатуре (aria-expanded), шеврон поворачивается. N = число архивных.
function ArchivedSection({ chats, keySessionId, isMobile, featureId }: {
  chats: FeatureChatLink[];
  keySessionId: string | null;
  isMobile: boolean;
  featureId: string;
}) {
  const [open, setOpen] = useState(false);
  const sorted = sortFeatureChats(chats, keySessionId);
  return (
    <div data-testid="chats-archived">
      <button
        type="button"
        data-testid="chats-archived-toggle"
        aria-expanded={open}
        onClick={() => setOpen(v => !v)}
        style={{
          display: 'flex', alignItems: 'center', gap: 8, width: 'calc(100% + 16px)',
          margin: '6px -8px 0', padding: '6px 8px',
          border: 'none', background: 'transparent', cursor: 'pointer',
          borderRadius: R.md, minHeight: isMobile ? 44 : 36,
          fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary, textAlign: 'left',
        }}
      >
        <ChevronRight
          size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden
          style={{ flexShrink: 0, color: C.textMuted, transform: open ? 'rotate(90deg)' : undefined, transition: 'transform .15s' }}
        />
        <span style={{ flex: 1 }}>Архивные · {chats.length}</span>
      </button>
      {open && (
        <div data-testid="chats-archived-body" style={{ display: 'flex', flexDirection: 'column', gap: 2, marginTop: 2 }}>
          {sorted.map(c => (
            <ChatRow key={c.id} chat={c} isKey={isKeyChat(c, keySessionId)} isMobile={isMobile} featureId={featureId} />
          ))}
        </div>
      )}
    </div>
  );
}

export interface FeatureChatsTabProps {
  // Данные страницы: chats[] (FeatureChatLink) — единственный источник вкладки.
  page: FeaturePage;
  // Ключевой чат (из индекса, feature.keySessionId): бэк в chats key не отдаёт.
  keySessionId?: string | null;
  // Id фичи (навигация: точка возврата).
  featureId: string;
  isMobile?: boolean;
}

export function FeatureChatsTab({ page, keySessionId = null, featureId, isMobile = false }: FeatureChatsTabProps) {
  const all = page.chats ?? [];
  const live = sortFeatureChats(all.filter(c => !c.archived), keySessionId);
  const archived = all.filter(c => c.archived);

  // Пустое: нет чатов вовсе.
  if (all.length === 0) {
    return (
      <div data-testid="chats-empty" style={{ padding: '24px 0', textAlign: 'center', fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted }}>
        У фичи пока нет чатов
      </div>
    );
  }

  return (
    <div data-testid="chats" style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      {live.map(c => (
        <ChatRow key={c.id} chat={c} isKey={isKeyChat(c, keySessionId)} isMobile={isMobile} featureId={featureId} />
      ))}
      {archived.length > 0 && (
        <ArchivedSection chats={archived} keySessionId={keySessionId} isMobile={isMobile} featureId={featureId} />
      )}
    </div>
  );
}
