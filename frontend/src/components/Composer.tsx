import { useState, useRef, useEffect, useLayoutEffect, useCallback, type CSSProperties, type ReactNode } from 'react';
import { AlertTriangle, Ban, ArrowUp, Check, ChevronDown, FolderGit2, Lock, Mic, Paperclip, Plus, RefreshCw, Users, WifiOff, X } from 'lucide-react';
import { C, R, FS, FONT, MODAL_W, SHADOW, Z } from '../lib/design';
import { type RateWindow, RATE_COLORS, windowLabel, fmtReset } from '../lib/rateLimit';
import { SkillsDropdown } from './SkillsDropdown';
import { MentionsDropdown } from './MentionsDropdown';
import { CompanionSelector, type CompanionSelection } from './CompanionSelector';
import { ToolbarOverflowMenu, type OverflowItem } from './ToolbarOverflowMenu';
import { useToolbarOverflow } from '../hooks/useToolbarOverflow';
import { ComposerModelPicker } from './ComposerModelPicker';
import { USAGE } from '../lib/models';
import { ComposerEffortPicker } from './ComposerEffortPicker';
import { TeamDrawer } from '../features/team/TeamDrawer';
import {
  DEFAULT_TEAM_SETTINGS, buildTeamTurnText, teamMechanic,
  type TeamMechanicId, type TeamMechanicSettings,
} from '../features/team/teamMechanics';
import { TeamImplementBadge } from '../features/team/TeamImplementBadge';
import { teamImplementModeLocked, TEAM_IMPLEMENT_MODE_LOCKED_TOOLTIP } from '../lib/teamImplement';
import { setLastMechanic } from '../lib/lastMechanic';
import { type Mode, MODE_META, MODES, ModeIcon, isDangerMode } from '../lib/modes';
import { DangerModeConfirm } from './DangerModeConfirm';
import { useAssistantName } from './chat/contexts';
import { getDraft, setDraft } from '../lib/drafts';
import { showToast } from '../lib/toast';
import { Modal } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useVoiceInput } from '../hooks/useVoiceInput';
import type { SkillInfo, AgentInfo, Persona, WorkLoopState, SessionTeamImplement } from '../types';

export interface ComposerProps {
  // Ключ чата — под него хранится черновик недовведённого текста
  sessionId: string;
  onSend: (text: string, attachments: string[], opts?: { auto?: boolean }) => void;
  onStop: () => void;
  onAttach: () => void;
  isGenerating: boolean;
  mode: Mode;
  onModeChange: (mode: Mode) => void;
  // false → провайдер модели не поддерживает режим «План» — прячем его из списка
  planAvailable?: boolean;
  attachments: string[];
  onRemoveAttachment: (path: string) => void;
  // Вставка/перетаскивание любых файлов (скриншот, pdf, документ) — File-объекты
  // для загрузки и отправки. Что делать с картинками у модели без зрения — решает родитель
  onAttachFiles?: (files: File[]) => void;
  isMobile?: boolean;
  // Офлайн: показываем заглушку вместо полей, но НЕ размонтируем компонент —
  // иначе теряется набранный черновик при кратком пропадании сети
  offline?: boolean;
  skills?: SkillInfo[];
  // Единый селектор «собеседника» (персона или .md-агент Claude); смена доступна
  // и по ходу разговора. hasMessages оставлен в пропсах для совместимости.
  personas?: Persona[];
  agents?: AgentInfo[];
  selectedPersona?: Persona | null;
  selectedAgentName?: string | null;
  onCompanionChange?: (sel: CompanionSelection) => void;
  canPickCompanion?: boolean;
  hasMessages?: boolean;
  // Выбор модели прямо в полосе контролов (слева от собеседника). Провайдер любой —
  // смену провайдера у начатого чата родитель проводит миграцией, см. chatStarted.
  model?: string | null;
  onModelChange?: (model: string) => void;
  chatStarted?: boolean;
  // Усилие рассуждения (--effort). Родитель не передаёт onEffortChange, если провайдер
  // модели усилие не поддерживает (caps.supportsEffort) — тогда пикера просто нет.
  effort?: string | null;
  onEffortChange?: (effort: string) => void;
  // Групповой чат: id участников (упоминаются первыми в @автокомплите; в группе
  // @упоминания работают независимо от флага persona-mentions)
  participantIds?: string[] | null;
  // Создание нового группового чата из селектора собеседника (флаг persona-group-chats)
  onCreateGroup?: (personaIds: string[]) => void;
  // Цикл «до готово» (флаг work-loop): текущее состояние (live с фолбэком на Session.workLoop);
  // null — цикл выключен. Тумблер виден при заданном onToggleWorkLoop.
  // Promise — чтобы автопилот с «до готово» мог дождаться включения цикла до отправки
  workLoop?: WorkLoopState | null;
  onToggleWorkLoop?: () => void | Promise<void>;
  // Режим «Командная реализация»: состояние (live с фолбэком
  // на Session.teamImplement); null — режим выключен. Бейдж виден при заданных обработчиках
  teamImplement?: SessionTeamImplement | null;
  onToggleTeamImplementAuto?: () => void | Promise<void>;
  onDisableTeamImplement?: () => void | Promise<void>;
  // «Остановить» прогон: режим остаётся включённым, новые волны не стартуют
  onStopTeamImplement?: () => void | Promise<void>;
  // Включение режима из карточки механики «Командная реализация»: состав (пустой =
  // вся команда проекта) и авто-волны. Без обработчика карточка ничего не делает
  onEnableTeamImplement?: (opts: { autoWaves: boolean; executorPersonaIds: string[] }) => void | Promise<void>;
  // Чат внутри проекта: вне проекта у режима нет команды по умолчанию
  isProjectChat?: boolean;
  // Онбординг-интервью (Session.onboardingKind): команды в чате ещё нет, поэтому
  // раскрывашка «Обсудить с командой» не показывается вовсе
  onboarding?: boolean;
  // Отдельное git worktree чата: имя ветки (null — чат в основном дереве проекта).
  // Тумблер виден при заданном onToggleWorktree (только проектный чат с git).
  // Само имя ветки здесь НЕ показываем — оно живёт в git-баре над композером
  // (ProjectGitBar), в композере остаётся только управление
  worktreeBranch?: string | null;
  onToggleWorktree?: () => void | Promise<void>;
  // Краткий контекст последних реплик чата — для механики «Панель экспертов»
  // с настройкой «Приложить контекст чата» (собирает ChatPanel из ленты)
  chatContext?: string;
  // Подсказка следующего сообщения: текст от сервера после хода,
  // null — подсказки нет. Чип виден при пустом поле; принятие — тап / → / Tab
  promptSuggestion?: string | null;
  // Худшее окно лимита подписки (worstWindow) — для полоски-индикатора по кромке
  // композера. Полоска видна при level !== 'normal' (warn/danger).
  rateWindow?: RateWindow;
  // «Стоп» вернул прерванное сообщение в композер (фича «честная очередь»). Подставляется
  // только в ПУСТОЕ поле — набранный черновик важнее. seq меняется на каждое событие.
  restore?: { text: string | null; attachedPaths: string[] | null; mode: string | null; seq: number } | null;
  // Замена всего списка вложений при restore (родитель владеет attachedFiles).
  onReplaceAttachments?: (paths: string[]) => void;
  // Сигнал «поставь курсор в поле»: растущее число (на стене — фокус колонки).
  // Именно счётчик, а не boolean: повторный запрос на то же значение не сработал бы.
  focusSignal?: number;
}

// Ступени полосы контролов («губы» под полем ввода) — по ширине САМОЙ полосы, не окна.
// Ниже STRIP_COMPACT правая группа (модель, усилие, собеседник) и селектор режима живут
// иконками без подписей; выше STRIP_WIDE собеседнику разрешена длинная подпись. Между
// ними — подписи есть, но короткие. Замеряется в самом Composer (stripWidth).
const STRIP_COMPACT = 640;
const STRIP_WIDE = 900;

// Получить имя файла из пути
function basename(filePath: string): string {
  return filePath.replace(/\\/g, '/').split('/').pop() ?? filePath;
}

// Длинное имя режем по середине, а не с конца: расширение должно остаться видно
function middleEllipsis(name: string, max = 30): string {
  if (name.length <= max) return name;
  const head = Math.ceil((max - 1) / 2);
  const tail = max - 1 - head;
  return `${name.slice(0, head)}…${name.slice(name.length - tail)}`;
}

// Иконка файла по расширению
function FileIcon({ name }: { name: string }) {
  const ext = name.split('.').pop()?.toLowerCase() ?? '';
  const color =
    ['ts', 'tsx'].includes(ext) ? '#3178C6' :
    ['js', 'jsx'].includes(ext) ? '#F7DF1E' :
    ext === 'json' ? '#CB8A1F' :
    ext === 'md' ? '#5C5246' :
    ext === 'cs' ? '#9B4F96' :
    '#8A8072';

  return (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" style={{ flexShrink: 0 }}>
      <rect x="2" y="1" width="8" height="11" rx="1.5" fill={color} opacity="0.18" stroke={color} strokeWidth="1" />
      <text x="6" y="9" textAnchor="middle" fontSize="4.5" fill={color} fontFamily="monospace" fontWeight="700">
        {ext.slice(0, 3).toUpperCase()}
      </text>
    </svg>
  );
}

// SVG стоп
function StopIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
      <rect x="2" y="2" width="12" height="12" rx="2" fill="currentColor" />
    </svg>
  );
}

// Дорожка-«волна» при записи (псевдо: SpeechRecognition не даёт амплитуду — анимируем полоски)
function Waveform() {
  const delays = [0.0, 0.12, 0.28, 0.45, 0.6, 0.32, 0.15, 0.5, 0.05, 0.36, 0.18, 0.42];
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 3, flex: 1, height: 22, overflow: 'hidden' }}>
      {delays.map((d, i) => (
        <span key={i} className="cc-wave-bar" style={{ height: 22, animationDelay: `${d}s` }} />
      ))}
    </div>
  );
}

function fmtRecTime(s: number): string {
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

// Сегментированная пилюля активного режима — «склейка» кнопки-тумблера с её значением
// (вариант A из docs/mockups/composer-toggle-chip-merge.html). Грамматика полосы:
// выключенный режим — прежняя круглая кнопка в левом ряду, включённый — пилюля в группе
// состояния, чей ведущий сегмент-иконка и есть та самая кнопка (иконка и действие
// прежние, порядок пилюль повторяет порядок кнопок). Опасная зона НЕ расширяется:
// действие живёт только на сегменте ~28px, значение пассивно (подробности в title).
//
// Склейку используют цикл «до готово» и командная механика. Дерево чата — осознанное
// ИСКЛЮЧЕНИЕ (круглый тумблер без значения, см. worktreeButton): его значение живёт
// в git-баре над композером. Не «унифицировать» пилюлю дерева обратно!
function ModePill({
  isMobile,
  icon,
  leadTitle,
  onLeadClick,
  leadDisabled = false,
  valueTitle,
  maxWidth,
  value,
  trailing = null,
}: {
  isMobile?: boolean;
  // Иконка сегмента — обязана совпадать с иконкой круглой кнопки, из которой переехала
  icon: ReactNode;
  leadTitle: string;
  // Не задан — сегмент пассивен (курсор обычный, клик ничего не делает)
  onLeadClick?: () => void;
  // Гейт «идёт ход»: та же форма, что у заблокированной круглой кнопки (opacity .4)
  leadDisabled?: boolean;
  valueTitle?: string;
  maxWidth?: number;
  // Значение пилюли (номер итерации цикла, короткое имя механики)
  value: ReactNode;
  // Доп. узел в хвосте значения (✕ командной механики)
  trailing?: ReactNode;
}) {
  const h = isMobile ? 30 : 28; // высоты пилюли из макета (badge-шкалы в design.ts нет)

  return (
    <span style={{
      display: 'inline-flex', alignItems: 'stretch', height: h, maxWidth: maxWidth ?? '100%',
      borderRadius: R.pill, overflow: 'hidden', flexShrink: 0,
      background: C.accentLight,
    }}>
      <button
        type="button"
        title={leadTitle}
        aria-label={leadTitle}
        onClick={leadDisabled ? undefined : onLeadClick}
        disabled={leadDisabled}
        onMouseEnter={e => { if (!leadDisabled && onLeadClick) e.currentTarget.style.background = C.accentMuted; }}
        onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
        // Кольцо — outline внутри сегмента: SHADOW.focus рисуется наружу и срезался бы
        // родительским overflow:hidden пилюли
        onFocus={e => { e.currentTarget.style.outline = `2px solid ${C.accent}`; e.currentTarget.style.outlineOffset = '-2px'; }}
        onBlur={e => { e.currentTarget.style.outline = 'none'; }}
        style={{
          width: h, flexShrink: 0, border: 'none', padding: 0,
          borderRight: `1px solid ${C.accentMuted}`,
          background: 'transparent',
          color: C.accent,
          cursor: leadDisabled || !onLeadClick ? 'default' : 'pointer',
          opacity: leadDisabled ? 0.4 : 1,
          display: 'flex', alignItems: 'center', justifyContent: 'center', outline: 'none',
          transition: 'background 0.15s, opacity 0.15s',
        }}
      >
        {icon}
      </button>
      <span
        title={valueTitle}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 6, minWidth: 0,
          padding: '0 9px',
          color: C.accent,
          fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
        }}
      >
        {value}
        {trailing}
      </span>
    </span>
  );
}

// Полоска-индикатор лимита подписки по верхней кромке карточки композера.
// Absolute внутри карточки — не сдвигает ленту и композер (никаких «прыжков» высоты).
// Толщина одна (3px) и для warn, и для danger — серьёзность несёт только цвет
// (RATE_COLORS[level].fill). Детали — в поповере: hover на desktop, tap на mobile.
function RateStripe({ w, isMobile }: { w: RateWindow; isMobile?: boolean }) {
  const [open, setOpen] = useState(false);
  const c = RATE_COLORS[w.level];
  const reached = w.level === 'danger';
  const reset = fmtReset(w.resetsAt);
  // Оверрасход всегда даёт level=danger (см. rateLevel), поэтому «+» уместен только в
  // danger-ветке, а не в warn — здесь его нет
  const detail = reached
    ? `${windowLabel(w.limitType)} — лимит достигнут${reset ? ` · сброс ${reset}` : ''}`
    : `${windowLabel(w.limitType)} — ${w.pct}%${reset ? ` · сброс ${reset}` : ''}`;
  // desktop — hover; mobile — tap с overlay для закрытия по нажатию вне
  const hostEvents = isMobile
    ? { onClick: () => setOpen(o => !o) }
    : { onMouseEnter: () => setOpen(true), onMouseLeave: () => setOpen(false) };
  return (
    <>
      {open && isMobile && (
        <div onClick={() => setOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: Z.dropdown - 1 }} />
      )}
      <div
        {...hostEvents}
        title={detail}
        style={{
          // Высота хит-зоны = верхний padding карточки (mobile 8 / desktop 7), чтобы
          // зона hover/tap полоски не залезала на первую строку поля ввода
          position: 'absolute', top: 0, left: 0, right: 0, height: isMobile ? 8 : 7,
          zIndex: 3, cursor: reached ? 'default' : 'pointer',
        }}
      >
        {/* Маска повторяет скругление верхних углов карточки — полоска садится по кромке */}
        <div style={{
          position: 'absolute', top: 0, left: 0, right: 0, height: R.xxl,
          borderTopLeftRadius: R.xxl, borderTopRightRadius: R.xxl,
          overflow: 'hidden', pointerEvents: 'none',
        }}>
          <div style={{ height: 3, width: '100%', background: c.fill }} />
        </div>
        {open && (
          <div style={{
            position: 'absolute', bottom: 'calc(100% + 8px)', left: isMobile ? 6 : 10, zIndex: Z.dropdown,
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
            boxShadow: SHADOW.dropdown, padding: '8px 11px', width: 'max-content',
            maxWidth: 'min(300px, calc(100vw - 24px))',
          }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 7, fontFamily: FONT.sans, fontSize: 12.5, color: c.text, lineHeight: 1.35 }}>
              <span style={{ flexShrink: 0, display: 'flex', color: c.text }}>
                {reached
                  ? <Ban size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  : <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              </span>
              <span>{detail}</span>
            </div>
            {/* Стрелка вниз к полоске */}
            <span style={{
              position: 'absolute', top: '100%', left: isMobile ? 14 : 18, width: 10, height: 10,
              marginTop: -5, background: C.bgWhite,
              borderRight: `1px solid ${C.border}`, borderBottom: `1px solid ${C.border}`,
              transform: 'rotate(45deg)',
            }} />
          </div>
        )}
      </div>
    </>
  );
}

export function Composer({
  sessionId,
  onSend,
  onStop,
  onAttach,
  isGenerating,
  mode,
  onModeChange,
  planAvailable = true,
  attachments,
  onRemoveAttachment,
  onAttachFiles,
  isMobile,
  offline,
  skills = [],
  personas = [],
  agents = [],
  selectedPersona = null,
  selectedAgentName = null,
  onCompanionChange,
  canPickCompanion,
  model,
  onModelChange,
  chatStarted,
  effort,
  onEffortChange,
  participantIds = null,
  onCreateGroup,
  workLoop = null,
  onToggleWorkLoop,
  teamImplement = null,
  onToggleTeamImplementAuto,
  onDisableTeamImplement,
  onStopTeamImplement,
  onEnableTeamImplement,
  isProjectChat = false,
  onboarding = false,
  worktreeBranch = null,
  onToggleWorktree,
  chatContext,
  promptSuggestion = null,
  rateWindow,
  restore = null,
  onReplaceAttachments,
  focusSignal,
}: ComposerProps) {
  const asstName = useAssistantName();
  // Черновик per-session. Composer смонтирован с key={sessionId} (см. ChatPanel), поэтому
  // смена чата = полное перемонтирование, и text заново инициализируется из getDraft(sessionId).
  // Здесь — только write-through: сохраняем набранный текст в стор черновиков этого чата,
  // чтобы он пережил переключение и возвращение.
  const [text, setText] = useState(() => getDraft(sessionId));
  useEffect(() => {
    setDraft(sessionId, text);
  }, [sessionId, text]);
  // Преднастройка из раздела «Заметки»: «Спросить Claude про это» кладёт контекст
  // заметки в sessionStorage — забираем при появлении композера и по событию
  // (на случай, если чат уже открыт и композер смонтирован).
  useEffect(() => {
    const consume = () => {
      const pending = sessionStorage.getItem('cc_pending_chat_prompt');
      if (pending) { sessionStorage.removeItem('cc_pending_chat_prompt'); setText(prev => prev ? prev : pending); }
    };
    consume();
    window.addEventListener('cc-compose-prefill', consume);
    return () => window.removeEventListener('cc-compose-prefill', consume);
  }, []);
  // Возврат прерванного сообщения по «Стоп» (фича «честная очередь», событие composer_restore).
  // Только в ПУСТОЕ поле: набранный черновик пользователя важнее серверного restore.
  // Гейт читает сохранённый черновик ЭТОГО чата — тот же источник, что и гейт режима
  // в ChatPanel: живое значение инпута равно ему здесь всегда (эффект синхронизации
  // черновика выше объявлен раньше и пишет его в том же коммите до этого эффекта),
  // поэтому режим и текст не могут разойтись. Режим здесь НЕ восстанавливаем —
  // restore-mode целиком ставит ChatPanel (единый владелец, спора за setMode нет).
  // text=null — прерван авто/агентский ход, восстанавливать нечего: композер не трогаем.
  // Защиты от повторного применения здесь нет и не нужно: команда разовая — ChatPanel гасит
  // её в сторе сразу после того, как её отработали оба владельца (см. consumeComposerRestore),
  // поэтому второй раз тот же restore до этого эффекта просто не доходит. Прежние ref-гарды
  // (applied-seq со сбросом при смене чата) как раз и воскрешали текст: seq — per-session
  // счётчик, сброс на переключении чата снимал фильтр с уже применённой команды.
  useEffect(() => {
    const r = restore;
    if (!r || r.seq === 0) return;
    if (getDraft(sessionId).trim()) return;          // черновик важнее
    if (r.text == null) return;                      // нечего восстанавливать
    // eslint-disable-next-line react-hooks/set-state-in-effect -- восстановление прерванного сообщения из события composer_restore
    setText(r.text);
    if (r.attachedPaths && r.attachedPaths.length > 0 && onReplaceAttachments) {
      onReplaceAttachments(r.attachedPaths);
    }
    textareaRef.current?.focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [restore?.seq, sessionId]);

  // Внешний запрос фокуса (колонка «Стены» стала активной). Ждём кадр: композер
  // в этот момент ещё проявляется, а фокус на скрытом поле браузер игнорирует.
  useEffect(() => {
    if (!focusSignal) return;
    const id = requestAnimationFrame(() => textareaRef.current?.focus());
    return () => cancelAnimationFrame(id);
  }, [focusSignal]);

  const [modeMenuOpen, setModeMenuOpen] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  // Опасный режим (bypass) ждёт подтверждения в модалке перед применением
  const [pendingMode, setPendingMode] = useState<Mode | null>(null);
  // Штаб «Командной реализации» думает (Э8): стадии интервью/планирования держат чат
  // в план-режиме, селектор показывает «план» и заблокирован — независимо от того,
  // что сейчас лежит в mode (после разблокировки значение снова приходит с бэкенда)
  const modeLocked = teamImplement ? teamImplementModeLocked(teamImplement) : false;
  const displayMode: Mode = modeLocked ? 'plan' : mode;
  // Пояснение залоченного селектора: десктоп — статичный каллаут в потоке (Майя ловила
  // перекрытие композера всплывающим пузырём), мобила — нижняя шторка (hover недоступен)
  const [lockInfoOpen, setLockInfoOpen] = useState(false);
  // Autocomplete скиллов
  const [showSkillsDropdown, setShowSkillsDropdown] = useState(false);
  const [skillQuery, setSkillQuery] = useState('');
  const skillWordStartRef = useRef(0);
  // Autocomplete @упоминаний персон — включён всегда; isGroupChat нужен для ранжирования участников
  const isGroupChat = (participantIds?.length ?? 0) > 1;
  const mentionsActive = true;
  const [showMentions, setShowMentions] = useState(false);
  const [mentionQuery, setMentionQuery] = useState('');
  const mentionWordStartRef = useRef(0);
  // Кого можно упомянуть: персоны контекста, кроме персоны самого чата;
  // в групповом чате участники группы идут первыми
  const mentionable = (() => {
    if (!mentionsActive) return [];
    const base = personas.filter(p => p.id !== selectedPersona?.id);
    if (!isGroupChat) return base;
    const rank = (p: Persona) => participantIds!.includes(p.id) ? 0 : 1;
    return [...base].sort((a, b) => rank(a) - rank(b));
  })();
  // Раскрывашка «Обсудить с командой»: выбранная механика + её настройки живут здесь
  // (TeamDrawer — контролируемый компонент), тема пишется в само поле композера
  const [teamOpen, setTeamOpen] = useState(false);
  const [teamMech, setTeamMech] = useState<TeamMechanicId | null>(null);
  const [teamSettings, setTeamSettings] = useState<TeamMechanicSettings>(DEFAULT_TEAM_SETTINGS);
  const canDiscuss = !!sessionId && !onboarding;
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const modeRef = useRef<HTMLDivElement>(null);
  // Замеры полосы контролов: по ним решается, сколько кнопок влезает в одну строку
  const stripRef = useRef<HTMLDivElement>(null);
  const fixedLeftRef = useRef<HTMLDivElement>(null);
  const badgesRef = useRef<HTMLDivElement>(null);
  const rightRef = useRef<HTMLDivElement>(null);

  // Ширина САМОЙ полосы, а не окна. Ступени раскладки губы раньше считались от isMobile
  // (ширина окна ≤600), и на планшете, телефоне в ландшафте и в сплите получалось так:
  // окно широкое → «десктопный» режим, а губа рядом со списком чатов узкая → пикеры
  // разворачивались в полные подписи, кнопки не сворачивались, и строка вылезала за край.
  // 0 — ещё не померили: до первого замера считаем полосу просторной, чтобы компактная
  // форма не мигала на первом кадре. offline в зависимостях — в офлайне полосы нет в DOM,
  // и наблюдателя надо переподписать на вернувшийся узел.
  const [stripWidth, setStripWidth] = useState(0);
  useEffect(() => {
    const el = stripRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const read = () => setStripWidth(el.clientWidth);
    const ro = new ResizeObserver(read);
    ro.observe(el);
    read();
    return () => ro.disconnect();
  }, [offline]);
  // Пикеры справа схлопнуты в иконки; подпись режима убрана
  const compactStrip = !!isMobile || (stripWidth > 0 && stripWidth < STRIP_COMPACT);
  // Собеседнику можно длинную подпись (иначе она режется по 200px)
  const widePickers = !compactStrip && (stripWidth === 0 || stripWidth >= STRIP_WIDE);

  // Голосовой ввод целиком в хуке: распознанное дописываем к тексту, а при мёртвом
  // движке просто ставим фокус — диктовать будет системный ввод клавиатуры
  const { hasSpeech, isListening, recSeconds, startMic, stopMic } = useVoiceInput({
    onResult: chunk => setText(prev => (prev ? prev + ' ' + chunk : chunk)),
    onKeyboardFallback: () => textareaRef.current?.focus(),
  });

  // Автозапуск «Обсудить с командой» — чат открыт через «Созвать команду» из центра
  // команды: раскрываем панель механик
  useEffect(() => {
    if (!sessionId || !canDiscuss) return;
    if (sessionStorage.getItem('cc_auto_discuss') === sessionId) {
      sessionStorage.removeItem('cc_auto_discuss');
      // eslint-disable-next-line react-hooks/set-state-in-effect -- одноразовое открытие «Команды» по флагу из sessionStorage
      setTeamOpen(true);
    }
  }, [sessionId, canDiscuss]);

  // Закрытие меню режимов (и каллаута лока, Э8) по клику вне него
  useEffect(() => {
    if (!modeMenuOpen && !lockInfoOpen) return;
    const onDown = (e: MouseEvent) => {
      if (modeRef.current && !modeRef.current.contains(e.target as Node)) {
        setModeMenuOpen(false);
        setLockInfoOpen(false);
      }
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [modeMenuOpen, lockInfoOpen]);

  // Низ мобильного меню режимов — по позиции кнопки (fixed во всю ширину чуть выше неё).
  // Меряем в layout-эффекте: читать ref в рендере нельзя, а число без изменений
  // не триггерит ререндер (setState с тем же значением выходит сразу).
  const [modeMenuBottom, setModeMenuBottom] = useState(80);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- меряем каждый рендер, пока меню открыто: позиция кнопки плавает от высоты композера
  useLayoutEffect(() => {
    if (!modeMenuOpen || !isMobile) return;
    const r = modeRef.current?.getBoundingClientRect();
    setModeMenuBottom(r ? window.innerHeight - r.top + 6 : 80);
  });

  const hasText = text.trim().length > 0;

  // Обновление состояния autocomplete при каждом изменении текста
  const updateSkillDropdown = useCallback((newText: string, cursorPos: number) => {
    // Ищем слово под курсором: от курсора назад до пробела/переноса
    let wordStart = cursorPos - 1;
    while (wordStart >= 0 && newText[wordStart] !== ' ' && newText[wordStart] !== '\n') wordStart--;
    wordStart++;
    const word = newText.slice(wordStart, cursorPos);
    if (skills.length > 0 && word.startsWith('/')) {
      skillWordStartRef.current = wordStart;
      setSkillQuery(word.slice(1));
      setShowSkillsDropdown(true);
    } else {
      setShowSkillsDropdown(false);
    }
    // @упоминание персоны — тот же принцип, что и /скилл
    if (mentionable.length > 0 && word.startsWith('@')) {
      mentionWordStartRef.current = wordStart;
      setMentionQuery(word.slice(1));
      setShowMentions(true);
    } else {
      setShowMentions(false);
    }
  }, [skills.length, mentionable.length]);

  const handleMentionSelect = useCallback((p: Persona) => {
    const wordStart = mentionWordStartRef.current;
    const before = text.slice(0, wordStart);
    const after = text.slice(wordStart + 1 + mentionQuery.length); // +1 за @
    const inserted = '@' + p.handle + ' ';
    const newText = before + inserted + after.trimStart();
    setText(newText);
    setShowMentions(false);
    setTimeout(() => {
      const el = textareaRef.current;
      if (el) {
        const pos = (before + inserted).length;
        el.focus();
        el.setSelectionRange(pos, pos);
      }
    }, 0);
  }, [text, mentionQuery]);

  const handleSkillSelect = useCallback((skill: SkillInfo) => {
    const wordStart = skillWordStartRef.current;
    const before = text.slice(0, wordStart);
    const after = text.slice(wordStart + 1 + skillQuery.length); // +1 за /
    const inserted = '/' + skill.name + (skill.argumentHint ? ' ' : ' ');
    const newText = before + inserted + after.trimStart();
    setText(newText);
    setShowSkillsDropdown(false);
    setTimeout(() => {
      const el = textareaRef.current;
      if (el) {
        const pos = (before + inserted).length;
        el.focus();
        el.setSelectionRange(pos, pos);
      }
    }, 0);
  }, [text, skillQuery]);

  const handleSlashButton = useCallback(() => {
    const el = textareaRef.current;
    const pos = el ? (el.selectionStart ?? text.length) : text.length;
    const before = text.slice(0, pos);
    const after = text.slice(pos);
    const needSpace = before.length > 0 && before[before.length - 1] !== ' ' && before[before.length - 1] !== '\n';
    const inserted = (needSpace ? ' ' : '') + '/';
    const newText = before + inserted + after;
    setText(newText);
    const newPos = pos + inserted.length;
    updateSkillDropdown(newText, newPos);
    setTimeout(() => {
      if (el) { el.focus(); el.setSelectionRange(newPos, newPos); }
    }, 0);
  }, [text, updateSkillDropdown]);

  // Авторазмер textarea под содержимое (до 200px, дальше — скролл внутри поля).
  // Поле всегда занимает свою строку во всю ширину композера, поэтому подгонять
  // раскладку под длину текста больше не нужно.
  const autoResize = useCallback(() => {
    const el = textareaRef.current;
    if (!el) return;
    // Прямая DOM-мутация осознанно: высота поля не должна гонять ререндер на каждый ввод
    // eslint-disable-next-line react-hooks/immutability -- стиль DOM-узла из эффекта, не рендер-данные
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 200) + 'px';
  }, []);

  useEffect(() => {
    autoResize();
  }, [text, autoResize]);

  const resetInput = () => {
    setText('');
    setDraft(sessionId, '');
    if (textareaRef.current) {
      // eslint-disable-next-line react-hooks/immutability -- сброс высоты DOM-узла из обработчика отправки
      textareaRef.current.style.height = '34px';
    }
  };

  const handleSend = async () => {
    const t = text.trim();

    // Режим «Командная реализация»: обвязки нет — включаем режим на сессии и отправляем
    // тему обычным сообщением, дальше чат работает штабом (планирование → волны → проверка)
    if (teamMech === 'implementMode') {
      if (!t) { setTeamOpen(true); return; }
      // Вне проекта команды нет — состав обязателен (подсказка в зоне настроек)
      if (!isProjectChat && teamSettings.participants.length === 0) { setTeamOpen(true); return; }
      // Режим уже включён — сообщение уходит как новая вводная, не пересобирая состояние
      if (!teamImplement && onEnableTeamImplement) {
        try {
          await onEnableTeamImplement({
            autoWaves: teamSettings.modeAutoWaves,
            executorPersonaIds: teamSettings.participants.map(p => p.id),
          });
        } catch {
          // Включение не удалось (причину уже показал тост) — вводную НЕ отправляем
          // обычным сообщением (M11): текст остаётся в поле, механика не сбрасывается,
          // человек может повторить или разобраться с причиной отказа
          return;
        }
      }
      setLastMechanic(sessionId, 'implementMode');
      onSend(t, attachments);
      setTeamMech(null);
      setTeamOpen(false);
      setTeamSettings(DEFAULT_TEAM_SETTINGS);
      resetInput();
      return;
    }

    // Командный ход: текст поля — тема, обвязка собирается buildTeamTurnText
    if (teamMech) {
      // Валидация: тема обязательна везде, кроме QA-цикла и ревью/красной команды
      // (они работают по текущему диффу/контексту); дискуссии и командной реализации
      // нужен хотя бы один участник (подсказка — в зоне настроек)
      const topicOptional = teamMech === 'qa' || teamMech === 'review' || teamMech === 'redteam';
      if (!t && !topicOptional) { setTeamOpen(true); return; }
      if ((teamMech === 'discuss' || teamMech === 'implement') && teamSettings.participants.length === 0) { setTeamOpen(true); return; }
      // Автопилот работает только через цикл «до готово»: включаем work-loop ДО отправки
      // (PUT /chats/{id}/loop), только если он ещё не активен. Включение может не удаться
      // (чат занят, 4xx/5xx, обрыв) — тогда ход НЕ отправляем (как с «Командной реализацией»
      // выше): иначе человек думает, что автопилот работает, а ушёл один обычный ход
      if (teamMech === 'autopilot' && !workLoop?.active && onToggleWorkLoop) {
        try {
          await onToggleWorkLoop();
        } catch {
          return;
        }
      }
      setLastMechanic(sessionId, teamMech);
      onSend(buildTeamTurnText(teamMech, t, teamSettings, chatContext), [], { auto: true });
      setTeamMech(null);
      setTeamOpen(false);
      setTeamSettings(DEFAULT_TEAM_SETTINGS);
      resetInput();
      return;
    }

    if (!t && attachments.length === 0) return;
    onSend(t, attachments);
    resetInput();
  };

  // Вставка файла из буфера (скриншот, документ) → отдаём File-объекты родителю на загрузку.
  // Копирование обычного текста тоже кладёт записи в items, поэтому берём только kind==='file'
  // и не гасим событие, если файлов нет — иначе сломалась бы вставка текста
  const handlePaste = (e: React.ClipboardEvent) => {
    if (!onAttachFiles) return;
    const files: File[] = [];
    for (const item of Array.from(e.clipboardData.items)) {
      if (item.kind === 'file') {
        const f = item.getAsFile();
        if (f) files.push(f);
      }
    }
    if (files.length) { e.preventDefault(); onAttachFiles(files); }
  };

  const handleDrop = (e: React.DragEvent) => {
    if (!onAttachFiles) return;
    const files = Array.from(e.dataTransfer.files);
    setDragOver(false);
    if (files.length) { e.preventDefault(); onAttachFiles(files); }
  };

  const handleDragOver = (e: React.DragEvent) => {
    if (!onAttachFiles) return;
    if (Array.from(e.dataTransfer.types).includes('Files')) { e.preventDefault(); setDragOver(true); }
  };

  // Подсказка следующего сообщения: дисмисс крестиком живёт до прихода новой подсказки
  const [suggestionDismissed, setSuggestionDismissed] = useState(false);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс «скрыто» при приходе новой подсказки
  useEffect(() => { setSuggestionDismissed(false); }, [promptSuggestion]);
  const suggestionVisible = !!promptSuggestion && text.trim() === '' && !suggestionDismissed && !isGenerating && !isListening;
  const acceptSuggestion = useCallback(() => {
    if (!promptSuggestion) return;
    setText(promptSuggestion);
    setTimeout(() => {
      const el = textareaRef.current;
      if (el) { el.focus(); el.setSelectionRange(promptSuggestion.length, promptSuggestion.length); }
    }, 0);
  }, [promptSuggestion]);

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // Принятие подсказки: → или Tab при пустом поле. Tab при открытых дропдаунах @ и /
    // до textarea не доходит — их capture-листенеры на document перехватывают раньше
    if (suggestionVisible && (e.key === 'ArrowRight' || e.key === 'Tab')) {
      e.preventDefault();
      acceptSuggestion();
      return;
    }
    // Esc — скрыть подсказку до прихода следующей
    if (suggestionVisible && e.key === 'Escape') {
      e.preventDefault();
      setSuggestionDismissed(true);
      return;
    }
    // На мобиле Enter переносит строку, отправка — только кнопкой (десктоп: Enter отправляет)
    if (e.key === 'Enter' && !e.shiftKey && !isMobile) {
      e.preventDefault();
      void handleSend();
    }
  };

  // Стили контейнера — поле всегда активно (доступно для ввода и во время генерации)
  const containerStyle: React.CSSProperties = {
    position: 'relative',
    background: C.bgWhite,
    border: `1px solid ${dragOver || hasText ? C.accent : C.border}`,
    borderRadius: R.xxl,
    padding: isMobile ? '8px 10px' : '7px 8px',
    // Подъём как у островов, но разлётом ВВЕРХ (SHADOW.lift): композер стоит на
    // самой кромке холста — его губа выровнена по низу соседних островов, и
    // нижнюю половину обычной тени срезал бы край
    boxShadow: dragOver ? SHADOW.focus : SHADOW.lift,
    display: 'flex',
    flexDirection: 'column',
    gap: 0,
    transition: 'border-color 0.15s, box-shadow 0.15s, background 0.15s',
  };

  // Анимация трёх точек
  const dotsStyle: React.CSSProperties = {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    flex: 1,
    minHeight: 34,
    padding: '0 4px',
  };

  // --- Контролы (переиспользуются в обеих раскладках) ---

  const attachButton = (
    <button
      onClick={onAttach}
      title="Прикрепить файл"
      style={{
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none', background: 'none',
        cursor: 'pointer', color: C.textMuted, display: 'flex', alignItems: 'center',
        justifyContent: 'center', flexShrink: 0,
      }}
    >
      <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  );

  const slashButton = skills.length > 0 ? (
    <button
      onClick={handleSlashButton}
      title="Выбрать скилл"
      style={{
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none', background: 'none',
        cursor: 'pointer', color: C.textMuted, display: 'flex', alignItems: 'center',
        justifyContent: 'center', flexShrink: 0,
        fontFamily: FONT.mono, fontSize: 16, fontWeight: 600, lineHeight: 1,
        paddingBottom: 1,
      }}
    >
      /
    </button>
  ) : null;

  // Кнопка «Обсудить с командой» — тоггл раскрывашки механик. Пока механика выбрана,
  // кнопка «переехала» в пилюлю состояния (teamPill) и из ряда убирается
  const discussButton = canDiscuss && !teamMech ? (
    <button
      onClick={() => setTeamOpen(o => !o)}
      title="Обсудить с командой"
      style={{
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none',
        background: teamOpen ? C.accentLight : 'none',
        cursor: 'pointer', color: teamOpen ? C.accent : C.textMuted,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        transition: 'color 0.15s, background 0.15s',
      }}
    >
      <Users size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  ) : null;

  // Пилюля выбранной командной механики — склейка кнопки «Обсудить с командой» с её
  // чипом: сегмент-иконка открывает раскрывашку настроек (бывшая кнопка), ✕ снимает
  // режим (бывший крестик чипа) — оба действия как раньше, просто в одном контуре
  const teamMechMeta = teamMech ? teamMechanic(teamMech) : null;
  const TeamMechIcon = teamMechMeta?.icon;
  const teamPill = teamMechMeta && TeamMechIcon ? (
    <ModePill
      isMobile={isMobile}
      icon={<TeamMechIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      leadTitle={`Командная механика «${teamMechMeta.name}» — настройки`}
      onLeadClick={() => setTeamOpen(o => !o)}
      valueTitle={`Активна механика «${teamMechMeta.name}». Иконка — настройки, ✕ — снять режим`}
      // Короткое имя из словаря механик (teamMechanics.ts) — полное вываливалось бы за
      // границы пилюли на узкой ширине; расшифровка — в leadTitle/valueTitle выше
      value={teamMechMeta.shortName}
      trailing={
        <button
          onClick={() => setTeamMech(null)}
          title="Отменить режим"
          style={{
            border: 'none', background: 'none', color: C.accent, cursor: 'pointer',
            width: 18, height: 18, borderRadius: R.full, padding: 0, flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}
        >
          <X size={12} strokeWidth={ICON_STROKE} />
        </button>
      }
    />
  ) : null;

  // Цикл «до готово»: выключен — круглая кнопка в ряду, включён — пилюля в группе
  // состояния, чей сегмент-иконка и есть кнопка (клик останавливает цикл, без confirm).
  // При провале onToggleWorkLoop сам показывает тост и бросает (для guard'а в handleSend) —
  // тумблерные кнопки ход не отправляют, им остаётся только не уронить необработанный reject
  const loopActive = !!workLoop?.active;
  const toggleWorkLoopSafe = onToggleWorkLoop
    ? () => { void Promise.resolve(onToggleWorkLoop()).catch(() => {}); }
    : undefined;
  const loopButton = onToggleWorkLoop && !loopActive ? (
    <button
      onClick={toggleWorkLoopSafe}
      title="Цикл «до готово»: агент работает итерациями, пока не отчитается о завершении, затем верификационный ход"
      style={{
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none',
        background: 'none',
        cursor: 'pointer', color: C.textMuted,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        transition: 'color 0.15s, background 0.15s',
      }}
    >
      <RefreshCw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  ) : null;
  const loopPill = onToggleWorkLoop && loopActive && workLoop ? (
    <ModePill
      isMobile={isMobile}
      icon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      leadTitle="Остановить цикл «до готово»"
      onLeadClick={toggleWorkLoopSafe}
      valueTitle={workLoop.phase === 'verifying'
        ? 'Цикл «до готово»: верификационный ход'
        : `Цикл «до готово»: итерация ${workLoop.iteration} из ${workLoop.maxIterations}`}
      value={workLoop.phase === 'verifying'
        ? (isMobile ? 'Проверка' : 'Цикл: верификация')
        : (isMobile ? `${workLoop.iteration}/${workLoop.maxIterations}` : `Цикл: итерация ${workLoop.iteration}/${workLoop.maxIterations}`)}
    />
  ) : null;

  // Режим «Командная реализация»: бейдж стадии + чип «Авто»
  const teamImplementBadge = teamImplement && onToggleTeamImplementAuto && onDisableTeamImplement ? (
    <TeamImplementBadge
      state={teamImplement}
      chatMode={mode}
      isMobile={isMobile}
      onToggleAuto={onToggleTeamImplementAuto}
      onDisable={onDisableTeamImplement}
      onStop={onStopTeamImplement}
    />
  ) : null;

  // Отдельное git worktree чата — круглая кнопка-тумблер в ряду: активное состояние
  // показывает только заливка accent (как у discussButton), БЕЗ имени ветки рядом.
  // Это осознанное ИСКЛЮЧЕНИЕ из склеенной грамматики режимов (цикл и команда таскают
  // значение в пилюле состояния): у дерева значение живёт в git-баре над композером
  // (ProjectGitBar — там же дифф и «Опубликовать»), дублировать его в полосе не нужно.
  // Дерево ХОДА (turnWorktree) здесь тоже не показываем — оно в том же баре.
  // Не «унифицировать» склейку дерева обратно!
  const worktreeActive = !!worktreeBranch;
  // Гейт безопасности: пока идёт ход, дерево чата переключать нельзя — процесс хода
  // работает в нём прямо сейчас
  const worktreeToggleDisabled = isGenerating;
  const worktreeButtonTitle = worktreeToggleDisabled
    ? 'Пока идёт ход, дерево чата переключать нельзя'
    : worktreeActive
      ? `Чат работает в отдельном дереве (ветка ${worktreeBranch}) — нажми, чтобы вернуть в проект`
      : 'Отдельное дерево: чат работает в изолированном git worktree на своей ветке';
  const worktreeButton = onToggleWorktree ? (
    <button
      onClick={worktreeToggleDisabled ? undefined : onToggleWorktree}
      disabled={worktreeToggleDisabled}
      title={worktreeButtonTitle}
      style={{
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none',
        background: worktreeActive ? C.accentLight : 'none',
        cursor: worktreeToggleDisabled ? 'default' : 'pointer',
        color: worktreeActive ? C.accent : C.textMuted,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        opacity: worktreeToggleDisabled ? 0.4 : 1,
        transition: 'color 0.15s, background 0.15s, opacity 0.15s',
      }}
    >
      <FolderGit2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  ) : null;

  const inputArea = isListening ? (
    <div style={{ ...dotsStyle, gap: 10 }}>
      <span style={{ width: 9, height: 9, borderRadius: '50%', background: C.danger, animation: 'pulsedot 1s ease-in-out infinite', flexShrink: 0 }} />
      <span style={{ fontSize: 13, color: C.dangerText, fontWeight: 600, fontFamily: FONT.mono, flexShrink: 0, minWidth: 34 }}>{fmtRecTime(recSeconds)}</span>
      <Waveform />
    </div>
  ) : (
    // Обёртка нужна ghost-слою подсказки: он позиционируется поверх ПУСТОГО textarea
    // (подсказка видна только при пустом поле, совмещать с текстом юзера не нужно)
    <div style={{ position: 'relative', flex: 1, minWidth: 0, width: isMobile ? '100%' : undefined, display: 'flex' }}>
      <textarea
        ref={textareaRef}
        className="cc-composer-input"
        value={text}
        onChange={(e) => {
          setText(e.target.value);
          updateSkillDropdown(e.target.value, e.target.selectionStart ?? e.target.value.length);
        }}
        onKeyDown={handleKeyDown}
        onInput={autoResize}
        onPaste={handlePaste}
        // Пока видна ghost-подсказка, обычный плейсхолдер прячем — тексты бы наложились
        placeholder={suggestionVisible ? '' : teamMechMeta ? teamMechMeta.placeholder : `Спросите ${asstName}…`}
        rows={1}
        style={{
          flex: 1,
          width: '100%',
          border: 'none',
          outline: 'none',
          resize: 'none',
          fontSize: isMobile ? 16 : 15, // 16px — чтобы iOS не зумил при фокусе
          color: C.textPrimary,
          background: 'transparent',
          minHeight: 34,
          maxHeight: 200,
          lineHeight: '1.5',
          padding: isMobile ? '6px 8px' : '6px 4px',
          fontFamily: 'inherit',
          overflowY: 'auto',
          boxSizing: 'border-box',
        }}
      />
      {suggestionVisible && promptSuggestion && (
        // Ghost text как в Claude Code Desktop: серый текст подсказки в самом поле
        // + бейдж-клавиша ⇥ (тап — принять; на десктопе также → / Tab).
        // pointerEvents:none у слоя — тап по полю ставит фокус как обычно
        <div style={{
          position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', gap: 8,
          padding: isMobile ? '0 8px' : '0 4px', pointerEvents: 'none', boxSizing: 'border-box',
          fontSize: isMobile ? 16 : 15, lineHeight: '1.5', color: C.textMuted, minWidth: 0,
        }}>
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
            {promptSuggestion}
          </span>
          <button
            onClick={acceptSuggestion}
            title="Вставить подсказку (→ или Tab)"
            style={{
              pointerEvents: 'auto', flexShrink: 0, cursor: 'pointer',
              border: `1px solid ${C.border}`, borderRadius: R.sm, background: 'transparent',
              color: C.textMuted, fontSize: 11, fontWeight: 600, lineHeight: 1,
              padding: '3px 7px', fontFamily: 'inherit',
            }}
          >
            ⇥
          </button>
        </div>
      )}
    </div>
  );

  const modeButton = (
    <div ref={modeRef} style={{ position: 'relative', flexShrink: 0 }}>
      <button
        onClick={() => {
          // Штаб планирует (Э8) — селектор не открывается, клик/тап раскрывает пояснение:
          // на десктопе — статичный каллаут в потоке, на мобиле — нижняя шторка (Modal)
          if (modeLocked) { setLockInfoOpen(o => !o); return; }
          setModeMenuOpen(o => !o);
        }}
        // В сжатом виде подпись скрыта — значение уносим в тултип, как у модели и усилия
        title={modeLocked ? TEAM_IMPLEMENT_MODE_LOCKED_TOOLTIP : `Режим работы: ${MODE_META[displayMode].label}`}
        // Фон только на наведении/открытии: полоса лежит на тени карточки композера,
        // и залитые плашки разрезали бы её пятнами
        onMouseEnter={e => { if (!modeMenuOpen && !modeLocked) e.currentTarget.style.background = C.accentLight; }}
        onMouseLeave={e => { if (!modeMenuOpen && !modeLocked) e.currentTarget.style.background = 'transparent'; }}
        style={{
          // Сжатый вид — иконка + шеврон без подписи. Высота остаётся тач-размером
          // мобилы (36) только на мобиле: на планшете полоса десктопная, сжата лишь ширина
          ...(compactStrip
            ? { height: isMobile ? 36 : 28, padding: '0 6px', justifyContent: 'center', gap: 3 }
            : { height: 28, padding: '0 10px' }),
          borderRadius: R.md, border: 'none',
          background: modeMenuOpen ? C.bgSelected : 'transparent',
          color: modeLocked ? C.textMuted : displayMode === 'bypass' ? C.danger : C.textSecondary,
          fontSize: 12.5, fontWeight: 600, cursor: modeLocked ? 'default' : 'pointer', whiteSpace: 'nowrap',
          display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
          transition: 'background 0.15s',
        }}
      >
        <ModeIcon mode={displayMode} />
        {/* В сжатом виде прячем только подпись (длинные названия распирают строку) —
            шеврон остаётся, как у модели, усилия и собеседника. Название — в тултипе. */}
        {!compactStrip && MODE_META[displayMode].label}
        {modeLocked ? (
          <Lock size={10} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, opacity: 0.6 }} />
        ) : (
          <ChevronDown size={compactStrip ? 10 : ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ flexShrink: 0, opacity: 0.55, transform: modeMenuOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
        )}
      </button>
      {modeMenuOpen && !modeLocked && (
        <div style={{
          // Десктоп: absolute от кнопки (вправо). Мобил: fixed во всю ширину (left/right 16px),
          // bottom — чуть выше кнопки по getBoundingClientRect, чтобы меню не уезжало за край
          // экрана, когда кнопка сместилась из-за переноса строк.
          ...(isMobile
            ? { position: 'fixed' as const, left: 16, right: 16, bottom: modeMenuBottom }
            : { position: 'absolute' as const, bottom: 'calc(100% + 6px)', left: 0, minWidth: 248 }),
          maxWidth: 'calc(100vw - 32px)',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, padding: 5, zIndex: Z.dropdown,
        }}>
          {MODES.filter(m => m !== 'plan' || planAvailable).map(m => {
            const active = m === mode;
            const danger = MODE_META[m].danger;
            return (
              <button key={m} onClick={() => { setModeMenuOpen(false); if (isDangerMode(m) && m !== mode) setPendingMode(m); else onModeChange(m); }}
                style={{
                  width: '100%', display: 'flex', alignItems: 'flex-start', gap: 9,
                  padding: isMobile ? '11px 11px' : '8px 9px',
                  borderRadius: R.md, border: 'none', background: active ? C.accentLight : 'transparent',
                  cursor: 'pointer', textAlign: 'left',
                }}
                onMouseEnter={e => { if (!active) e.currentTarget.style.background = C.accentLight; }}
                onMouseLeave={e => { if (!active) e.currentTarget.style.background = 'transparent'; }}
              >
                <span style={{ color: danger ? C.danger : active ? C.accent : C.textMuted, display: 'flex', marginTop: 1, flexShrink: 0 }}><ModeIcon mode={m} /></span>
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 13, fontWeight: 600, color: danger ? C.danger : C.textHeading }}>
                    <span>{MODE_META[m].label}</span>
                    {danger && <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                  </span>
                  <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1, lineHeight: 1.35 }}>{MODE_META[m].desc}</span>
                </span>
                {active && (
                  <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.accent} style={{ flexShrink: 0, marginTop: 2 }} />
                )}
              </button>
            );
          })}
        </div>
      )}
      {/* Пояснение лока (Э8), мобила — нижняя шторка: hover недоступен на тач, поэтому
          тап открывает Modal вместо статичного каллаута десктопа */}
      {lockInfoOpen && isMobile && (
        <Modal title="Штаб планирует" onClose={() => setLockInfoOpen(false)} width={MODAL_W.confirm}>
          <p style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5, margin: 0 }}>
            {TEAM_IMPLEMENT_MODE_LOCKED_TOOLTIP}
          </p>
        </Modal>
      )}
    </div>
  );

  // Пояснение лока (Э8), десктоп: статичный каллаут В ПОТОКЕ (не всплывающий пузырь) —
  // рендерится отдельной строкой под полосой контролов, а не поповером у кнопки, чтобы
  // не перекрывать поле ввода композера (Майя ловила это на макете)
  const lockInfoCallout = lockInfoOpen && (
    <div style={{
      marginTop: 6, padding: '8px 10px', borderRadius: R.lg,
      background: C.bgSelected, border: `1px solid ${C.border}`,
      display: 'flex', alignItems: 'flex-start', gap: 7,
    }}>
      <Lock size={12} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0, marginTop: 2 }} />
      <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.4 }}>
        {TEAM_IMPLEMENT_MODE_LOCKED_TOOLTIP}
      </span>
      <button
        onClick={() => setLockInfoOpen(false)}
        aria-label="Закрыть"
        style={{ flexShrink: 0, background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 2, display: 'flex' }}
      >
        <X size={12} strokeWidth={ICON_STROKE} />
      </button>
    </div>
  );

  // Гасим нативный touch-callout / контекстное меню на иконочных кнопках.
  // На планшете long-press по SVG-иконке внутри кнопки иначе вызывает меню
  // браузера «Скачать/Поделиться/Печать» и перебивает onClick (голосовой ввод
  // не стартует). Подавляем callout и выделение; onContextMenu гасит и правый клик.
  const iconBtnGuard: CSSProperties = {
    WebkitTouchCallout: 'none',
    WebkitUserSelect: 'none',
    userSelect: 'none',
    touchAction: 'manipulation',
  };

  const micButton = hasSpeech ? (
    <button
      type="button"
      onClick={startMic}
      onContextMenu={(e) => e.preventDefault()}
      title="Голосовой ввод"
      style={{
        ...iconBtnGuard,
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none',
        background: 'none', cursor: 'pointer', color: C.textMuted,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        transition: 'color 0.15s, background 0.15s',
      }}
    >
      <Mic size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  ) : null;

  // Во время записи mic+send заменяются на отмену (✕) и подтверждение (✓)
  const cancelRecBtn = (
    <button type="button" onClick={() => stopMic(false)} onContextMenu={(e) => e.preventDefault()} title="Отменить запись"
      style={{ ...iconBtnGuard, width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none', background: C.dangerBg, color: C.danger, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
      <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  );
  const confirmRecBtn = (
    <button type="button" onClick={() => stopMic(true)} onContextMenu={(e) => e.preventDefault()} title="Готово — вставить текст"
      style={{ ...iconBtnGuard, width: isMobile ? 38 : 34, height: isMobile ? 38 : 34, borderRadius: R.pill, border: 'none', background: C.success, color: C.onAccent, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
      <Check size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
    </button>
  );

  // QA-цикл, ревью-консилиум и красная команда отправляются и без темы
  // (работают по текущему диффу/контексту)
  const canSend = hasText || attachments.length > 0
    || teamMech === 'qa' || teamMech === 'review' || teamMech === 'redteam';
  // «Стоп» показываем, только когда чат активен и в поле ничего не введено.
  // Как только появился текст — кнопка становится «Отправить» (даже во время генерации).
  const sendButton = isGenerating && !canSend ? (
    <button
      type="button"
      onClick={onStop}
      onContextMenu={(e) => e.preventDefault()}
      title="Остановить"
      style={{
        ...iconBtnGuard,
        width: isMobile ? 38 : 34,
        height: isMobile ? 38 : 34,
        borderRadius: R.pill,
        border: 'none',
        background: C.textHeading,
        color: C.bgMain,
        cursor: 'pointer',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        flexShrink: 0,
      }}
    >
      <StopIcon />
    </button>
  ) : (
    <button
      type="button"
      onClick={handleSend}
      onContextMenu={(e) => e.preventDefault()}
      disabled={!canSend}
      title={isMobile ? 'Отправить' : 'Отправить (Enter) · Shift+Enter — новая строка'}
      style={{
        ...iconBtnGuard,
        width: isMobile ? 38 : 34,
        height: isMobile ? 38 : 34,
        borderRadius: R.pill,
        border: 'none',
        background: canSend ? C.accent : C.bgSelected,
        color: canSend ? C.onAccent : C.textMuted,
        cursor: canSend ? 'pointer' : 'default',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        flexShrink: 0,
        transition: 'background 0.15s, color 0.15s',
      }}
    >
      <ArrowUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  );

  // Единый селектор собеседника (персона или .md-агент). Доступен и в начатом чате:
  // смена по ходу разговора разрешена (персона-слой пересобирается каждый ход).
  const companionSelector = canPickCompanion && (personas.length > 0 || agents.length > 0) && onCompanionChange ? (
    <CompanionSelector
      personas={personas}
      agents={agents}
      selectedPersona={selectedPersona ?? null}
      selectedAgentName={selectedAgentName ?? null}
      onSelect={onCompanionChange}
      isMobile={isMobile}
      wide={widePickers}
      compact={compactStrip}
      onCreateGroup={onCreateGroup}
    />
  ) : null;

  // Сворачиваемые кнопки полосы — в порядке показа. Не влезли → уезжают в «⋯» с конца
  // (то есть справа налево). Режим прав не сворачиваем: он и так крайний слева, а внутри
  // меню его собственный список выбора выглядел бы вложенным меню.
  // eslint-disable-next-line react-hooks/refs -- кнопки не refs: taint от onClick-обработчиков, читающих refs только в событиях
  const collapsible = [
    { key: 'attach', node: attachButton, item: { key: 'attach', icon: <Plus size={16} strokeWidth={ICON_STROKE} />, label: 'Прикрепить файл', sublabel: 'Добавить файл к сообщению', onClick: onAttach } },
    slashButton && { key: 'slash', node: slashButton, item: { key: 'slash', icon: <span style={{ fontFamily: FONT.mono, fontSize: 15, fontWeight: 700, lineHeight: 1 }}>/</span>, label: 'Вставить скилл', sublabel: 'Список навыков через «/»', onClick: handleSlashButton } },
    loopButton && { key: 'loop', node: loopButton, item: { key: 'loop', icon: <RefreshCw size={16} strokeWidth={ICON_STROKE} />, label: 'Цикл «до готово»', sublabel: 'Повторять итерациями, пока не готово', toggle: loopActive, onClick: () => toggleWorkLoopSafe?.() } },
    worktreeButton && { key: 'worktree', node: worktreeButton, item: { key: 'worktree', icon: <FolderGit2 size={16} strokeWidth={ICON_STROKE} />, label: 'Отдельное дерево', sublabel: worktreeToggleDisabled ? (worktreeActive ? `Включено · ${worktreeBranch} · идёт ход…` : 'Пока идёт ход, недоступно') : (worktreeActive ? `Включено · ${worktreeBranch}` : 'Чат в изолированном git worktree'), toggle: worktreeActive, disabled: worktreeToggleDisabled, onClick: () => { if (!worktreeToggleDisabled) void onToggleWorktree?.(); } } },
    discussButton && { key: 'discuss', node: discussButton, item: { key: 'discuss', icon: <Users size={16} strokeWidth={ICON_STROKE} />, label: 'Обсудить с командой', sublabel: 'Выбрать механику совместной работы', toggle: teamOpen, onClick: () => setTeamOpen(o => !o) } },
  ].filter(Boolean) as { key: string; node: React.ReactNode; item: OverflowItem }[];

  const visibleCount = useToolbarOverflow({
    stripRef, fixedLeftRef, badgesRef, rightRef,
    count: collapsible.length,
    // Всегда включено (как в шапке FileViewer): решает замер полосы, а не ширина окна.
    // Гейт по isMobile оставлял планшет и телефон в ландшафте вовсе без сворачивания —
    // кнопки с flexShrink:0 выдавливали строку за край губы
    enabled: true,
    itemWidth: isMobile ? 36 : 32,
    gap: isMobile ? 6 : 4,
    menuWidth: isMobile ? 40 : 34,
  });
  // Запасной клапан переполнения: badgesRef не сворачивается поэлементно, он жмётся
  // flex'ом и режет пилюли через overflow:hidden. До склейки на узком десктопе это
  // прятало только ЗНАЧЕНИЯ (кнопки режимов оставались в ряду), а теперь прячет и
  // управление — поэтому переполненные пилюли дублируем строками-свитчами в «⋯»
  // (как в макете: «пилюля не влезла → режим свитчем, значение в sublabel»).
  // Два нюанса замера: (1) scrollWidth меняется без resize самого блока (новая пилюля
  // в том же clientWidth) — мерим и на каждый рендер, и по ResizeObserver;
  // (2) сам клапан занимает menuWidth+gap полосы и мог бы поддерживать переполнение,
  // его оправдывающее — поэтому порог с запасом: клапан только когда без него обрезка
  // была бы заметной, а не пару пикселей
  const OVERFLOW_SLACK = isMobile ? 62 : 54; // menuWidth + gap + ~16px запаса
  const [badgesOverflowed, setBadgesOverflowed] = useState(false);
  useEffect(() => {
    const el = badgesRef.current;
    if (!el) return;
    const check = () => setBadgesOverflowed(el.scrollWidth > el.clientWidth + OVERFLOW_SLACK);
    check();
    const ro = new ResizeObserver(check);
    ro.observe(el);
    return () => ro.disconnect();
  });

  // Пилюли активных режимов не сворачиваются (живут в badgesRef). В «⋯» дублируем их
  // строками-свитчами: цикл — только когда пилюли реально обрезаны (его «N/M» видно
  // в самой пилюле); команду — также когда «⋯» уже открыт свёрнутыми кнопками (её
  // значение короткое, sublabel — единственное место, где оно видно, а дубль в
  // существующее меню ничего не стоит). Критерий узости — сам факт сворачивания, а не
  // isMobile: полоса жмётся и на планшете. Дерева здесь нет: его кнопка-тумблер живёт
  // в сворачиваемом ряду (collapsible) и попадает в «⋯» общим путём — дубль не нужен,
  // а значение ветки показывает git-бар
  const collapsedAny = visibleCount < collapsible.length;
  const activeModeItems = ([
    badgesOverflowed && loopActive && workLoop && onToggleWorkLoop && { key: 'loop-on', icon: <RefreshCw size={16} strokeWidth={ICON_STROKE} />, label: 'Цикл «до готово»',
      sublabel: workLoop.phase === 'verifying' ? 'Включено · верификация' : `Включено · итерация ${workLoop.iteration}/${workLoop.maxIterations}`,
      toggle: true, onClick: () => toggleWorkLoopSafe?.() },
    (collapsedAny || badgesOverflowed) && teamMechMeta && { key: 'discuss-on', icon: <Users size={16} strokeWidth={ICON_STROKE} />, label: 'Обсудить с командой',
      sublabel: `Включено · ${teamMechMeta.name}`, toggle: true,
      onClick: () => setTeamMech(null) },
  ].filter(Boolean) as OverflowItem[]);
  const hiddenItems = [...collapsible.slice(visibleCount).map(c => c.item), ...activeModeItems];

  // Офлайн: заглушка вместо полей. Компонент остаётся смонтированным, поэтому
  // набранный текст (text) сохраняется до возврата в онлайн. Ранний return строго
  // ПОСЛЕ всех хуков (useToolbarOverflow и пр.) — иначе число хуков между рендерами
  // расходится и React падает с «Rendered fewer hooks than expected».
  if (offline) {
    return (
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
        padding: '14px', borderRadius: 14, background: C.bgPanel,
        border: `1px solid ${C.border}`, color: C.textMuted, fontSize: 13, fontWeight: 600,
      }}>
        <WifiOff size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        Отправка недоступна офлайн
      </div>
    );
  }

  return (
    // Приём файла ловит вся обёртка, а не только белая карточка: полоса контролов под
    // ней визуально часть композера, и промах по ней иначе открывал бы файл в браузере
    // (SPA перезагрузилась бы вместе с черновиком). Оверлей-подсказку рисует карточка.
    // dragleave от детей игнорируем по relatedTarget — иначе подсказка мигает при движении
    <div
      onDrop={handleDrop}
      onDragOver={handleDragOver}
      onDragLeave={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDragOver(false); }}
    >
      {/* Раскрывашка «Обсудить с командой» — над полем композера */}
      {canDiscuss && (
        <TeamDrawer
          open={teamOpen}
          mech={teamMech}
          settings={teamSettings}
          candidates={mentionable}
          availableSkills={skills.map(s => s.name)}
          isProjectChat={isProjectChat}
          chatMode={mode}
          implementActive={!!teamImplement}
          isMobile={isMobile}
          onPick={id => { setTeamMech(id); textareaRef.current?.focus(); }}
          onSettings={setTeamSettings}
          onClose={() => setTeamOpen(false)}
          onResetModes={skills.some(s => s.name === 'oh-my-claudecode:cancel')
            ? () => {
                // Тихий ход: чистит state зависших OMC-режимов (autopilot/ultraqa/ralph).
                // Признака «было ли что чистить» на фронте нет — тост нейтральный
                onSend('/oh-my-claudecode:cancel', [], { auto: true });
                showToast('Командные режимы', 'Запрос на сброс отправлен');
                setTeamOpen(false);
              }
            : undefined}
        />
      )}
    <div style={containerStyle}>
      {/* Перетаскивание файла над композером: подсказка поверх карточки.
          pointerEvents:none — иначе слой перехватил бы drop у самой карточки */}
      {dragOver && (
        <div style={{
          position: 'absolute', inset: 0, zIndex: 4, pointerEvents: 'none',
          borderRadius: R.xxl, background: C.accentLight,
          display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
          color: C.textHeading, fontSize: FS.base, fontWeight: 600, textAlign: 'center', padding: '0 12px',
        }}>
          <Paperclip size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.accent }} />
          Отпустите — прикрепим к сообщению
        </div>
      )}
      {/* Полоска-индикатор лимита подписки по кромке карточки (warn/danger) */}
      {rateWindow && rateWindow.level !== 'normal' && <RateStripe w={rateWindow} isMobile={isMobile} />}
      {/* Dropdown скиллов (показывается над полем ввода при /query) */}
      {showSkillsDropdown && skills.length > 0 && (
        <SkillsDropdown
          skills={skills}
          query={skillQuery}
          onSelect={handleSkillSelect}
          onClose={() => setShowSkillsDropdown(false)}
          anchorRef={textareaRef as React.RefObject<HTMLElement | null>}
          isMobile={isMobile}
        />
      )}
      {/* Dropdown @упоминаний персон (при @query, флаг persona-mentions) */}
      {showMentions && mentionable.length > 0 && (
        <MentionsDropdown
          personas={mentionable}
          query={mentionQuery}
          onSelect={handleMentionSelect}
          onClose={() => setShowMentions(false)}
          anchorRef={textareaRef as React.RefObject<HTMLElement | null>}
          isMobile={isMobile}
        />
      )}
      {/* Чипы вложений */}
      {attachments.length > 0 && (
        <div
          style={{
            display: 'flex',
            flexWrap: 'wrap',
            gap: 7,
            padding: '11px 12px 8px',
          }}
        >
          {attachments.map((filePath) => {
            const name = basename(filePath);
            return (
              <div
                key={filePath}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 5,
                  background: C.accentLight,
                  borderRadius: R.md,
                  height: 30,
                  padding: '0 9px 0 7px',
                  fontSize: 12,
                  color: C.textSecondary,
                }}
              >
                <FileIcon name={name} />
                <span title={name} style={{ maxWidth: 220, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {middleEllipsis(name, isMobile ? 22 : 30)}
                </span>
                <button
                  onClick={() => onRemoveAttachment(filePath)}
                  style={{
                    background: 'none',
                    border: 'none',
                    cursor: 'pointer',
                    padding: 0,
                    marginLeft: 2,
                    width: 24,
                    height: 24,
                    borderRadius: R.full,
                    color: C.textMuted,
                    lineHeight: 1,
                    fontSize: 13,
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    flexShrink: 0,
                  }}
                  title="Удалить"
                  aria-label={`Удалить вложение ${name}`}
                >
                  <X size={13} strokeWidth={ICON_STROKE} />
                </button>
              </div>
            );
          })}
        </div>
      )}

      {/* В белой рамке — только сам ввод: поле, микрофон и «отправить» */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        {inputArea}
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
          {isListening ? <>{cancelRecBtn}{confirmRecBtn}</> : <>{micButton}{sendButton}</>}
        </div>
      </div>
    </div>

    {/* Полоса контролов — ПОД рамкой композера, на собственной «губе»: на десктопе
        чат живёт на холсте с дудл-паттерном, и без опаковой плашки фон просвечивал бы
        прямо под кнопками. Губа ПРИМЫКАЕТ к карточке композера (стиль Claude Desktop):
        отрицательный margin заводит её верх под карточку (карточка positioned и
        рисуется поверх static-губы), скруглены только нижние углы. Строка всегда одна:
        на узком экране пикеры справа схлопнуты в иконки, а левые кнопки по мере
        нехватки места уезжают справа налево в «⋯» (см. useToolbarOverflow). */}
    <div ref={stripRef} style={{
      display: 'flex', alignItems: 'center', gap: isMobile ? 6 : 4,
      flexWrap: 'nowrap', minWidth: 0,
      ...(isMobile
        ? { marginTop: 7, padding: '0 2px' }
        : {
            margin: '-12px 0 0', padding: '15px 8px 4px',
            background: C.bgMain, border: `1px solid ${C.borderLight}`,
            borderRadius: `0 0 ${R.xxl}px ${R.xxl}px`,
            // Низ фигуры обводит та же тень, что у панелей-островов: губа стоит с
            // ними на одной линии, и обрыв без тени рядом с их мягким низом виден
            boxShadow: SHADOW.island,
          }),
    }}>
      <div ref={fixedLeftRef} style={{ display: 'flex', alignItems: 'center', gap: isMobile ? 6 : 4, flexShrink: 0 }}>
        {modeButton}
      </div>
      {collapsible.slice(0, visibleCount).map(c => <span key={c.key} style={{ display: 'flex', flexShrink: 0 }}>{c.node}</span>)}
      {hiddenItems.length > 0 && (
        <ToolbarOverflowMenu isMobile={isMobile} items={hiddenItems} title="Ещё"
          indicator={hiddenItems.some(i => i.toggle)} />
      )}
      <div ref={badgesRef} style={{ display: 'flex', alignItems: 'center', gap: isMobile ? 6 : 4, minWidth: 0, overflow: 'hidden' }}>
        {loopPill}
        {teamImplementBadge}
        {teamPill}
      </div>
      {/* Правая группа: модель → усилие → собеседник, прижаты к правому краю */}
      {(onModelChange || onEffortChange || companionSelector) && (
        <div ref={rightRef} style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: isMobile ? 6 : 4, flexShrink: 0 }}>
          {onModelChange && (
            <ComposerModelPicker
              value={model}
              onChange={onModelChange}
              started={chatStarted}
              isMobile={isMobile}
              compact={compactStrip}
              // У чата с персоной своё назначение модели — пункт «По умолчанию» подписывается им
              usage={selectedPersona ? USAGE.chatPersona : USAGE.chatNew}
            />
          )}
          {onEffortChange && (
            <ComposerEffortPicker value={effort} onChange={onEffortChange} isMobile={isMobile} compact={compactStrip} />
          )}
          {companionSelector}
        </div>
      )}
    </div>

    {/* Десктоп-только: на мобиле пояснение лока уходит в шторку (Modal внутри modeButton) */}
    {!isMobile && lockInfoCallout}

    {pendingMode && (
      <DangerModeConfirm
        mode={pendingMode}
        assistantName={asstName}
        onConfirm={() => { onModeChange(pendingMode); setPendingMode(null); }}
        onCancel={() => setPendingMode(null)}
      />
    )}
    </div>
  );
}
