import { useState, useRef, useEffect, useCallback, type CSSProperties } from 'react';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { SkillsDropdown } from './SkillsDropdown';
import { MentionsDropdown } from './MentionsDropdown';
import { CompanionSelector, type CompanionSelection } from './CompanionSelector';
import { DiscussTeamDialog } from '../features/personas/DiscussTeamDialog';
import { type Mode, MODE_META, MODES, ModeIcon, isDangerMode } from '../lib/modes';
import { DangerModeConfirm } from './DangerModeConfirm';
import { useAssistantName } from './chat/contexts';
import { getDraft, setDraft } from '../lib/drafts';
import { useFeature, FLAGS } from '../lib/featureFlags';
import type { SkillInfo, AgentInfo, Persona, WorkLoopState } from '../types';

export interface ComposerProps {
  // Ключ чата — под него хранится черновик недовведённого текста
  sessionId: string;
  onSend: (text: string, attachments: string[]) => void;
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
  // null — цикл выключен. Тумблер виден при заданном onToggleWorkLoop
  workLoop?: WorkLoopState | null;
  onToggleWorkLoop?: () => void;
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

// SVG микрофона
function MicIcon() {
  return (
    <svg width="17" height="17" viewBox="0 0 17 17" fill="none">
      <rect x="6" y="1" width="5" height="9" rx="2.5" fill="currentColor" />
      <path d="M3 8.5C3 11.538 5.239 14 8.5 14C11.761 14 14 11.538 14 8.5" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <line x1="8.5" y1="14" x2="8.5" y2="16" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <line x1="6" y1="16" x2="11" y2="16" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  );
}

// SVG стрелки отправки
function SendIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
      <path d="M8 13V3M8 3L4 7M8 3L12 7" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
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
  // Autocomplete скиллов
  const [showSkillsDropdown, setShowSkillsDropdown] = useState(false);
  const [skillQuery, setSkillQuery] = useState('');
  const skillWordStartRef = useRef(0);
  // Autocomplete @упоминаний персон (флаг persona-mentions; в групповом чате — всегда)
  const mentionsOn = useFeature(FLAGS.personaMentions);
  // Совещания P7 — по флагу групповых чатов (переключатель в DiscussTeamDialog)
  const groupChatsOn = useFeature(FLAGS.personaGroupChats);
  const isGroupChat = (participantIds?.length ?? 0) > 1;
  const mentionsActive = mentionsOn || isGroupChat;
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
  // Конвейер пантеона (флаг persona-pipeline): запускается из «Обсудить с командой» в любом чате
  const pipelineOn = useFeature(FLAGS.personaPipeline);
  // Мультиперсонная дискуссия: в чате персоны — когда есть кого позвать; конвейер — в любом чате
  const [showDiscuss, setShowDiscuss] = useState(false);
  const canDiscuss = (mentionsActive && !!selectedPersona && mentionable.length > 0)
    || (pipelineOn && !!sessionId);
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

  // Авторазмер textarea
  const autoResize = useCallback(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 200) + 'px';
  }, []);

  useEffect(() => {
    autoResize();
  }, [text, autoResize]);

  const handleSend = () => {
    const t = text.trim();
    if (!t && attachments.length === 0) return;
    onSend(t, attachments);
    setText('');
    if (textareaRef.current) {
      textareaRef.current.style.height = '34px';
    }
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
      handleSend();
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
        alert('Распознавание речи недоступно в браузере на этом устройстве.\n' +
          'Переключаюсь на голосовой ввод клавиатуры — нажми кнопку микрофона ещё раз: ' +
          'откроется клавиатура, на ней нажми микрофон и говори.');
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
      <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
        <line x1="12" y1="5" x2="12" y2="19" />
        <line x1="5" y1="12" x2="19" y2="12" />
      </svg>
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

  // Кнопка «Обсудить с командой» — открывает диалог выбора участников дискуссии
  const discussButton = canDiscuss ? (
    <button
      onClick={() => setShowDiscuss(true)}
      title="Обсудить с командой"
      style={{
        width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none', background: 'none',
        cursor: 'pointer', color: C.textMuted, display: 'flex', alignItems: 'center',
        justifyContent: 'center', flexShrink: 0,
      }}
    >
      <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
        <circle cx="9" cy="7" r="4" />
        <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
        <path d="M16 3.13a4 4 0 0 1 0 7.75" />
      </svg>
    </button>
  ) : null;

  // Цикл «до готово» (флаг work-loop): тумблер + компактный бейдж прогресса итераций
  const workLoopOn = useFeature(FLAGS.workLoop);
  const loopActive = !!workLoop?.active;
  const loopButton = workLoopOn && onToggleWorkLoop ? (
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
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M23 4v6h-6" />
        <path d="M1 20v-6h6" />
        <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10" />
        <path d="M20.49 15a9 9 0 0 1-14.85 3.36L1 14" />
      </svg>
    </button>
  ) : null;
  const loopBadge = workLoopOn && onToggleWorkLoop && loopActive && workLoop ? (
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
      placeholder={`Спросите ${asstName}…`}
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
        <svg width="9" height="9" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round"
          style={{ opacity: 0.55, transform: modeMenuOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}>
          <path d="M6 9l6 6 6-6" />
        </svg>
      </button>
      {modeMenuOpen && (
        <div style={{
          // Кнопка режима теперь слева в обеих раскладках → раскрываем меню вправо (left:0),
          // иначе широкое меню ушло бы за левый край.
          position: 'absolute', bottom: 'calc(100% + 6px)', left: 0,
          minWidth: isMobile ? 260 : 248, maxWidth: 'calc(100vw - 32px)',
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
                  <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: danger ? C.danger : C.textHeading }}>{MODE_META[m].label}{danger ? ' ⚠️' : ''}</span>
                  <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1, lineHeight: 1.35 }}>{MODE_META[m].desc}</span>
                </span>
                {active && (
                  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke={C.accent} strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0, marginTop: 2 }}><path d="M20 6L9 17l-5-5" /></svg>
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
      <MicIcon />
    </button>
  ) : null;

  // Во время записи mic+send заменяются на отмену (✕) и подтверждение (✓)
  const cancelRecBtn = (
    <button type="button" onClick={() => stopMic(false)} onContextMenu={(e) => e.preventDefault()} title="Отменить запись"
      style={{ ...iconBtnGuard, width: isMobile ? 36 : 32, height: isMobile ? 36 : 32, borderRadius: R.pill, border: 'none', background: C.dangerBg, color: C.danger, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
    </button>
  );
  const confirmRecBtn = (
    <button type="button" onClick={() => stopMic(true)} onContextMenu={(e) => e.preventDefault()} title="Готово — вставить текст"
      style={{ ...iconBtnGuard, width: isMobile ? 38 : 34, height: isMobile ? 38 : 34, borderRadius: R.pill, border: 'none', background: C.success, color: C.onAccent, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
      <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
    </button>
  );

  const canSend = hasText || attachments.length > 0;
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
      <SendIcon />
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
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M1 1l22 22" />
          <path d="M16.72 11.06A10.94 10.94 0 0 1 19 12.55M5 12.55a10.94 10.94 0 0 1 5.17-2.39M10.71 5.05A16 16 0 0 1 22.58 9M1.42 9a15.91 15.91 0 0 1 4.7-2.88M8.53 16.11a6 6 0 0 1 6.95 0" />
          <line x1="12" y1="20" x2="12.01" y2="20" />
        </svg>
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
      onCreateGroup={onCreateGroup}
    />
  ) : null;

  return (
    <div style={containerStyle} onDrop={handleDrop} onDragOver={handleDragOver} onDragLeave={() => setDragOver(false)}>
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
                    color: C.textMuted,
                    lineHeight: 1,
                    fontSize: 13,
                    display: 'flex',
                    alignItems: 'center',
                  }}
                  title="Удалить"
                >
                  ✕
                </button>
              </div>
            );
          })}
        </div>
      )}

      {isMobile ? (
        /* Мобильная раскладка: поле ввода во всю ширину, контролы — отдельным рядом снизу */
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <div style={{ display: 'flex' }}>{inputArea}</div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            {attachButton}
            {slashButton}
            {discussButton}
            {loopButton}
            {loopBadge}
            {modeButton}
            {companionSelector}
            <div style={{ flex: 1 }} />
            {isListening ? <>{cancelRecBtn}{confirmRecBtn}</> : <>{micButton}{sendButton}</>}
          </div>
        </div>
      ) : (
        /* Десктоп: всё в одну строку */
        <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
          {modeButton}
          {attachButton}
          {slashButton}
          {discussButton}
          {loopButton}
          {loopBadge}
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

      {showDiscuss && (
        <DiscussTeamDialog
          candidates={mentionable}
          chatPersona={selectedPersona}
          sessionId={sessionId}
          meetingEnabled={groupChatsOn}
          pipelineEnabled={pipelineOn}
          onSend={t => onSend(t, [])}
          onClose={() => setShowDiscuss(false)}
        />
      )}
    </div>
  );
}
