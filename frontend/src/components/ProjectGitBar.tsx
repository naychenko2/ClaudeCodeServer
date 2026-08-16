// Git-бар над композером чата (клод-стиль): слева «где мы работаем» — ветка/дерево
// чата и дерево текущего хода (turnWorktree), справа суммарный diff +N/−M, кнопки
// «Зафиксировать» и «Опубликовать N». Витрина поверх готовой механики: данные/мутации —
// из стора lib/git.ts, форма фиксации живёт в правой панели «Изменения» (сюда только
// ведём). Виден только в проектном чате на десктопе. Прячется, когда фиксировать и
// публиковать нечего И нет активного дерева: с активным деревом (чата или хода) бар
// показываем ВСЕГДА, даже с пустым диффом — иначе после переключения в свежее дерево
// узнать «где мы работаем» было бы неоткуда (композер значение дерева не показывает,
// там только кнопка-тумблер).
import { useCallback, useEffect, useState } from 'react';
import { GitBranch, FolderGit2, Check, CloudUpload, ChevronDown, ChevronUp, MessageSquare, Sparkles } from 'lucide-react';
import type { Project, Session } from '../types';
import { C, FONT, R, SP } from '../lib/design';
import { useWindowWidth, MOBILE_MAX, TABLET_MAX } from '../lib/breakpoints';
import { basename } from '../lib/paths';
import { ensureGit, useGitState, loadUnpushedLog, clearGitError, workingDiffStat } from '../lib/git';
import type { TurnTree } from '../lib/turnWorktree';
import { wsPanels } from '../pages/workspace/panelStackState';
import { PublishDialog } from './PublishDialog';
import { CommitPromptDialog } from './CommitPromptDialog';
import { Menu, MenuItem, MenuSep } from './ui';
import { ICON_STROKE } from './ui/icons';

// Ключ сворачивания гит-бара на планшете. На десктопе свернутого режима нет, и
// пользовательский выбор здесь не играет роли — оставлено для единой точки истины
// и возможного будущего расширения (например, «закрепить» slim-вариант на десктопе).
const COLLAPSED_KEY = 'cc-gitbar-collapsed';

export function ProjectGitBar({ project, session, turnTree = null, turnTreeLive = false, onCommitOwn, onCommitAll }: {
  project: Project;
  session?: Session;
  // Дерево ХОДА: агент внутри хода ушёл в свой git worktree (EnterWorktree), минуя
  // Session.worktreePath (см. lib/turnWorktree). Показываем вторым сегментом
  // нейтральным тоном; turnTreeLive — идёт ли ход сейчас (пульс-точка и формулировка title)
  turnTree?: TurnTree | null;
  turnTreeLive?: boolean;
  // Коммит только файлов этого диалога / всех изменений рабочего дерева
  onCommitOwn: () => void;
  onCommitAll: () => void;
}) {
  const st = useGitState(project.id);
  const status = st.status;
  const { reveal } = wsPanels.use();
  const [publishConfirm, setPublishConfirm] = useState(false);
  // Диалог стиля сообщений коммита — общий с панелью «Изменения»; открывается из
  // попапа фиксации, чтобы правила правились там же, где коммит и запускают
  const [promptOpen, setPromptOpen] = useState(false);
  // rect кнопки «Зафиксировать» — открытое меню выбора области коммита (null = закрыто)
  const [commitMenu, setCommitMenu] = useState<DOMRect | null>(null);
  // Чат в отдельном worktree: запросы стора уже идут в его дерево (gitSessionContext),
  // перечитываем статус при переключении дерева у активной сессии
  const worktreeBranch = session?.worktreeBranch ?? null;

  // Статус + стек незапушенных (для кнопки «Опубликовать»); realtime держит их свежими
  useEffect(() => {
    ensureGit(project.id, true);
    void loadUnpushedLog(project.id);
  }, [project.id, worktreeBranch]);

  // Меню коммита в anchor-режиме само не ловит Esc — закрываем на вызывающей стороне
  useEffect(() => {
    if (!commitMenu) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setCommitMenu(null); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [commitMenu]);

  const diff = workingDiffStat(status);
  const ahead = status?.ahead ?? 0;
  const behind = status?.behind ?? 0;
  const publishN = ahead > 0 ? ahead : st.unpushed.length;
  const canPublish = publishN > 0;

  // Нечего ни фиксировать, ни публиковать — бар не показываем, ЕСЛИ нет активного
  // дерева. Активное дерево (чата или хода) держит бар даже при пустом диффе.
  // Вне git-репозитория бару по-прежнему делать нечего
  const treeActive = !!worktreeBranch || !!turnTree;
  const isEmpty = diff.files === 0 && !canPublish;
  if (!status?.isRepo || (!treeActive && isEmpty)) return null;

  // Планшет (601–1199): компактная геометрия — slim-бар (44 + поля 6/6 ≈ 56px),
  // действия иконками. Сворачивание в микростроку 28 + поля 4/4 ≈ 36px, дефолт
  // свёрнуто. На десктопе и мобиле — без изменений (мобил вообще скрыт гейтом ChatPanel).
  const ww = useWindowWidth();
  const isCompact = ww > MOBILE_MAX && ww <= TABLET_MAX;

  // Состояние сворачивания: планшет → дефолт свёрнуто, десктоп → развёрнуто.
  // Значение в localStorage переживает перезагрузку и переключение проектов.
  // Под десктопом записи не пишутся: при первой попытке сохранить «не свёрнуто»
  // мы бы затирали планшетный выбор на машинах, где планшет не открывали —
  // вместо этого читаем дефолт прямо на десктопе, а в localStorage пишем только
  // когда сворачивание состоялось на планшете (через переход в slim→micro).
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    if (!isCompact) return false;
    try {
      const v = localStorage.getItem(COLLAPSED_KEY);
      return v === null ? true : v === '1';
    } catch { return true; }
  });
  // Синхронизируем сворачивание при смене раскладки: ушли с планшета на десктоп
  // → разворачиваем, пришли обратно — восстанавливаем из localStorage.
  useEffect(() => {
    try {
      if (!isCompact) { setCollapsed(false); return; }
      const v = localStorage.getItem(COLLAPSED_KEY);
      setCollapsed(v === null ? true : v === '1');
    } catch { /* ignore */ }
  }, [isCompact]);
  const setCollapsedPersist = useCallback((next: boolean) => {
    setCollapsed(next);
    try { localStorage.setItem(COLLAPSED_KEY, next ? '1' : '0'); } catch { /* ignore */ }
  }, []);

  // Метка: ветка worktree чата > имя папки (проект сам открыт как worktree) > ветка
  const label = worktreeBranch ?? (status.isWorktree ? basename(project.rootPath) : (status.branch ?? '—'));

  // Открыть панель «Изменения» на скоупе «Не зафиксировано» (working). Панель
  // живёт в любой из зон, поэтому просим стор её показать: reveal открывает
  // закрытую в её домашней рельсе и возвращает true, если она УЖЕ открыта.
  // В этом случае просим панель мигнуть: иначе клик выглядит как «ничего не
  // произошло», хотя скоуп в панели переключился.
  const openChanges = () => {
    if (reveal('changes')) {
      window.dispatchEvent(new CustomEvent('cc-panel-flash', { detail: { key: 'changes' } }));
    }
    window.dispatchEvent(new CustomEvent('cc-git-open-working'));
  };

  // Правило видимости уточняем для планшета: есть активное дерево, но действий нет —
  // показываем микростроку с одной меткой ветки. Разворачивать нечего, slim не поможет.
  const microOnly = isCompact && isEmpty && treeActive;
  // На планшете: либо микрострока (свёрнуто, либо действий нет), либо slim-бар
  const showMicro = isCompact && (collapsed || microOnly);

  // Базовый контейнер плашки: общий для slim и full, геометрия — параметром.
  // Микрострока использует свой собственный, упрощённый layout (28, без рамки).
  const shellStyle = isCompact
    ? {
        // Slim-планшет: 44 + поля 6/6 = 56px вместо десктопных 51 + 10/8 = 69px
        display: 'flex', alignItems: 'center', gap: 8, margin: '6px 0',
        height: 44, padding: '0 6px 0 10px',
        background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
      }
    : {
        display: 'flex', alignItems: 'center', gap: 12, margin: '10px 0 8px',
        height: 51, padding: '0 8px 0 12px',
        background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
      };

  // Метка ветки — без изменений на всех раскладках: это ответ на «где мы работаем».
  const branchLabel = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 7, minWidth: 0 }}>
      {worktreeBranch
        ? <FolderGit2 size={15} strokeWidth={ICON_STROKE} color={C.accent} style={{ flexShrink: 0 }} />
        : <GitBranch size={15} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />}
      <span title={worktreeBranch ? `Отдельное дерево чата: ${label}` : label} style={{
        fontFamily: FONT.mono, fontSize: 12.5, color: C.textSecondary,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>{label}</span>
      {behind > 0 && (
        <span
          title={`На сервере есть коммиты, которых нет локально: ${behind}`}
          style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, flexShrink: 0 }}
        >↓{behind}</span>
      )}
    </div>
  );

  // Сегмент дерева хода (turnTree). На планшете maxWidth 140 вместо 220 — slim-бар уже.
  const turnTreeSegment = turnTree ? (
    <span
      title={`${turnTreeLive ? 'Ход выполняется' : 'Последний ход выполнялся'} в дереве агента: ${turnTree.path}`}
      style={{
        display: 'flex', alignItems: 'center', gap: 6, height: 28, maxWidth: isCompact ? 140 : 220,
        padding: '0 11px', borderRadius: R.md, background: C.bgPanel,
        border: `1px solid ${C.border}`, color: C.textSecondary, flexShrink: 0, minWidth: 0,
      }}
    >
      <span style={{
        fontFamily: FONT.mono, fontSize: 12.5, whiteSpace: 'nowrap',
        overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0,
      }}>ход: {turnTree.name}</span>
      <span style={{
        width: 6, height: 6, borderRadius: R.full, background: C.info, flexShrink: 0,
        opacity: turnTreeLive ? 1 : 0.4,
        animation: turnTreeLive ? 'pulsedot 1.2s ease-in-out infinite' : 'none',
      }} />
    </span>
  ) : null;

  // diff-пилюля +N/−M. На планшете tap-цель выше (32 vs 28) — иначе под палец тесновато.
  const diffPill = diff.files > 0 ? (
    <button
      type="button"
      onClick={openChanges}
      title="Открыть изменения"
      style={{
        display: 'flex', alignItems: 'center', gap: 8,
        height: isCompact ? 32 : 28, padding: '0 11px',
        border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgWhite,
        cursor: 'pointer', fontFamily: FONT.mono, fontSize: 12.5, flexShrink: 0,
      }}
    >
      {diff.added > 0 && <span style={{ color: C.diffAddText }}>+{diff.added}</span>}
      {diff.deleted > 0 && <span style={{ color: C.diffRemText }}>−{diff.deleted}</span>}
      {diff.added === 0 && diff.deleted === 0 && <span style={{ color: C.textMuted }}>±0</span>}
    </button>
  ) : null;

  // Кнопка фиксации: десктоп — текст+галка+шеврон, планшет — иконочная 36×36 (только Check).
  // Меню и CommitPromptDialog одни и те же: состав не меняется, только кнопка-вход.
  const commitBtn = diff.files > 0 ? (
    <div style={{ position: 'relative', display: 'flex', flexShrink: 0 }}>
      <button
        type="button"
        onClick={e => setCommitMenu(e.currentTarget.getBoundingClientRect())}
        title="Зафиксировать изменения (git commit)"
        style={isCompact
          ? {
              // Иконочная 36×36 — TB.iconHitMobile плотностью. Только Check, без подписи
              // и ChevronDown — это просто вход в то же меню области коммита.
              width: 36, height: 36, padding: 0, justifyContent: 'center',
              border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard,
              cursor: 'pointer', display: 'flex', alignItems: 'center', flexShrink: 0,
            }
          : {
              display: 'flex', alignItems: 'center', gap: 6, height: 28, padding: '0 10px 0 12px',
              border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard,
              cursor: 'pointer', fontFamily: FONT.sans, fontSize: 12.5, color: C.textHeading,
            }}
      >
        <Check size={15} strokeWidth={ICON_STROKE} color={C.accent} />
        {!isCompact && <>
          {' '}Зафиксировать
          <ChevronDown size={14} strokeWidth={ICON_STROKE} color={C.textMuted} />
        </>}
      </button>
      {commitMenu && (
        // maxHeight — фактическая высота карточки (три пункта MenuItem ~34px,
        // разделитель и padding): по ней Menu решает, открываться вверх или вниз
        <Menu anchor={commitMenu} minWidth={200} maxHeight={124} gap={4} onClose={() => setCommitMenu(null)}>
          <MenuItem
            icon={<MessageSquare size={15} strokeWidth={ICON_STROKE} />}
            label="Только этот чат"
            onClick={() => { setCommitMenu(null); onCommitOwn(); }}
          />
          <MenuItem
            icon={<FolderGit2 size={15} strokeWidth={ICON_STROKE} />}
            label="Всё дерево"
            onClick={() => { setCommitMenu(null); onCommitAll(); }}
          />
          {/* Не область коммита, а его оформление — отделяем чертой */}
          <MenuSep />
          <MenuItem
            icon={<Sparkles size={15} strokeWidth={ICON_STROKE} />}
            label="Стиль сообщений коммита…"
            onClick={() => { setCommitMenu(null); setPromptOpen(true); }}
          />
        </Menu>
      )}
    </div>
  ) : null;

  // Кнопка публикации: на планшете — accent, height 36, иконка + mono-число N (без
  // слова «Опубликовать»). Dialog публикации общий.
  const publishBtn = canPublish ? (
    <button
      type="button"
      onClick={() => setPublishConfirm(true)}
      disabled={st.busy}
      title="Опубликовать (git push)"
      style={isCompact
        ? {
            display: 'flex', alignItems: 'center', gap: 6,
            height: 36, padding: '0 12px',
            border: 'none', borderRadius: R.md, background: C.accent, color: C.onAccent,
            cursor: st.busy ? 'default' : 'pointer',
            fontFamily: FONT.mono, fontSize: 12.5, fontWeight: 700, flexShrink: 0,
            opacity: st.busy ? 0.6 : 1,
          }
        : {
            display: 'flex', alignItems: 'center', gap: 6, height: 28, padding: '0 12px',
            border: 'none', borderRadius: R.md, background: C.accent, color: C.onAccent,
            cursor: st.busy ? 'default' : 'pointer', fontFamily: FONT.sans, fontSize: 12.5,
            fontWeight: 600, flexShrink: 0, opacity: st.busy ? 0.6 : 1,
          }}
    >
      <CloudUpload size={15} strokeWidth={ICON_STROKE} />
      {/* На планшете — только число, без слова «Опубликовать» (место экономит). */}
      {isCompact ? <span>{publishN}</span> : <>Опубликовать <span style={{ opacity: 0.85 }}>{publishN}</span></>}
    </button>
  ) : null;

  // Шеврон сворачивания (только планшет, только в slim-баре). Декор, не действие —
  // кнопка во всю зону тапа, шеврон — на правом краю, чтобы намерение читалось.
  const collapseBtn = isCompact && !microOnly ? (
    <button
      type="button"
      onClick={() => setCollapsedPersist(true)}
      title="Свернуть гит-бар"
      aria-label="Свернуть гит-бар"
      style={{
        width: 28, height: 28, padding: 0,
        background: 'transparent', border: 'none', cursor: 'pointer',
        color: C.textMuted, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        borderRadius: R.md,
      }}
      // hover-подложка только там, где курсор вообще бывает (десктоп/мышь);
      // на тач-экране hover'а нет — кнопка читается по иконке
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
    >
      <ChevronUp size={15} strokeWidth={ICON_STROKE} />
    </button>
  ) : null;

  // Микрострока: 28 + поля 4/4 = 36px. Вся строка — одна tap-цель (role=button).
  // Тап разворачивает. Действия в свёрнутом виде недоступны: на touch-экране
  // дробить 28px на четыре цели нельзя, и «Зафиксировать» без диффа не имеет смысла.
  // Здесь же рисуются дефолтные индикаторы действий (+N, −M, ↑N) — как «превью»,
  // чтобы человек видел состояние, не разворачивая.
  const microRow = (
    <button
      type="button"
      onClick={() => setCollapsedPersist(false)}
      title="Показать гит-действия"
      style={{
        display: 'flex', alignItems: 'center', gap: 8, margin: '4px 0',
        width: '100%',
        background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.lg,
        padding: '0 8px 0 10px', height: 28, cursor: 'pointer',
        // Не скрываем по hover — на тач-экране hover'а нет, и без подложки кнопка
        // выглядит как обычная подпись. Десктопный курсор получает лёгкий tint.
        transition: 'background 0.12s',
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = C.bgPanel; }}
    >
      {worktreeBranch
        ? <FolderGit2 size={14} strokeWidth={ICON_STROKE} color={C.accent} style={{ flexShrink: 0 }} />
        : <GitBranch size={14} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />}
      <span title={worktreeBranch ? `Отдельное дерево чата: ${label}` : label} style={{
        fontFamily: FONT.mono, fontSize: 12, color: C.textSecondary,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0,
      }}>{label}</span>
      {behind > 0 && (
        <span
          title={`На сервере есть коммиты, которых нет локально: ${behind}`}
          style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, flexShrink: 0 }}
        >↓{behind}</span>
      )}
      <span style={{ flex: 1 }} />
      {/* Индикаторы — не кликабельные по отдельности: вся строка — одна зона тапа. */}
      {diff.added > 0 && <span style={{ fontFamily: FONT.mono, fontSize: 11.5, color: C.diffAddText, fontWeight: 700, flexShrink: 0 }}>+{diff.added}</span>}
      {diff.deleted > 0 && <span style={{ fontFamily: FONT.mono, fontSize: 11.5, color: C.diffRemText, fontWeight: 700, flexShrink: 0 }}>−{diff.deleted}</span>}
      {publishN > 0 && <span style={{ fontFamily: FONT.mono, fontSize: 11.5, color: C.accent, fontWeight: 700, flexShrink: 0 }}>↑{publishN}</span>}
      <ChevronDown size={15} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
    </button>
  );

  // Планшет: при активном дереве без действий — только микрострока (slim не развернуть)
  if (microOnly) {
    return (
      <>
        {microRow}
        {st.error && (
          <div
            onClick={() => clearGitError(project.id)}
            title="Скрыть"
            style={{
              margin: '0 0 8px', padding: `0 ${SP.md}px`, cursor: 'pointer',
              fontFamily: FONT.sans, fontSize: 12, lineHeight: 1.4, color: C.dangerText,
            }}
          >
            {st.error}
          </div>
        )}
        {/* Поповер фиксации и диалоги публикации/стиля не нужны: действий нет. */}
      </>
    );
  }

  return (
    // Фрагмент: под плашкой должна вставать строка ошибки (родитель — вертикальный
    // поток ChatPanel), а самой плашке нужен свой height и фон без лишних наследников
    <>
    {showMicro ? microRow : (
    <div style={shellStyle}>
      {/* Ветка / имя worktree; папка-иконка — чат в отдельном дереве */}
      {branchLabel}

      {/* Дерево ХОДА (агент ушёл в свой worktree внутри хода): нейтральный сегмент
          рядом с меткой чата — читаются все сочетания: только ветка, только дерево
          чата, только дерево хода, оба дерева сразу. Полный путь — в title */}
      {turnTreeSegment}

      <div style={{ flex: 1 }} />

      {/* diff-пилюля +N/−M — кликом открывает панель «Изменения» */}
      {diffPill}

      {/* Зафиксировать — делегирует коммит чату; меню выбирает область: только
          изменения этого диалога («своё») или всё рабочее дерево. */}
      {commitBtn}

      {/* Опубликовать N — git push с подтверждением */}
      {publishBtn}

      {/* Шеврон сворачивания: только планшет, только в slim-режиме (в full свернуть
          нечего — десктоп-вариант всегда развёрнут). В microOnly рендер не заходим,
          поэтому здесь collapseBtn=null и сюда не попадёт. */}
      {collapseBtn}

    </div>
    )}

    {/* Ошибка git-операции: публикация запускается прямо из бара, и без этой строки
        её провал (например отклонённый push) остался бы невидимым при закрытой
        панели «Изменения». Клик — скрыть, как в самой панели */}
    {st.error && (
      <div
        onClick={() => clearGitError(project.id)}
        title="Скрыть"
        style={{
          margin: '0 0 8px', padding: `0 ${SP.md}px`, cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: 12, lineHeight: 1.4, color: C.dangerText,
        }}
      >
        {st.error}
      </div>
    )}

    {/* Подтверждение публикации: сверяется с origin и сам решает, звать push
        или «Подтянуть и опубликовать» (общий диалог с панелью «Изменения») */}
    {publishConfirm && (
      <PublishDialog projectId={project.id} onClose={() => setPublishConfirm(false)} />
    )}

    {/* Стиль сообщений коммита — из попапа фиксации; тот же диалог, что в настройках
        панели «Изменения» (один источник правды по уровням «общий»/«проектный») */}
    {promptOpen && <CommitPromptDialog project={project} onClose={() => setPromptOpen(false)} />}
    </>
  );
}
