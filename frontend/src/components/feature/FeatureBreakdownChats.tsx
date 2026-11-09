// «Чаты группы» — раскрытие строки таблицы «Кто и на чём работал» (волна 5, ST-F10;
// макет feature-stats-breakdown-5.html §.chats, вариант C): список чатов выбранной
// группы. Подпись строки — «шаг · роль» из chatLabel (FeatureBreakdownModel, ST-F8),
// а не обрезанный id; чисел в строке чата нет: per-chat чисел в API нет, а числа
// группы живут в строке таблицы (ST-F11) — в строке чата они читались бы как
// метрики чата. Список резется шестью чатами, остаток — строка «ещё N» с
// склонением (макет chatsOf, html:278-280). Блок отделён сверху от таблицы
// (border-top + padding-top 12, макет .chats). Архивный чат — приглушён
// (C.textMuted) и помечен «в архиве» (состояние строит NavTargetButton, единая
// точка правды для пометки). Пустой список — блока нет (null).
// Токены — только C.*/FS/SP/R; высота строки 34, на 360 — 44 (тач-цель), как в
// макете .chat / .w360 .chat. Хуков здесь нет; переход — openChatById, как в ST-F6.

import { type CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { plural } from '../../lib/plural';
import { openChatById } from '../../lib/openChat';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import type { FeatureNavCtx } from '../../lib/featureNavMarks';
import { chatLabel } from './FeatureBreakdownModel';
import { NavTargetButton } from './NavTargetButton';
import type { FeatureBreakdownGroup } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';

export interface FeatureBreakdownChatsProps {
  // Выбранная группа: её чаты (chatIds). Числа группы здесь не показываются —
  // они живут в строке таблицы (ST-F11), per-chat чисел в API нет.
  group: FeatureBreakdownGroup;
  // Run для подписей: chatId → stepRef/taskRole через chatLabel.
  run: FeatureRun;
  isMobile?: boolean;
  // Контекст возврата: переход в чат пишет точку «← к фиче · Статистика».
  nav?: FeatureNavCtx;
}

// Отделитель блока сверху от таблицы: border-top + padding-top 12 (макет .chats,
// html:105).
const BLOCK: CSSProperties = {
  borderTop: `1px solid ${C.borderLight}`,
  paddingTop: SP.md,
};

// Подпись секции: FS.xxs UPPERCASE textMuted (как в других блоках), счётчик
// «N чатов» — со склонением (plural), как «N чатов ›» в записке.
const SEC_LABEL: CSSProperties = {
  fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: 0.6,
  color: C.textMuted, fontFamily: FONT.sans, marginBottom: SP.xs,
};

// Сетка чатов: три в ряд, на 360 — столбик (макет .chats .list / .w360 .chats .list).
function gridStyle(isMobile: boolean): CSSProperties {
  return {
    display: 'grid',
    gridTemplateColumns: isMobile ? '1fr' : 'repeat(3, 1fr)',
    gap: SP.sm,
  };
}

// Строка: bgWhite, borderLight, R.md, высота 34 (на 360 — 44) — макет .chat.
// Архивный чат — вся строка приглушена (макет .chat.arch).
function rowStyle(isMobile: boolean, archived: boolean): CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: SP.sm,
    height: isMobile ? 44 : 34,
    padding: `0 ${SP.sm}px`,
    border: `1px solid ${C.borderLight}`,
    borderRadius: R.md,
    background: C.bgWhite,
    color: archived ? C.textMuted : undefined,
    ...(isMobile ? { width: '100%' } : {}),
  };
}

// Шаг — моно-чип на bgSelected, жирный 700 (макет .ref: `font:700 11px mono`);
// роль — обычный текст.
const REF: CSSProperties = {
  fontSize: FS.xs, fontFamily: FONT.mono, fontWeight: 700,
  background: C.bgSelected, color: C.textHeading,
  borderRadius: R.sm, padding: `0 ${SP.xs}px`, flexShrink: 0,
};
const ROLE: CSSProperties = {
  fontSize: FS.sm, fontFamily: FONT.sans, fontWeight: 600,
  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};
// Строка остатка «ещё N чат/чата/чатов»: FS.xs textMuted (макет html:279-280;
// 6px из макета — ближайший шаг шкалы SP.xs).
const MORE: CSSProperties = {
  fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs, fontFamily: FONT.sans,
};

// Срез списка — шесть чатов, как в макете (chatsOf, list.slice(0, 6)).
const MAX_ROWS = 6;

// Архивность чата: по исполнителю с этим chatId (chatArchived); исполнителя нет —
// не архивный (о другом состоянии чата бэк в разбивке не сообщает).
function isArchived(run: FeatureRun, chatId: string): boolean {
  return (run.stages ?? []).flatMap(s => s.executors ?? []).some(e => e.chatId === chatId && e.chatArchived);
}

// Parts подписи: «Ш2» отдельным чипом, «доработка» — текстом. chatLabel — источник
// подписи; чип выделяем только когда шаг действительно в начале подписи.
function labelParts(chatId: string, run: FeatureRun): { ref: string | null; rest: string } {
  const label = chatLabel(chatId, run);
  const exec = (run.stages ?? []).flatMap(s => s.executors ?? []).find(e => e.chatId === chatId);
  if (exec?.stepRef && label.startsWith(`${exec.stepRef} · `)) {
    return { ref: exec.stepRef, rest: label.slice(exec.stepRef.length + 3) };
  }
  return { ref: null, rest: label };
}

export function FeatureBreakdownChats({ group, run, isMobile = false, nav }: FeatureBreakdownChatsProps) {
  // Хуков здесь нет; ранний return без хуков — безопасен (правило про хуки выше
  // return соблюдается vacuously).
  const chatIds = (group.chatIds ?? []).filter(Boolean);
  if (chatIds.length === 0) return null;
  const shown = chatIds.slice(0, MAX_ROWS);
  const hidden = chatIds.length - shown.length;

  return (
    <div data-testid="breakdown-chats" style={BLOCK}>
      <div style={SEC_LABEL}>
        Чаты · {chatIds.length} {plural(chatIds.length, 'чат', 'чата', 'чатов')}
      </div>
      <div style={gridStyle(isMobile)}>
        {shown.map((chatId: string) => {
          const archived = isArchived(run, chatId);
          const { ref, rest } = labelParts(chatId, run);
          return (
            <div
              key={chatId}
              data-testid={`breakdown-chat-${chatId}`}
              style={rowStyle(isMobile, archived)}
            >
              {ref ? <span aria-hidden style={REF}>{ref}</span> : null}
              <span style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: SP.xs }}>
                <span style={ROLE}>{rest}</span>
              </span>
              <NavTargetButton
                kind="chat"
                chatId={chatId}
                chatArchived={archived}
                title="Открыть чат"
                isMobile={isMobile}
                onOpen={() => {
                  if (nav?.featureId) recordFeatureReturn(nav.featureId, { kind: 'chat', id: chatId }, nav.featureTitle);
                  void openChatById(chatId);
                }}
              />
            </div>
          );
        })}
      </div>
      {hidden > 0 ? (
        <div style={MORE}>ещё {hidden} {plural(hidden, 'чат', 'чата', 'чатов')}</div>
      ) : null}
    </div>
  );
}
