import { useState, useRef, useEffect, useCallback, type CSSProperties } from 'react';
import { AlertTriangle, Ban, ArrowUp, Check, ChevronDown, FolderGit2, Mic, Plus, RefreshCw, Users, WifiOff, X } from 'lucide-react';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { type RateWindow, RATE_COLORS, windowLabel, fmtReset } from '../lib/rateLimit';
import { SkillsDropdown } from './SkillsDropdown';
import { MentionsDropdown } from './MentionsDropdown';
import { CompanionSelector, type CompanionSelection } from './CompanionSelector';
import { ToolbarOverflowMenu, type OverflowItem } from './ToolbarOverflowMenu';
import { useToolbarOverflow } from '../hooks/useToolbarOverflow';
import { ComposerModelPicker } from './ComposerModelPicker';
import { ComposerEffortPicker } from './ComposerEffortPicker';
import { TeamDrawer } from '../features/team/TeamDrawer';
import {
  DEFAULT_TEAM_SETTINGS, buildTeamTurnText, teamMechanic,
  type TeamMechanicId, type TeamMechanicSettings,
} from '../features/team/teamMechanics';
import { setLastMechanic } from '../lib/lastMechanic';
import { type Mode, MODE_META, MODES, ModeIcon, isDangerMode } from '../lib/modes';
import { DangerModeConfirm } from './DangerModeConfirm';
import { useAssistantName } from './chat/contexts';
import { getDraft, setDraft } from '../lib/drafts';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useVoiceInput } from '../hooks/useVoiceInput';
import type { SkillInfo, AgentInfo, Persona, WorkLoopState } from '../types';

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
  // Вставка/перетаскивание картинок (скриншоты) — File-объекты для загрузки и отправки
  onAttachImages?: (files: File[]) => void;
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
  // Отдельное git worktree чата: имя ветки (null — чат в основном дереве проекта).
  // Тумблер виден при заданном onToggleWorktree (только проектный чат с git)
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
}

// Получить имя файла из пути
function basename(filePath: string): string {
  return filePath.replace(/\\/g, '/').split('/').pop() ?? filePath;
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
  onAttachImages,
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
  worktreeBranch = null,
  onToggleWorktree,
  chatContext,
  promptSuggestion = null,
  rateWindow,
}: ComposerProps) {
  const asstName = useAssistantName();
  // Черновик per-session: инициализируем из стора и синхронизируем при переключении чата
  const [text, setText] = useState(() => getDraft(sessionId));
  const draftSessionRef = useRef(sessionId);
  useEffect(() => {
    if (draftSessionRef.current !== sessionId) {
      // Смена чата (Composer переиспользуется без размонтирования): сохраняем черновик
      // уходящего чата и подгружаем черновик открытого
      setDraft(draftSessionRef.current, text);
      draftSessionRef.current = sessionId;
      setText(getDraft(sessionId));
    } else {
      setDraft(sessionId, text);
    }
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
  const [modeMenuOpen, setModeMenuOpen] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  // Опасный режим (bypass) ждёт подтверждения в модалке перед применением
  const [pendingMode, setPendingMode] = useState<Mode | null>(null);
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
  const canDiscuss = !!sessionId;
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const modeRef = useRef<HTMLDivElement>(null);
  // Замеры полосы контролов: по ним решается, сколько кнопок влезает в одну строку
  const stripRef = useRef<HTMLDivElement>(null);
  const fixedLeftRef = useRef<HTMLDivElement>(null);
  const badgesRef = useRef<HTMLDivElement>(null);
  const rightRef = useRef<HTMLDivElement>(null);

  // Голосовой ввод целиком в хуке: распознанное дописываем к тексту, а при мёртвом
  // движке просто ставим фокус — диктовать будет системный ввод клавиатуры
  const { hasSpeech, isListening, recSeconds, startMic, stopMic } = useVoiceInput({
    onResult: chunk => setText(prev => (prev ? prev + ' ' + chunk : chunk)),
    onKeyboardFallback: () => textareaRef.current?.focus(),
  });

  // Автозапуск «Обсудить с командой» — чат открыт через «Созвать команду» из центра
  // команды: раскрываем панель механик
  useEffect(() => {
    if (!sessionId) return;
    if (sessionStorage.getItem('cc_auto_discuss') === sessionId) {
      sessionStorage.removeItem('cc_auto_discuss');
      setTeamOpen(true);
    }
  }, [sessionId]);

  // Закрытие меню режимов по клику вне него
  useEffect(() => {
    if (!modeMenuOpen) return;
    const onDown = (e: MouseEvent) => {
      if (modeRef.current && !modeRef.current.contains(e.target as Node)) setModeMenuOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [modeMenuOpen]);

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
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 200) + 'px';
  }, []);

  useEffect(() => {
    autoResize();
  }, [text, autoResize]);

  const resetInput = () => {
    setText('');
    if (textareaRef.current) {
      textareaRef.current.style.height = '34px';
    }
  };

  const handleSend = async () => {
    const t = text.trim();

    // Командный ход: текст поля — тема, обвязка собирается buildTeamTurnText
    if (teamMech) {
      // Валидация: тема обязательна везде, кроме QA-цикла и ревью/красной команды
      // (они работают по текущему диффу/контексту); дискуссии и командной реализации
      // нужен хотя бы один участник (подсказка — в зоне настроек)
      const topicOptional = teamMech === 'qa' || teamMech === 'review' || teamMech === 'redteam';
      if (!t && !topicOptional) { setTeamOpen(true); return; }
      if ((teamMech === 'discuss' || teamMech === 'implement') && teamSettings.participants.length === 0) { setTeamOpen(true); return; }
      // «Остановиться на плане» у автопилота = честный консенсус-план (ralplan):
      // у скилла autopilot нет флага «стоп на плане», а ralplan делает ровно это —
      // план через спор до одобрения критика, без исполнения
      const effective: TeamMechanicId =
        teamMech === 'autopilot' && !teamSettings.untilDone ? 'consensus' : teamMech;
      // Автопилот «до готово»: включаем цикл work-loop ДО отправки (PUT /chats/{id}/loop),
      // только если он ещё не активен — тумблер переключает состояние
      if (teamMech === 'autopilot' && teamSettings.untilDone && !workLoop?.active && onToggleWorkLoop) {
        await onToggleWorkLoop();
      }
      setLastMechanic(sessionId, effective);
      onSend(buildTeamTurnText(effective, t, teamSettings, chatContext), [], { auto: true });
      setTeamMech(null);
      setTeamOpen(false);
      resetInput();
      return;
    }

    if (!t && attachments.length === 0) return;
    onSend(t, attachments);
    resetInput();
  };

  // Вставка картинки из буфера (скриншот) → отдаём File-объекты родителю на загрузку
  const handlePaste = (e: React.ClipboardEvent) => {
    if (!onAttachImages) return;
    const files: File[] = [];
    for (const item of Array.from(e.clipboardData.items)) {
      if (item.kind === 'file' && item.type.startsWith('image/')) {
        const f = item.getAsFile();
        if (f) files.push(f);
      }
    }
    if (files.length) { e.preventDefault(); onAttachImages(files); }
  };

  const handleDrop = (e: React.DragEvent) => {
    if (!onAttachImages) return;
    const files = Array.from(e.dataTransfer.files).filter(f => f.type.startsWith('image/'));
    setDragOver(false);
    if (files.length) { e.preventDefault(); onAttachImages(files); }
  };

  const handleDragOver = (e: React.DragEvent) => {
    if (!onAttachImages) return;
    if (Array.from(e.dataTransfer.types).includes('Files')) { e.preventDefault(); setDragOver(true); }
  };

  // Подсказка следующего сообщения: дисмисс крестиком живёт до прихода новой подсказки
  const [suggestionDismissed, setSuggestionDismissed] = useState(false);
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
    // Карточка всегда приподнята над лентой (она плавает поверх неё) — раньше подъём
    // давала обёртка в ChatPanel, но её тень пачкала вынесенную наружу полосу контролов
    boxShadow: dragOver ? SHADOW.focus : SHADOW.dropdown,
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

  // Кнопка «Обсудить с командой» — тоггл раскрывашки механик (активная — как loopButton)
  const discussButton = canDiscuss ? (
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

  // Чип выбранной командной механики — слева от поля ввода; крестик снимает режим
  const teamMechMeta = teamMech ? teamMechanic(teamMech) : null;
  const TeamMechIcon = teamMechMeta?.icon;
  const teamChip = teamMechMeta && TeamMechIcon ? (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 6, height: isMobile ? 26 : 24,
      padding: '0 4px 0 10px', borderRadius: R.max, background: C.accentLight, color: C.accent,
      fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
    }}>
      <TeamMechIcon size={13} strokeWidth={2} style={{ flexShrink: 0 }} />
      {teamMechMeta.name}
      <button
        onClick={() => setTeamMech(null)}
        title="Отменить режим"
        style={{
          border: 'none', background: 'none', color: C.accent, cursor: 'pointer',
          width: 18, height: 18, borderRadius: R.full, padding: 0,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}
      >
        <X size={12} strokeWidth={ICON_STROKE} />
      </button>
    </span>
  ) : null;

  // Цикл «до готово»: тумблер + компактный бейдж прогресса итераций
  const loopActive = !!workLoop?.active;
  const loopButton = onToggleWorkLoop ? (
    <button
      onClick={onToggleWorkLoop}
      title="Цикл «до готово»: агент работает итерациями, пока не отчитается о завершении, затем верификационный ход"
      style={{
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none',
        background: loopActive ? C.accentLight : 'none',
        cursor: 'pointer', color: loopActive ? C.accent : C.textMuted,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        transition: 'color 0.15s, background 0.15s',
      }}
    >
      <RefreshCw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  ) : null;
  const loopBadge = onToggleWorkLoop && loopActive && workLoop ? (
    <span style={{
      display: 'inline-flex', alignItems: 'center', height: isMobile ? 26 : 24,
      padding: '0 9px', borderRadius: R.pill, background: C.accentLight, color: C.accent,
      fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
    }}>
      {workLoop.phase === 'verifying'
        ? 'Цикл: верификация'
        : `Цикл: итерация ${workLoop.iteration}/${workLoop.maxIterations}`}
    </span>
  ) : null;

  // Отдельное git worktree: тумблер + бейдж ветки (как loopButton/loopBadge)
  const worktreeActive = !!worktreeBranch;
  const worktreeButton = onToggleWorktree ? (
    <button
      onClick={onToggleWorktree}
      title={worktreeActive
        ? `Чат работает в отдельном дереве (ветка ${worktreeBranch}) — нажми, чтобы вернуть в проект`
        : 'Отдельное дерево: чат работает в изолированном git worktree на своей ветке'}
      style={{
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none',
        background: worktreeActive ? C.accentLight : 'none',
        cursor: 'pointer', color: worktreeActive ? C.accent : C.textMuted,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        transition: 'color 0.15s, background 0.15s',
      }}
    >
      <FolderGit2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  ) : null;
  const worktreeBadge = onToggleWorktree && worktreeActive ? (
    <span title={`Изолированное дерево чата, ветка ${worktreeBranch}`} style={{
      display: 'inline-flex', alignItems: 'center', height: isMobile ? 26 : 24, maxWidth: 180,
      padding: '0 9px', borderRadius: R.pill, background: C.accentLight, color: C.accent,
      fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden',
      textOverflow: 'ellipsis', flexShrink: 0,
    }}>
      {worktreeBranch}
    </span>
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
        onClick={() => setModeMenuOpen(o => !o)}
        // В сжатом виде подпись скрыта — значение уносим в тултип, как у модели и усилия
        title={`Режим работы: ${MODE_META[mode].label}`}
        // Фон только на наведении/открытии: полоса лежит на тени карточки композера,
        // и залитые плашки разрезали бы её пятнами
        onMouseEnter={e => { if (!modeMenuOpen) e.currentTarget.style.background = C.accentLight; }}
        onMouseLeave={e => { if (!modeMenuOpen) e.currentTarget.style.background = 'transparent'; }}
        style={{
          // Сжатый (мобильный) вид — иконка + шеврон без подписи
          ...(isMobile
            ? { height: 36, padding: '0 6px', justifyContent: 'center', gap: 3 }
            : { height: 28, padding: '0 10px' }),
          borderRadius: R.md, border: 'none',
          background: modeMenuOpen ? C.bgSelected : 'transparent',
          color: mode === 'bypass' ? C.danger : C.textSecondary,
          fontSize: 12.5, fontWeight: 600, cursor: 'pointer', whiteSpace: 'nowrap',
          display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
          transition: 'background 0.15s',
        }}
      >
        <ModeIcon mode={mode} />
        {/* В сжатом виде прячем только подпись (длинные названия распирают строку) —
            шеврон остаётся, как у модели, усилия и собеседника. Название — в тултипе. */}
        {!isMobile && MODE_META[mode].label}
        <ChevronDown size={isMobile ? 10 : ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ flexShrink: 0, opacity: 0.55, transform: modeMenuOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
      </button>
      {modeMenuOpen && (
        <div style={{
          // Десктоп: absolute от кнопки (вправо). Мобил: fixed во всю ширину (left/right 16px),
          // bottom — чуть выше кнопки по getBoundingClientRect, чтобы меню не уезжало за край
          // экрана, когда кнопка сместилась из-за переноса строк.
          ...(isMobile
            ? (() => { const r = modeRef.current?.getBoundingClientRect(); return { position: 'fixed' as const, left: 16, right: 16, bottom: r ? window.innerHeight - r.top + 6 : 80 }; })()
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
      wide={!isMobile}
      compact={isMobile}
      onCreateGroup={onCreateGroup}
    />
  ) : null;

  // Сворачиваемые кнопки полосы — в порядке показа. Не влезли → уезжают в «⋯» с конца
  // (то есть справа налево). Режим прав не сворачиваем: он и так крайний слева, а внутри
  // меню его собственный список выбора выглядел бы вложенным меню.
  const collapsible = [
    { key: 'attach', node: attachButton, item: { key: 'attach', icon: <Plus size={16} strokeWidth={ICON_STROKE} />, label: 'Прикрепить файл', sublabel: 'Добавить файл к сообщению', onClick: onAttach } },
    slashButton && { key: 'slash', node: slashButton, item: { key: 'slash', icon: <span style={{ fontFamily: FONT.mono, fontSize: 15, fontWeight: 700, lineHeight: 1 }}>/</span>, label: 'Вставить скилл', sublabel: 'Список навыков через «/»', onClick: handleSlashButton } },
    loopButton && { key: 'loop', node: loopButton, item: { key: 'loop', icon: <RefreshCw size={16} strokeWidth={ICON_STROKE} />, label: 'Цикл «до готово»', sublabel: 'Повторять итерациями, пока не готово', toggle: loopActive, onClick: () => { void onToggleWorkLoop?.(); } } },
    worktreeButton && { key: 'worktree', node: worktreeButton, item: { key: 'worktree', icon: <FolderGit2 size={16} strokeWidth={ICON_STROKE} />, label: 'Отдельное дерево', sublabel: 'Чат в изолированном git worktree', toggle: worktreeActive, onClick: () => { void onToggleWorktree?.(); } } },
    discussButton && { key: 'discuss', node: discussButton, item: { key: 'discuss', icon: <Users size={16} strokeWidth={ICON_STROKE} />, label: 'Обсудить с командой', sublabel: 'Выбрать механику совместной работы', toggle: teamOpen, onClick: () => setTeamOpen(o => !o) } },
  ].filter(Boolean) as { key: string; node: React.ReactNode; item: OverflowItem }[];

  const visibleCount = useToolbarOverflow({
    stripRef, fixedLeftRef, badgesRef, rightRef,
    count: collapsible.length,
    enabled: !!isMobile,
    itemWidth: isMobile ? 36 : 32,
    gap: isMobile ? 6 : 4,
    menuWidth: isMobile ? 40 : 34,
  });
  const hiddenItems = collapsible.slice(visibleCount).map(c => c.item);

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
    <div>
      {/* Раскрывашка «Обсудить с командой» — над полем композера */}
      {canDiscuss && (
        <TeamDrawer
          open={teamOpen}
          mech={teamMech}
          settings={teamSettings}
          candidates={mentionable}
          availableSkills={skills.map(s => s.name)}
          isMobile={isMobile}
          onPick={id => { setTeamMech(id); textareaRef.current?.focus(); }}
          onSettings={setTeamSettings}
          onClose={() => setTeamOpen(false)}
          onResetModes={skills.some(s => s.name === 'oh-my-claudecode:cancel')
            ? () => {
                // Тихий ход: чистит state зависших OMC-режимов (autopilot/ultraqa/ralph)
                onSend('/oh-my-claudecode:cancel', [], { auto: true });
                setTeamOpen(false);
              }
            : undefined}
        />
      )}
    <div style={containerStyle} onDrop={handleDrop} onDragOver={handleDragOver} onDragLeave={() => setDragOver(false)}>
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
                <span style={{ maxWidth: 160, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {name}
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
        {loopBadge}
        {worktreeBadge}
        {teamChip}
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
              compact={isMobile}
            />
          )}
          {onEffortChange && (
            <ComposerEffortPicker value={effort} onChange={onEffortChange} isMobile={isMobile} compact={isMobile} />
          )}
          {companionSelector}
        </div>
      )}
    </div>

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
