import { useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { GitBranch, ListChecks, MessageCircle } from 'lucide-react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ToolbarOverflowMenu, type OverflowItem } from '../ToolbarOverflowMenu';
import { Button } from '../ui/Button';
import { FeatureGlyph } from '../chat/feature/FeatureGlyphs';
import { formatDuration, MERGE_PENDING_STATUS, type FeatureStatusTag } from '../../lib/chatGroups';
import { getPersonaById } from '../../lib/personas';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { showToast } from '../../lib/toast';
import type { FeatureRun, FeatureState, FeatureAttention } from '../../types/featureRun';
import type { FeatureIndexFeature } from '../../types/featureIndex';
import { taskCounters } from './featurePageBody';
import { NavTargetButton } from './NavTargetButton';

// === Тег статуса (макет §3) ===
//
// Единая точка правды — run.state (9 значений, бэк Б1): из него бэк выводит bucket
// строки индекса и порядок, тег не расходится.
// Таблица макета — 8 подписей: `idle` («ничего не идёт, не ждёт, не закрыта»)
// показывается как `not_started` — для пользователя фича ещё не началась.
// `stageName` — имя активной стадии (тег «идёт · {этап}»); null — просто «идёт».
// Тон `loop` («Доработка») run.state не возвращает — только фолбэк по индексу.
export type FeatureRunTagTone = 'notStarted' | 'run' | 'wait' | 'stop' | 'merge' | 'loop' | 'merged' | 'closed';
export interface FeatureRunTag {
  label: string;
  tone: FeatureRunTagTone;
  // Точка 6 (C.info) у «ждёт вас» / «ждёт согласия на мерж».
  dot?: boolean;
}

export function featureRunStatusTag(state: FeatureState, stageName?: string | null): FeatureRunTag {
  switch (state) {
    case 'not_started':
    case 'idle':
      return { label: 'не начата', tone: 'notStarted' };
    case 'running':
      return { label: stageName ? `идёт · ${stageName}` : 'идёт', tone: 'run' };
    case 'waiting':
      return { label: 'ждёт вас', tone: 'wait', dot: true };
    case 'stopped':
      return { label: 'стоит', tone: 'stop' };
    case 'merge_pending':
      return { label: MERGE_PENDING_STATUS, tone: 'merge', dot: true };
    case 'merging':
      return { label: 'мерж · идёт', tone: 'run' };
    case 'merged':
      return { label: 'смержена', tone: 'merged' };
    case 'closed':
      return { label: 'закрыта без мержа', tone: 'closed' };
  }
}

// Старый тег индекса (featureStatusTag, 7 тонов) → тег страницы: тоны совпадают
// один в один (loop — только фолбэк по индексу, run.state его не возвращает).
export function toRunTag(t: FeatureStatusTag): FeatureRunTag {
  return { label: t.label, tone: t.tone as FeatureRunTagTone };
}

// Тон → вид (макет §3): не начата/закрыта — bgSelected/textSecondary; идёт —
// bgSelected/textHeading; ждёт/мерж — infoBg/textPrimary (+точка); стоит —
// dangerBg/dangerText + inset-кромка; доработка — warningBg/warningText;
// смержена — successBg/successText.
const TAG_STYLE: Record<FeatureRunTagTone, CSSProperties> = {
  notStarted: { background: C.bgSelected, color: C.textSecondary },
  run:      { background: C.bgSelected, color: C.textHeading },
  wait:     { background: C.infoBg, color: C.textPrimary },
  stop:     { background: C.dangerBg, color: C.dangerText, boxShadow: `inset 0 0 0 1px ${C.danger}` },
  merge:    { background: C.infoBg, color: C.textPrimary },
  loop:     { background: C.warningBg, color: C.warningText },
  merged:   { background: C.successBg, color: C.successText },
  closed:   { background: C.bgSelected, color: C.textSecondary },
};

// Тег: 11/600, поля 1 7, R.sm, строка 16 (макет §1). Точка — у «ждёт вас»/«к мержу».
export function StatusTag({ tag, testId }: { tag: FeatureRunTag; testId?: string }) {
  return (
    <span
      data-testid={testId}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0,
        fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans, lineHeight: '16px',
        padding: `${SP.xxs}px ${SP.xs}px`, borderRadius: R.sm, whiteSpace: 'nowrap',
        ...TAG_STYLE[tag.tone],
      }}
    >
      {tag.dot && (
        <span aria-hidden style={{ width: 6, height: 6, borderRadius: R.full, background: C.info, flexShrink: 0 }} />
      )}
      {tag.label}
    </span>
  );
}

// === Главное действие (макет §1/§2/§11) ===
//
// wait → «Ответить {имя}» (в чат), stopped → «Чат {имя}» (в чат), merge → «Мерж…»
// (действие Ф6: кнопка открывает общую модалку подтверждения, POST /merge).
// Имени нет (персона не резолвится) — нейтральная подпись. Тревоги нет → null.
export interface FeaturePrimaryAction {
  kind: 'chat' | 'merge';
  label: string;
  // Чат, который открывает действие; null — чата нет.
  chatId: string | null;
}

export function featurePrimaryAction(a: FeatureAttention | null | undefined, personaName?: string | null): FeaturePrimaryAction | null {
  if (!a) return null;
  if (a.kind === 'merge') return { kind: 'merge', label: 'Мерж…', chatId: a.linkedSessionId ?? null };
  const name = personaName ?? null;
  if (a.kind === 'stopped') {
    return { kind: 'chat', label: name ? `Чат ${name}` : 'Открыть чат', chatId: a.linkedSessionId ?? null };
  }
  return { kind: 'chat', label: name ? `Ответить ${name}` : 'Ответить', chatId: a.linkedSessionId ?? null };
}

// Имя персоны тревоги (null — не резолвится).
function attentionPersonaName(a: FeatureAttention | null | undefined): string | null {
  if (!a?.personaId) return null;
  return getPersonaById(a.personaId)?.name ?? null;
}

// === Мета-строка под шапкой (макет §1) ===
//
// Тег (только 360: на 1280 он в шапке) · ветка (клик копирует, тост) · возраст ·
// число задач. Поля 4 12, перенос; 12 textSecondary; ветка — mono 11 + GitBranch 12,
// наведение bgSelected, на 360 minHeight 40.
export interface FeaturePageHeaderProps {
  // null — данных страницы ещё нет (loading): строится по индексу.
  run: FeatureRun | null;
  // Строка индекса (ветка, createdAt, taskCount, ключевой чат); null — фичи нет в индексе.
  feature: FeatureIndexFeature | null;
  isMobile: boolean;
  // Тег страницы (считает FeaturePage: run.state, фолбэк — индекс).
  tag: FeatureRunTag | null;
}

export function FeaturePageHeader({ run, feature, isMobile, tag }: FeaturePageHeaderProps) {
  const branch = feature?.branch ?? null;

  // Копировать ветку: clipboard + тост (макет: «клик копирует, тост»).
  const [copied, setCopied] = useState(false);
  const copyBranch = () => {
    if (!branch) return;
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      void navigator.clipboard.writeText(branch).catch(() => {});
    }
    showToast('Ветка скопирована', branch);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  // Возраст: смержена/закрыта — «шла {итог}» (summary.durationMinutes, бэк считает);
  // не начата — «создана {N} назад»; иначе — «идёт {от создания до сейчас}».
  const createdAt = feature?.createdAt ?? run?.epic?.createdAt ?? null;
  let age: string | null = null;
  if (createdAt) {
    const created = new Date(createdAt).getTime();
    if (Number.isFinite(created)) {
      if (run?.state === 'merged' || run?.state === 'closed') {
        const d = run?.summary?.durationMinutes;
        age = d != null ? `шла ${formatDuration(d)}` : null;
      } else if (run?.state === 'not_started' || run?.state === 'idle') {
        age = `создана ${formatDuration(Math.max(1, Math.round((Date.now() - created) / 60000)))} назад`;
      } else {
        age = `идёт ${formatDuration(Math.max(1, Math.round((Date.now() - created) / 60000)))}`;
      }
    }
  }

  // «N задач» — ОДИН источник со строками вкладки «Задачи»: taskCounters(run).total
  // (честное число из run, не taskCount из индекса — индекс может расходиться с run).
  // run=null (loading) — прочерк: число из индекса (taskCount) показало бы значение,
  // которое изменится, когда run придёт.
  const total = run ? taskCounters(run).total : null;
  const tasks = total != null
    ? (total > 0 ? `${total} ${pluralTasks(total)}` : 'задач нет')
    : '—';

  const branchBtn = branch && (
    <button
      type="button"
      data-testid="feature-page-branch"
      onClick={copyBranch}
      title={copied ? 'Ветка скопирована' : 'Скопировать ветку'}
      aria-label={`Скопировать ветку ${branch}`}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, flex: '0 1 auto', minWidth: 0,
        height: isMobile ? 40 : 18, padding: `0 ${SP.xs}px`, margin: isMobile ? 0 : `-${SP.xxs}px -${SP.xs}px`,
        border: 'none', borderRadius: R.sm, background: 'transparent', cursor: 'pointer',
        fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
      }}
    >
      <GitBranch size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{branch}</span>
    </button>
  );

  return (
    <div
      data-testid="feature-page-meta"
      style={{
        display: 'flex', flexWrap: 'wrap', alignItems: 'center',
        gap: `${SP.xs}px ${SP.md}px`, fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans,
      }}
    >
      {isMobile && tag && <StatusTag tag={tag} testId="feature-page-status" />}
      {branchBtn}
      {age && <span>{age}</span>}
      <span data-testid="feature-page-taskcount">{tasks}</span>
    </div>
  );
}

// Склонение «задача/задачи/задач» (экспортируется для теста).
export function pluralTasks(n: number): string {
  const d = n % 10, h = n % 100;
  if (d === 1 && h !== 11) return 'задача';
  if (d >= 2 && d <= 4 && (h < 10 || h >= 20)) return 'задачи';
  return 'задач';
}

// === Шапка (Toolbar): тег (1280) · главное действие (1280) · меню ⋯ ===
//
// На 360 в шапке только ⋯ (40): тег уходит в мету, главное действие — в блок
// тревоги (макет §11). ⋯ — общий ToolbarOverflowMenu (десктоп — дропдаун,
// мобила — шторка, пункты 44): «Скопировать ветку», «Открыть эпик в «Задачах»»,
// «Открыть ключевой чат».
export interface FeaturePageToolbarProps {
  // null — данных страницы ещё нет (loading): строится по индексу.
  run: FeatureRun | null;
  feature: FeatureIndexFeature | null;
  isMobile: boolean;
  tag: FeatureRunTag | null;
  // «Мерж…»: открыть общую модалку подтверждения (Ф6); undefined — без действия.
  onMerge?: () => void;
}

export function FeaturePageToolbar({ run, feature, isMobile, tag, onMerge }: FeaturePageToolbarProps) {
  const branch = feature?.branch ?? null;
  const keyChat = feature?.keySessionId ?? null;
  const epic = run?.epic ?? null;

  // Копировать ветку (тот же тост, что в мете).
  const copyBranch = () => {
    if (!branch) return;
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      void navigator.clipboard.writeText(branch).catch(() => {});
    }
    showToast('Ветка скопирована', branch);
  };

  // Главное действие: wait/stopped → в чат исполнителя; merge → «Мерж…» (Ф6: общая
  // модалка, POST /merge); не начата → «Открыть ключевой чат» (ghost, макет §1).
  const attention = run?.attention ?? null;
  const primary = featurePrimaryAction(attention, attentionPersonaName(attention));
  const notStarted = run?.state === 'not_started';
  // «Мерж…» — только когда state === 'merge_pending'; никогда при merging/merged/closed.
  const mergePending = run?.state === 'merge_pending';
  const effectivePrimary = (primary?.kind === 'merge' && !mergePending) ? null : primary;
  const showPrimary = !isMobile && (effectivePrimary != null || (notStarted && keyChat != null));
  const onPrimary = () => {
    if (effectivePrimary?.kind === 'chat' && effectivePrimary.chatId) {
      void openChatById(effectivePrimary.chatId);
      return;
    }
    if (effectivePrimary?.kind === 'merge') {
      onMerge?.();
      return;
    }
    if (notStarted && keyChat) void openChatById(keyChat);
  };

  // Пункты ⋯: только существующие (ветки/эпика/ключевого чата нет — пункта нет).
  const items: OverflowItem[] = [];
  if (branch) {
    items.push({
      key: 'branch',
      icon: <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Скопировать ветку',
      onClick: copyBranch,
    });
  }
  if (epic) {
    items.push({
      key: 'epic',
      icon: <ListChecks size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Открыть эпик в «Задачах»',
      onClick: () => openTaskInSection({ id: epic.id, projectId: epic.projectId ?? undefined }),
    });
  }
  if (keyChat) {
    items.push({
      key: 'keychat',
      icon: <MessageCircle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Открыть ключевой чат',
      onClick: () => { void openChatById(keyChat); },
    });
  }

  return (
    <>
      {!isMobile && tag && <StatusTag tag={tag} testId="feature-page-status" />}
      {showPrimary && (
        <span data-testid="feature-page-main-action" style={{ display: 'inline-flex', flexShrink: 0 }}>
          <Button
            size="sm"
            variant={effectivePrimary ? 'primary' : 'ghost'}
            onClick={onPrimary}
          >
            {effectivePrimary ? effectivePrimary.label : 'Открыть ключевой чат'}
          </Button>
        </span>
      )}
      {items.length > 0 && (
        <ToolbarOverflowMenu
          isMobile={isMobile}
          title="Ещё"
          items={items}
        />
      )}
    </>
  );
}

// === Блок тревоги / «ждёт вас» (макет §2) ===
//
// Рисуется, когда run.attention есть (wait/stopped/merge) или фича не начата
// (нейтральный блок «Фича ещё не начата»). Тон: wait/merge — infoBg, stopped —
// dangerBg, не начата — bgSelected + рамка. Каркас: R.lg, поля 10 12, gap 9, 13,
// глиф 14 сверху, отступ сверху 10. Действия: главное (на 360; на 1280 оно в
// шапке) + пара «чат · задача» (NavTargetButton; у wait/stopped — только «задача»,
// чат — и есть главное действие).
export interface FeaturePageAlertProps {
  // null — данных страницы ещё нет (loading): тревоги нет.
  run: FeatureRun | null;
  feature: FeatureIndexFeature | null;
  isMobile: boolean;
  // «Мерж…»: открыть общую модалку подтверждения (Ф6); undefined — без действия.
  onMerge?: () => void;
  // Пометки «в архиве»/«задача закрыта» (страница даёт полные, с чатами); без пропа —
  // из самого агрегата.
  marks?: NavMarks;
}

export function FeaturePageAlert({ run, feature, isMobile, onMerge, marks: marksProp }: FeaturePageAlertProps) {
  const a = run?.attention ?? null;
  const marks = marksProp ?? buildNavMarks(run);
  const notStarted = run?.state === 'not_started';
  if (!a && !notStarted) return null;

  // Тон блока: stopped — danger, wait/merge — info, не начата — нейтральный.
  const tone: 'info' | 'danger' | 'neutral' =
    a?.kind === 'stopped' ? 'danger' : (a ? 'info' : 'neutral');
  const bg = tone === 'danger' ? C.dangerBg : tone === 'info' ? C.infoBg : C.bgSelected;
  const emphasis = tone === 'danger' ? C.dangerText : C.textHeading;

  // Глиф: wait/merge — ромб, stopped — квадрат, не начата — нейтральный run.
  const glyphKind = a?.kind === 'stopped' ? 'stopped' : a ? 'wait' : 'run';

  // Длительность: с attention.since до сейчас.
  const sinceMin = a?.since
    ? Math.max(0, Math.round((Date.now() - new Date(a.since).getTime()) / 60000))
    : null;
  const dur = sinceMin != null ? formatDuration(sinceMin) : null;
  const name = attentionPersonaName(a);

  // Главная фраза по kind (макет §2).
  let head: string;
  let reason: string | null = a?.reason ?? null;
  if (a?.kind === 'wait') {
    head = name
      ? `${name} ждёт вашего ответа${dur ? ` ${dur}` : ''}`
      : `Ждёт вашего ответа${dur ? ` ${dur}` : ''}`;
  } else if (a?.kind === 'stopped') {
    head = `Стоит${dur ? ` ${dur}` : ''}`;
  } else if (a?.kind === 'merge') {
    head = 'Мерж в master ждёт вашего согласия';
    reason = null;
  } else {
    head = 'Фича ещё не начата';
    reason = 'Запустить её можно из ключевого чата — куратор начнёт с разреза.';
  }

  // Действия: главное (360) + «чат» (только merge) + «задача».
  // «Мерж…» — только когда state === 'merge_pending'; при merging/merged/closed —
  // только пара «чат · задача» (кнопки нет).
  const primary = featurePrimaryAction(a, name);
  const mergePending = run?.state === 'merge_pending';
  const effectivePrimary = (primary?.kind === 'merge' && !mergePending) ? null : primary;
  // Id фичи для точки возврата «страница фичи → чат/задача» (Ф9b).
  const fid = run?.featureId ?? feature?.featureId ?? '';
  // Название фичи (epic.title) — для «← к фиче {название}» в шапке чата (Ф-pl-b).
  const featureTitle = run?.epic?.title ?? null;
  const acts: ReactNode[] = [];
  if (isMobile && effectivePrimary) {
    acts.push(
      <Button
        key="primary"
        size="xs"
        variant="primary"
        style={{ background: C.bgWhite, minHeight: 40 }}
        onClick={() => {
          if (effectivePrimary.kind === 'chat' && effectivePrimary.chatId) {
            recordFeatureReturn(fid, { kind: 'chat', id: effectivePrimary.chatId }, featureTitle);
            void openChatById(effectivePrimary.chatId);
          }
          else if (effectivePrimary.kind === 'merge') onMerge?.();
        }}
      >
        {effectivePrimary.label}
      </Button>,
    );
  }
  if (a?.kind === 'merge' && a.linkedSessionId) {
    acts.push(
      <NavTargetButton
        key="chat"
        kind="chat"
        chatId={a.linkedSessionId}
        chatArchived={marks.chatArchived(a.linkedSessionId)}
        label="чат"
        isMobile={isMobile}
        title="Открыть чат. Страница фичи скроется, вернётся по плашке."
        onOpen={() => {
          recordFeatureReturn(fid, { kind: 'chat', id: a.linkedSessionId! }, featureTitle);
          void openChatById(a.linkedSessionId!);
        }}
      />,
    );
  }
  if (a?.taskId) {
    acts.push(
      <NavTargetButton
        key="task"
        kind="task"
        taskId={a.taskId}
        taskClosed={marks.taskClosed(a.taskId)}
        label="задача"
        isMobile={isMobile}
        title="Открыть задачу. Страница фичи скроется, вернётся по плашке."
        onOpen={() => {
          recordFeatureReturn(fid, { kind: 'task', id: a.taskId! }, featureTitle);
          openTaskInSection({ id: a.taskId!, projectId: run?.epic?.projectId ?? undefined });
        }}
      />,
    );
  }
  // Не начата: «Открыть ключевой чат» (ghost) — на 360 в блоке, на 1280 в шапке.
  if (!a && isMobile) {
    const keyChat = feature?.keySessionId ?? run?.epic?.linkedSessionId ?? null;
    if (keyChat) {
      acts.push(
        <Button
          key="keychat"
          size="xs"
          variant="ghost"
          style={{ background: C.bgWhite, minHeight: 40 }}
          onClick={() => { void openChatById(keyChat); }}
        >
          Открыть ключевой чат
        </Button>,
      );
    }
  }

  return (
    <div
      data-testid="feature-page-alarm"
      data-tone={tone}
      role="status"
      aria-live="polite"
      style={{
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg,
        background: bg, fontSize: FS.base, fontFamily: FONT.sans, color: C.textPrimary,
        ...(tone === 'neutral' ? { border: `1px solid ${C.borderLight}` } : {}),
      }}
    >
      <span style={{ paddingTop: SP.xxs, display: 'inline-flex', flexShrink: 0 }}>
        <FeatureGlyph kind={glyphKind} size={14} />
      </span>
      <div style={{ minWidth: 0 }}>
        <div>
          <b style={{ color: emphasis }}>{head}</b>
          {reason && <span> {reason}</span>}
        </div>
        {acts.length > 0 && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.sm }}>{acts}</div>
        )}
      </div>
    </div>
  );
}
