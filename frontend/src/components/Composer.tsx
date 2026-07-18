import { useState, useRef, useEffect, useLayoutEffect, useCallback, type CSSProperties } from 'react';
import { AlertTriangle, ArrowUp, Check, ChevronDown, Mic, MoreVertical, Plus, RefreshCw, Users, WifiOff, X } from 'lucide-react';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { SkillsDropdown } from './SkillsDropdown';
import { MentionsDropdown } from './MentionsDropdown';
import { CompanionSelector, type CompanionSelection } from './CompanionSelector';
import { ToolbarOverflowMenu, type OverflowItem } from './ToolbarOverflowMenu';
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
import { showToast } from '../lib/toast';
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
  // Краткий контекст последних реплик чата — для механики «Панель экспертов»
  // с настройкой «Приложить контекст чата» (собирает ChatPanel из ленты)
  chatContext?: string;
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
  participantIds = null,
  onCreateGroup,
  workLoop = null,
  onToggleWorkLoop,
  chatContext,
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
  const [isListening, setIsListening] = useState(false);
  const [recSeconds, setRecSeconds] = useState(0);
  const [modeMenuOpen, setModeMenuOpen] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  // Опасный режим (bypass) ждёт подтверждения в модалке перед применением
  const [pendingMode, setPendingMode] = useState<Mode | null>(null);
  // Разнос композера в две позиции (собрано ↔ разнесено): поле ввода и «отправить»
  // своей строкой, второстепенные кнопки полосой ниже. Два независимых триггера:
  // pinned — ручной пин грипом ⋮ (на планшете уводит поле из-под облачка раскладки
  // клавиатуры), запоминается per-device; autoWide — авто-разнос при многострочном
  // тексте, чтобы текст занимал всю ширину композера, а не узкий столбик между
  // кнопками слева и собеседником справа (не персистится).
  const [pinned, setPinned] = useState(() => {
    try { return localStorage.getItem('cc_composer_split') === '1'; } catch { return false; }
  });
  useEffect(() => {
    try { localStorage.setItem('cc_composer_split', pinned ? '1' : '0'); } catch { /* noop */ }
  }, [pinned]);
  const [autoWide, setAutoWide] = useState(false);
  // Разнос действует только на планшете/десктопе: в мобильной раскладке поле и так
  // занимает отдельную строку во всю ширину. pinned/autoWide не сбрасываем — при
  // возврате к широкому экрану вид восстановится.
  const splitActive = (pinned || autoWide) && !isMobile;
  // Драг грипа ⋮: тянем — раскладка переключается под пальцем (live), «дотягивая» до
  // нового размера. dragStartYRef — якорь текущего порога (сдвигается при переключении =
  // гистерезис), dragMovedRef — был ли реальный сдвиг (чтобы погасить паразитный click).
  const [dragging, setDragging] = useState(false);
  const dragStartYRef = useRef<number | null>(null);
  const dragMovedRef = useRef(false);
  // FLIP-анимация при разносе: замеряем top textarea и высоту контейнера ДО смены раскладки,
  // после — проигрываем разницу (translateY поля + height контейнера), чтобы переход был
  // плавным в обе стороны (и раскрытие, и сборка), а не скачком.
  const flipInputTopRef = useRef<number | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const flipContainerHRef = useRef<number | null>(null);
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
  const recognitionRef = useRef<any>(null);
  const recCancelRef = useRef(false);
  const micWatchdogRef = useRef<number | null>(null); // детект «мёртвого» Web Speech (нет audiostart)
  const modeRef = useRef<HTMLDivElement>(null);

  // Таймер записи голоса
  useEffect(() => {
    if (!isListening) { setRecSeconds(0); return; }
    setRecSeconds(0);
    const id = setInterval(() => setRecSeconds(s => s + 1), 1000);
    return () => clearInterval(id);
  }, [isListening]);

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

  const hasSpeech = typeof window !== 'undefined' &&
    ('SpeechRecognition' in window || 'webkitSpeechRecognition' in window);

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

  // Снимок позиции поля и высоты контейнера перед сменой раскладки — для FLIP-анимации.
  // Плюс фокус и каретка: при смене раскладки textarea пересоздаётся (другая ветка JSX),
  // без восстановления в layout-эффекте набор текста обрывается на моменте разноса.
  const flipFocusRef = useRef<{ start: number; end: number } | null>(null);
  const captureFlip = () => {
    const el = textareaRef.current;
    flipInputTopRef.current = el?.getBoundingClientRect().top ?? null;
    flipContainerHRef.current = containerRef.current?.getBoundingClientRect().height ?? null;
    flipFocusRef.current = el && document.activeElement === el
      ? { start: el.selectionStart ?? el.value.length, end: el.selectionEnd ?? el.value.length }
      : null;
  };

  // Ширина поля в собранной раскладке (замер до разноса) и канвас для замера текста —
  // чтобы понять, влезет ли текст обратно в одну узкую строку при сборке
  const collapsedInputWidthRef = useRef<number | null>(null);
  const measureCtxRef = useRef<CanvasRenderingContext2D | null>(null);
  const fitsCollapsedWidth = (el: HTMLTextAreaElement) => {
    const narrowW = collapsedInputWidthRef.current;
    if (narrowW === null) return true;
    if (el.value.includes('\n')) return false;
    if (!measureCtxRef.current) measureCtxRef.current = document.createElement('canvas').getContext('2d');
    const ctx = measureCtxRef.current;
    if (!ctx) return true;
    const cs = getComputedStyle(el);
    ctx.font = cs.font || `${cs.fontSize} ${cs.fontFamily}`; // Firefox: шорткат font пустой
    return ctx.measureText(el.value).width <= narrowW - 16;
  };

  // Авторазмер textarea + авто-разнос при многострочном тексте (планшет/десктоп).
  // Гистерезис: разносим, когда текст уверенно выше одной строки (44px), собираем при
  // возврате к одной строке (<=36px) — и только если текст влезает в прежнюю узкую
  // ширину поля, иначе раскладка зациклится (в узкой снова две строки → снова разнос).
  const autoResize = useCallback(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    const h = el.scrollHeight;
    el.style.height = Math.min(h, 200) + 'px';
    if (isMobile) return;
    if (!autoWide) {
      if (!splitActive) collapsedInputWidthRef.current = el.clientWidth;
      if (h > 44) { captureFlip(); setAutoWide(true); }
    } else if (h <= 36 && fitsCollapsedWidth(el)) {
      captureFlip();
      setAutoWide(false);
    }
  }, [isMobile, autoWide, splitActive]);

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

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // На мобиле Enter переносит строку, отправка — только кнопкой (десктоп: Enter отправляет)
    if (e.key === 'Enter' && !e.shiftKey && !isMobile) {
      e.preventDefault();
      void handleSend();
    }
  };

  // Голосовой ввод. На устройствах с рабочим Web Speech (телефоны) распознаём сами.
  // Где движок «мёртвый» (например, Huawei без Google-сервисов) — фокусируем поле,
  // чтобы пользователь надиктовал системным голосовым вводом клавиатуры.
  const micKeyboardOnly = () => {
    try { return localStorage.getItem('micKeyboardFallback') === '1'; } catch { return false; }
  };

  const startMic = () => {
    if (isListening) return;

    // Web Speech отсутствует или ранее выяснили, что он не работает → сразу клавиатура.
    if (!hasSpeech || micKeyboardOnly()) {
      textareaRef.current?.focus();
      return;
    }

    const SpeechRecognitionCtor =
      (window as any).SpeechRecognition ?? (window as any).webkitSpeechRecognition;
    const rec = new SpeechRecognitionCtor() as any;
    rec.lang = 'ru-RU';
    rec.interimResults = true;
    rec.continuous = false;
    rec.maxAlternatives = 1;
    recCancelRef.current = false;

    let gotAudio = false;
    const clearWatchdog = () => {
      if (micWatchdogRef.current !== null) { clearTimeout(micWatchdogRef.current); micWatchdogRef.current = null; }
    };

    rec.onaudiostart = () => { gotAudio = true; clearWatchdog(); };

    rec.onresult = (e: any) => {
      let last = '';
      for (let i = 0; i < e.results.length; i++) {
        const r = e.results[i];
        if (r.isFinal && r[0]?.transcript) last = r[0].transcript;
      }
      if (recCancelRef.current) return; // отменено — не вставляем
      if (last) setText(prev => (prev ? prev + ' ' + last : last));
    };

    rec.onend = () => { clearWatchdog(); setIsListening(false); };
    rec.onerror = () => { clearWatchdog(); setIsListening(false); };

    recognitionRef.current = rec;
    try {
      rec.start();
      setIsListening(true);
      // Детектор «мёртвого» движка: если за 2.5с не пришёл audiostart — распознавания
      // в браузере нет (нет Google-сервисов). Переходим на клавиатурный ввод и
      // запоминаем выбор, чтобы впредь сразу открывать клавиатуру.
      micWatchdogRef.current = window.setTimeout(() => {
        if (gotAudio) return;
        try { rec.abort(); } catch { /* noop */ }
        setIsListening(false);
        try { localStorage.setItem('micKeyboardFallback', '1'); } catch { /* noop */ }
        // Тост клампится тремя строками — текст короче исходного alert, но с той же сутью
        showToast('Голосовой ввод',
          'Распознавание речи в браузере недоступно. Нажми кнопку микрофона ещё раз — откроется клавиатура, говори через её микрофон.');
      }, 2500);
    } catch {
      setIsListening(false);
    }
  };

  // confirm=true — остановить и вставить распознанное; false — отменить без вставки
  const stopMic = (confirm: boolean) => {
    recCancelRef.current = !confirm;
    if (micWatchdogRef.current !== null) { clearTimeout(micWatchdogRef.current); micWatchdogRef.current = null; }
    setIsListening(false); // фикс: закрываем режим записи сразу, не дожидаясь onend (его может не быть)
    try {
      if (confirm) recognitionRef.current?.stop();
      else recognitionRef.current?.abort();
    } catch { /* noop */ }
  };

  // После смены раскладки: проигрываем разницу позиций поля (translateY+fade) и высоты
  // контейнера (height) как плавный переход — одинаково при раскрытии и сборке.
  useLayoutEffect(() => {
    // Пересчёт высоты поля под новую ширину — текст мог перелечь на другое число строк
    autoResize();
    const DUR = 0.3;
    // Поле: плавный переезд по вертикали + лёгкий fade (прячет резкую смену ширины)
    const el = textareaRef.current;
    // Возврат фокуса и каретки в пересозданную textarea — до отрисовки, набор не рвётся
    const focus = flipFocusRef.current;
    flipFocusRef.current = null;
    if (el && focus) {
      el.focus({ preventScroll: true });
      el.setSelectionRange(focus.start, focus.end);
    }
    const prevTop = flipInputTopRef.current;
    flipInputTopRef.current = null;
    if (el && prevTop !== null) {
      const dy = prevTop - el.getBoundingClientRect().top;
      if (Math.abs(dy) >= 2) {
        el.style.transition = 'none';
        el.style.transform = `translateY(${dy}px)`;
        el.style.opacity = '0.35';
        void el.offsetHeight;
        requestAnimationFrame(() => {
          el.style.transition = `transform ${DUR}s ease, opacity ${DUR}s ease`;
          el.style.transform = 'translateY(0)';
          el.style.opacity = '1';
        });
      }
    }
    // Контейнер: плавное схлопывание/раскрытие высоты (overflow hidden прячет «лишнюю»
    // строку, пока высота едет) — даёт плавную сборку, а не резкое исчезновение полосы
    const c = containerRef.current;
    const prevH = flipContainerHRef.current;
    flipContainerHRef.current = null;
    if (c && prevH !== null) {
      const newH = c.getBoundingClientRect().height;
      if (Math.abs(newH - prevH) >= 2) {
        c.style.overflow = 'hidden';
        c.style.height = `${prevH}px`;
        void c.offsetHeight;
        requestAnimationFrame(() => {
          c.style.transition = `height ${DUR}s ease`;
          c.style.height = `${newH}px`;
        });
        window.setTimeout(() => {
          c.style.transition = '';
          c.style.height = '';
          c.style.overflow = '';
        }, DUR * 1000 + 30);
      }
    }
    // autoResize намеренно вне deps: эффект должен играть только на смену раскладки
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [splitActive]);

  // --- Drag грипа ⋮: live-переключение композера в две позиции (собрано ↔ разнесено) ---
  // Порог протягивания — раньше срабатывает, смена начинается почти сразу за движением.
  const SNAP = 18;
  // Ширина зоны справа от собеседника в собранном виде (для выравнивания его на нижней
  // строке под правый край поля): разделитель 12 + mic 32 + send 34 + зазоры.
  const micSendW = 86;
  const onGripPointerDown = (e: React.PointerEvent) => {
    // preventDefault — чтобы не начиналось выделение текста/нативный drag на десктопе
    e.preventDefault();
    setModeMenuOpen(false); // страховка: меню режима не должно висеть во время смены раскладки
    dragStartYRef.current = e.clientY;
    dragMovedRef.current = false;
    setDragging(true);
  };
  // Слушатели move/up вешаем на window: грип переезжает между строками при смене раскладки
  // (его DOM-узел пересоздаётся), поэтому pointer capture на кнопке порвался бы посреди
  // драга — а это и роняло финальный click на кнопку режима под пальцем («Авто»-попап).
  useEffect(() => {
    if (!dragging) return;
    const onMove = (e: PointerEvent) => {
      if (dragStartYRef.current === null) return;
      const dy = e.clientY - dragStartYRef.current;
      if (Math.abs(dy) > 4) dragMovedRef.current = true;
      // Live-переключение под пальцем; после смены сдвигаем якорь (гистерезис против дребезга).
      // Тянем pinned, а не splitActive: при активном autoWide (длинный текст) сборка
      // грипом не сработает визуально — раскладка вернётся сама, когда текст укоротится.
      if (!pinned && dy < -SNAP) { captureFlip(); setPinned(true); dragStartYRef.current = e.clientY; }
      else if (pinned && dy > SNAP) { captureFlip(); setPinned(false); dragStartYRef.current = e.clientY; }
    };
    const onUp = () => {
      const moved = dragMovedRef.current;
      dragStartYRef.current = null;
      setDragging(false);
      // Гасим один паразитный click по элементу под пальцем (иначе всплывает меню режима)
      if (moved) {
        const swallow = (ev: Event) => { ev.stopPropagation(); ev.preventDefault(); };
        window.addEventListener('click', swallow, { capture: true, once: true });
        window.setTimeout(() => window.removeEventListener('click', swallow, true), 350);
      }
    };
    window.addEventListener('pointermove', onMove, { passive: false });
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onUp);
    return () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onUp);
    };
  }, [dragging, pinned]);

  // Стили контейнера — поле всегда активно (доступно для ввода и во время генерации)
  const containerStyle: React.CSSProperties = {
    position: 'relative',
    background: C.bgWhite,
    border: `1px solid ${dragOver || hasText ? C.accent : C.border}`,
    borderRadius: R.xxl,
    padding: isMobile ? '8px 10px' : '7px 8px',
    boxShadow: dragOver ? SHADOW.focus : hasText ? SHADOW.card : 'none',
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

  const inputArea = isListening ? (
    <div style={{ ...dotsStyle, gap: 10 }}>
      <span style={{ width: 9, height: 9, borderRadius: '50%', background: C.danger, animation: 'pulsedot 1s ease-in-out infinite', flexShrink: 0 }} />
      <span style={{ fontSize: 13, color: C.dangerText, fontWeight: 600, fontFamily: FONT.mono, flexShrink: 0, minWidth: 34 }}>{fmtRecTime(recSeconds)}</span>
      <Waveform />
    </div>
  ) : (
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
      placeholder={teamMechMeta ? teamMechMeta.placeholder : `Спросите ${asstName}…`}
      rows={1}
      style={{
        flex: 1,
        width: isMobile ? '100%' : undefined,
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
  );

  const modeButton = (
    <div ref={modeRef} style={{ position: 'relative', flexShrink: 0 }}>
      <button
        onClick={() => setModeMenuOpen(o => !o)}
        title="Режим работы"
        style={{
          height: isMobile ? 32 : 28, padding: isMobile ? '0 8px' : '0 10px', borderRadius: R.md, border: 'none',
          background: modeMenuOpen ? C.bgSelected : C.accentLight,
          color: mode === 'bypass' ? C.danger : C.textSecondary,
          fontSize: 12.5, fontWeight: 600, cursor: 'pointer', whiteSpace: 'nowrap',
          display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
        }}
      >
        <ModeIcon mode={mode} />
        {/* На мобилке только иконка — длинные названия распирают строку контролов; полные подписи есть в списке */}
        {!isMobile && MODE_META[mode].label}
        <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ opacity: 0.55, transform: modeMenuOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
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

  // Грип ⋮ — тащим вертикально, чтобы разнести/собрать композер (снап в 2 позиции).
  // touchAction:'none' обязателен: иначе тач-драг проскроллит страницу вместо перетаскивания.
  const gripButton = (
    <button
      type="button"
      onPointerDown={onGripPointerDown}
      onContextMenu={(e) => e.preventDefault()}
      title={pinned ? 'Собрать панель (потяни вниз)'
        : splitActive ? 'Закрепить разнесённый вид (потяни вверх)'
        : 'Приподнять поле ввода (потяни вверх)'}
      aria-label="Перетащить панель ввода"
      style={{
        ...iconBtnGuard,
        touchAction: 'none',
        width: 24, height: 32, borderRadius: R.pill, border: 'none',
        background: 'none', cursor: dragging ? 'grabbing' : 'grab', color: C.textMuted,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}
    >
      <MoreVertical size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  );

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
      title="Отправить (Enter)"
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

  // Офлайн: заглушка вместо полей. Компонент остаётся смонтированным,
  // поэтому набранный текст (text) сохраняется до возврата в онлайн.
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
      wide={splitActive}
      onCreateGroup={onCreateGroup}
    />
  ) : null;

  // Мобильный overflow «⋯»: вложение файла + цикл «до готово» + вставка скилла (уводим
  // из ряда контролов — так он не наматывается в 2-3 строки, а в ряду остаётся место
  // под всегда видимую кнопку «Обсудить с командой»).
  const overflowItems: OverflowItem[] = [];
  overflowItems.push({
    key: 'attach', icon: <Plus size={16} strokeWidth={ICON_STROKE} />,
    label: 'Прикрепить файл', onClick: onAttach,
  });
  if (onToggleWorkLoop) overflowItems.push({
    key: 'loop', icon: <RefreshCw size={16} strokeWidth={ICON_STROKE} />,
    label: 'Цикл «до готово»', sublabel: 'Повторять итерациями, пока не готово',
    toggle: loopActive, onClick: () => { void onToggleWorkLoop(); },
  });
  if (skills.length > 0) overflowItems.push({
    key: 'slash', icon: <span style={{ fontFamily: FONT.mono, fontSize: 15, fontWeight: 700, lineHeight: 1 }}>/</span>,
    label: 'Вставить скилл', sublabel: 'Список навыков через «/»', onClick: handleSlashButton,
  });

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
    <div ref={containerRef} style={containerStyle} onDrop={handleDrop} onDragOver={handleDragOver} onDragLeave={() => setDragOver(false)}>
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

      {splitActive ? (
        /* РАЗНЕСЕНО (только планшет/десктоп): поле ввода + «отправить» вверх во всю
           ширину, полоса контролов снизу. Включается грипом (планшет: уводит поле
           из-под облачка клавиатуры) или само при многострочном тексте (autoWide). */
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {/* Верхняя строка: грип, поле и «отправить» */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            {gripButton}
            {teamChip}
            {inputArea}
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
              {isListening ? <>{cancelRecBtn}{confirmRecBtn}</> : <>{micButton}{sendButton}</>}
            </div>
          </div>
          {/* Нижняя полоса контролов — порядок и gap как в собранной раскладке, плюс
              спейсер шириной грипа: все кнопки встают ровно на свои прежние X.
              Собеседник прижат вправо с резервом под зону mic/send верхней строки. */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 4, flexWrap: 'wrap', minWidth: 0 }}>
            <div style={{ width: 24, flexShrink: 0 }} aria-hidden />
            {modeButton}
            {attachButton}
            {slashButton}
            {discussButton}
            {loopButton}
            {loopBadge}
            {companionSelector && (
              <>
                <div style={{ marginLeft: 'auto', flexShrink: 0 }}>{companionSelector}</div>
                <div style={{ width: micSendW, flexShrink: 0 }} aria-hidden />
              </>
            )}
          </div>
        </div>
      ) : isMobile ? (
        /* Мобильная раскладка: статус-пилюли + поле сверху; primary-контролы фиксированным
           рядом снизу. Вложение/цикл/скилл спрятаны в «⋯», «Обсудить с командой» всегда видна —
           row2 не наматывается в 2-3 строки, mic/send всегда на месте (гарантированные 2 строки).
           Грипа здесь нет: разнос на узком экране недоступен (вернётся при расширении). */
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>{teamChip}{loopBadge}{inputArea}</div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            {/* Primary-контролы: overflow-меню, обсуждение с командой, режим, собеседник */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, flex: 1, minWidth: 0 }}>
              {overflowItems.length > 0 && (
                <ToolbarOverflowMenu isMobile items={overflowItems} title="Ещё" indicator={loopActive} />
              )}
              {discussButton}
              {modeButton}
              {companionSelector}
            </div>
            {/* Правая группа — всегда справа, не переносится: mic+send стоят на месте */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
              {isListening ? <>{cancelRecBtn}{confirmRecBtn}</> : <>{micButton}{sendButton}</>}
            </div>
          </div>
        </div>
      ) : (
        /* СОБРАНО (десктоп): всё в одну строку; грип первым */
        <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
          {gripButton}
          {modeButton}
          {attachButton}
          {slashButton}
          {discussButton}
          {loopButton}
          {loopBadge}
          {teamChip}
          {inputArea}
          {companionSelector}
          {isListening ? <>{cancelRecBtn}{confirmRecBtn}</> : <><div style={{ width: 12, flexShrink: 0 }} />{micButton}{sendButton}</>}
        </div>
      )}

      {pendingMode && (
        <DangerModeConfirm
          mode={pendingMode}
          assistantName={asstName}
          onConfirm={() => { onModeChange(pendingMode); setPendingMode(null); }}
          onCancel={() => setPendingMode(null)}
        />
      )}
    </div>
    </div>
  );
}
