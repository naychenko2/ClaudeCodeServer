import { useState, useRef, useEffect, useMemo, createContext, useContext, Fragment } from 'react';
import type { Project, Session, ChatItem, FileEntry } from '../types';
import { useSession } from '../hooks/useSession';
import { useOnline } from '../hooks/useOnline';
import { api, type WorkflowAgentInfo } from '../lib/api';
import { modelLabel } from '../lib/models';
import { Composer } from './Composer';
import { EditSessionDialog } from './EditSessionDialog';
import { C, FONT, R, MODAL_W, SHADOW } from '../lib/design';
import { Toolbar, ToolbarIconButton } from './Toolbar';
import { BackButton, Modal } from './ui';
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';

interface Props {
  session: Session;
  project: Project;
  onOpenFile: (path: string) => void;
  pendingMessage?: string;
  onPendingMessageSent?: () => void;
  onSessionUpdated?: (session: Session) => void;
  dockMode?: 'expanded' | 'collapsed';
  onToggleDock?: () => void;
  isMobile?: boolean;
  onBack?: () => void;
}

// Спиннер для выполняющегося инструмента
function ToolSpinner() {
  return <div className="tool-spinner" />;
}

// Иконка режима «План» — прямоугольник с линиями (как ModeIcon plan в Composer)
function PlanIcon({ size = 13, color = 'currentColor', strokeWidth = 2 }: { size?: number; color?: string; strokeWidth?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={strokeWidth} strokeLinecap="round" strokeLinejoin="round">
      <rect x="9" y="3" width="6" height="4" rx="1" />
      <path d="M9 5H7a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-2" />
      <path d="M9 12h6M9 16h4" />
    </svg>
  );
}

// Русские названия режимов для подзаголовка шапки чата
const MODE_LABELS: Record<'auto' | 'plan' | 'ask', string> = {
  auto: 'Авто',
  plan: 'План',
  ask: 'Спросить',
};

// Инлайн-чип режима в подзаголовке шапки чата
function ModeChip({ mode }: { mode: 'auto' | 'plan' | 'ask' }) {
  const isPlan = mode === 'plan';
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 4,
      background: isPlan ? C.planLight : C.accentLight,
      color: isPlan ? C.planText : C.textMuted,
      borderRadius: R.sm,
      padding: '1px 6px', fontFamily: FONT.sans, fontWeight: 600, fontSize: 11,
      verticalAlign: 'baseline',
    }}>
      {isPlan && <PlanIcon size={11} color={C.planText} />}
      {MODE_LABELS[mode]}
    </span>
  );
}

// Фаза работы режима «План» — выводится из ленты, mode и isWaiting (сервер фазу не присылает)
type PlanPhase = 'review' | 'executing' | 'done' | 'replanning' | 'planning' | 'idle' | null;

function derivePlanPhase(items: ChatItem[], mode: 'auto' | 'plan' | 'ask', isWaiting: boolean): PlanPhase {
  // «Текущий ход» — от последнего user_message
  let turnStart = -1;
  for (let i = items.length - 1; i >= 0; i--) {
    if (items[i].kind === 'user_message') { turnStart = i; break; }
  }
  const turn = turnStart >= 0 ? items.slice(turnStart) : items;

  // Незакрытый запрос на согласование — на согласовании
  const pendingReview = items.some(it => it.kind === 'plan_review' && !it.resolved);
  if (pendingReview) return 'review';

  // Последний plan_review (по всей ленте) и его позиция
  let lastReviewIdx = -1;
  for (let i = items.length - 1; i >= 0; i--) {
    if (items[i].kind === 'plan_review') { lastReviewIdx = i; break; }
  }
  if (lastReviewIdx >= 0) {
    const lastReview = items[lastReviewIdx] as Extract<ChatItem, { kind: 'plan_review' }>;
    if (lastReview.approved) {
      const hasResultAfter = items.slice(lastReviewIdx + 1).some(it => it.kind === 'result');
      if (hasResultAfter) return 'done';
      if (isWaiting) return 'executing';
    } else if (lastReview.resolved && lastReview.approved === false && isWaiting) {
      return 'replanning';
    }
  }

  if (mode === 'plan' && isWaiting) {
    const reviewInTurn = turn.some(it => it.kind === 'plan_review');
    if (!reviewInTurn) return 'planning';
  }
  if (mode === 'plan') return 'idle';
  return null;
}

// 200 шутливых вариантов «процесса» — синонимы к думаю/рассуждаю/синтезирую/создаю
const THINKING_VERBS = [
  'Думаю', 'Размышляю', 'Мыслю', 'Соображаю', 'Кумекаю', 'Раздумываю', 'Обмозговываю', 'Прикидываю', 'Мозгую', 'Раскидываю мозгами',
  'Шевелю извилинами', 'Шевелю мозгами', 'Ломаю голову', 'Морщу лоб', 'Напрягаю мозг', 'Включаю мозг', 'Прогреваю мозг', 'Раскочегариваю мозг', 'Кипячу мозг', 'Перегреваю процессор',
  'Рассуждаю', 'Анализирую', 'Взвешиваю', 'Обдумываю', 'Осмысляю', 'Прокручиваю', 'Перебираю варианты', 'Взвешиваю «за» и «против»', 'Прикидываю расклад', 'Прорабатываю',
  'Раскладываю по полочкам', 'Раскладываю по косточкам', 'Вникаю', 'Разбираюсь', 'Докапываюсь до сути', 'Копаю глубже', 'Раскапываю суть', 'Прослеживаю связи', 'Связываю факты', 'Сопоставляю факты',
  'Распутываю клубок', 'Разматываю клубок', 'Раскручиваю логику', 'Выстраиваю логику', 'Делаю выводы', 'Изучаю детали', 'Вглядываюсь в детали', 'Прочёсываю детали', 'Прошерстиваю', 'Свожу концы с концами',
  'Синтезирую', 'Собираю воедино', 'Свожу воедино', 'Связываю мысли', 'Сшиваю идеи', 'Комбинирую', 'Сопоставляю', 'Структурирую', 'Систематизирую', 'Компоную',
  'Соединяю точки', 'Собираю пазл', 'Складываю пазл', 'Собираю мозаику', 'Собираю картину', 'Складываю картину', 'Сплетаю нити', 'Стыкую факты', 'Группирую идеи', 'Упорядочиваю мысли',
  'Перевариваю информацию', 'Усваиваю данные', 'Обрабатываю данные', 'Перемалываю данные', 'Просеиваю идеи', 'Фильтрую мысли', 'Дистиллирую суть', 'Выпариваю суть', 'Сгущаю мысль', 'Концентрируюсь',
  'Создаю', 'Творю', 'Сочиняю', 'Конструирую', 'Мастерю', 'Изобретаю', 'Придумываю', 'Выдумываю', 'Замышляю', 'Задумываю',
  'Проектирую', 'Набрасываю', 'Эскизирую', 'Рисую в уме', 'Вырисовываю', 'Формирую', 'Леплю', 'Ваяю', 'Кую', 'Строю',
  'Возвожу', 'Рождаю идею', 'Высиживаю идею', 'Вынашиваю мысль', 'Стряпаю', 'Замешиваю', 'Завариваю мысль', 'Настаиваю идею', 'Отливаю в форму', 'Шлифую формулировку',
  'Пишу', 'Печатаю мысли', 'Слагаю', 'Складываю слова', 'Подбираю слова', 'Нанизываю слова', 'Плету слова', 'Вью словеса', 'Жонглирую словами', 'Перебираю слова',
  'Колдую', 'Химичу', 'Шаманю', 'Ворожу', 'Чародействую', 'Творю волшебство', 'Творю магию', 'Варю зелье мыслей', 'Варю идею', 'Варю мысли',
  'Медитирую', 'Гружусь', 'Загружаюсь', 'Втыкаю', 'Парю в облаках мыслей', 'Жонглирую идеями', 'Тасую варианты', 'Раскидываю пасьянс', 'Перебираю карты', 'Раскручиваю маховик',
  'Кручу шестерёнки', 'Завожу шестерёнки', 'Запускаю мыслемашину', 'Гоняю байты', 'Перебираю биты', 'Щёлкаю нейронами', 'Искрю нейронами', 'Шуршу нейронами', 'Перебираю нейроны', 'Раскручиваю нейроны',
  'Работаю', 'Тружусь', 'Вкалываю', 'Пыхчу', 'Корплю', 'Колупаюсь', 'Ковыряюсь', 'Копошусь', 'Вожусь', 'Хлопочу',
  'Стараюсь', 'Усердствую', 'Напрягаюсь', 'Кручусь', 'Верчусь', 'Шуршу', 'Бьюсь над задачей', 'Грызу задачу', 'Жую задачу', 'Пыхчу над задачей',
  'Прорабатываю детали', 'Прокапываю', 'Распаковываю', 'Раскручиваю', 'Разгоняюсь', 'Набираю обороты', 'Вхожу в курс', 'Погружаюсь', 'Ныряю глубже', 'Углубляюсь',
  'Прозреваю', 'Дозреваю до ответа', 'Дозреваю', 'Нащупываю ответ', 'Нащупываю мысль', 'Ищу зацепку', 'Ловлю мысль', 'Ловлю идею', 'Ловлю вдохновение', 'Призываю музу',
  'Совещаюсь с музой', 'Подключаю интуицию', 'Сверяюсь с логикой', 'Прикидываю на пальцах', 'Считаю в уме', 'Раскручиваю сюжет', 'Распутываю узел', 'Собираю по крупицам', 'Свожу к сути', 'Финализирую мысль',
];

const pickVerb = (exclude?: string) => {
  let v = THINKING_VERBS[Math.floor(Math.random() * THINKING_VERBS.length)];
  if (exclude && THINKING_VERBS.length > 1) {
    while (v === exclude) v = THINKING_VERBS[Math.floor(Math.random() * THINKING_VERBS.length)];
  }
  return v;
};

// Живой индикатор ожидания: пульс-аватар Claude + «печатная машинка» по синонимам.
// Текст печатается посимвольно с курсором, в конце дописывается «…», держит паузу,
// затем стирается и сменяется новым случайным синонимом.
function WaitingIndicator({ planning }: { planning?: 'planning' | 'replanning' } = {}) {
  const [text, setText] = useState('');
  const reduced = typeof window !== 'undefined'
    && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  // Шутливые глаголы крутятся всегда; в режиме планирования отличается только цвет пульса (индиго)
  const pulseColor = planning ? C.plan : C.accent;

  useEffect(() => {
    // При reduced-motion — статичная подпись без анимации печати
    if (reduced) { setText(pickVerb() + '…'); return; }
    let timer = 0;
    let verb = pickVerb();
    let shown = '';
    let phase: 'typing' | 'pausing' | 'deleting' = 'typing';
    const tick = () => {
      const full = verb + '…';
      if (phase === 'typing') {
        shown = full.slice(0, shown.length + 1);
        setText(shown);
        if (shown.length >= full.length) { phase = 'pausing'; timer = window.setTimeout(tick, 1700); }
        else timer = window.setTimeout(tick, 55 + Math.random() * 50);
      } else if (phase === 'pausing') {
        phase = 'deleting';
        timer = window.setTimeout(tick, 35);
      } else {
        shown = shown.slice(0, -1);
        setText(shown);
        if (shown.length === 0) { verb = pickVerb(verb); phase = 'typing'; timer = window.setTimeout(tick, 260); }
        else timer = window.setTimeout(tick, 26);
      }
    };
    timer = window.setTimeout(tick, 140);
    return () => clearTimeout(timer);
  }, [reduced]);

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
      <span className="cc-pulse-ring" style={{
        width: 22, height: 22, borderRadius: 6, background: pulseColor,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}>
        <span style={{ width: 9, height: 9, borderRadius: '50%', background: C.bgMain }} />
      </span>
      <span style={{ display: 'inline-flex', alignItems: 'baseline', minHeight: 17 }}>
        <span className="cc-shimmer-text" style={{ fontSize: 13, fontWeight: 600, fontFamily: 'inherit' }}>
          {text}
        </span>
        <span style={{
          display: 'inline-block', width: 2, height: '0.95em', marginLeft: 2,
          background: pulseColor, borderRadius: 1, alignSelf: 'center',
          animation: reduced ? 'none' : 'blink 1s step-start infinite',
        }} />
      </span>
    </div>
  );
}

// Общая шапка чата — одинаковая для полноэкранного режима и дока (split снизу).
// onToggleDock задаётся только в доке — добавляет кнопку сворачивания.
interface ChatHeaderBarProps {
  session: Session;
  project: Project;
  online: boolean;
  // Текущий режим из Composer — может отличаться от session.mode до отправки следующего сообщения
  mode: 'auto' | 'plan' | 'ask';
  onOpenSettings: () => void;
  onToggleDock?: () => void;
  isMobile?: boolean;
  onBack?: () => void;
}

function ChatHeaderBar({ session, project, online, mode, onOpenSettings, onToggleDock, isMobile, onBack }: ChatHeaderBarProps) {
  // Блок названия чата + подзаголовок (режим/модель). На мобиле он целиком кликабелен как «назад».
  const titleBlock = (
    <div style={{ minWidth: 0, flex: 1 }}>
      <div style={{ fontSize: 17, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {session.name ?? 'Новый чат'}
      </div>
      <div style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textMuted, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', display: 'flex', alignItems: 'center', gap: 0 }}>
        {/* На мобиле имя проекта не дублируем — оно доступно через кнопку «назад» */}
        {!isMobile && <span>{project.name} · </span>}
        <ModeChip mode={mode} />
        <span style={{ marginLeft: 4 }}>· {modelLabel(session.model)}</span>
      </div>
    </div>
  );
  return (
    <Toolbar isMobile={isMobile}>
      {/* На мобиле стрелка + название кликабельны как «назад» в сайдбар; на десктопе — просто заголовок */}
      {isMobile && onBack
        ? <BackButton onClick={onBack} style={{ flex: 1 }} title="Назад к списку">{titleBlock}</BackButton>
        : titleBlock}
      {online && (
        <ToolbarIconButton onClick={onOpenSettings} title="Настройки чата" isMobile={isMobile}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
          </svg>
        </ToolbarIconButton>
      )}
      {onToggleDock && (
        <ToolbarIconButton onClick={onToggleDock} title="Свернуть чат" isMobile={isMobile}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M6 9l6 6 6-6"/></svg>
        </ToolbarIconButton>
      )}
    </Toolbar>
  );
}

// Заглушка вместо Composer в офлайн-режиме
function OfflineComposerStub() {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
      padding: '14px', borderRadius: 14, background: C.bgPanel,
      border: '1px solid #E0D8CC', color: C.textMuted, fontSize: 13, fontWeight: 600,
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

// Контекст текущего проекта — для резолва локальных путей картинок в сообщениях
const ChatProjectContext = createContext<{ id: string; rootPath: string } | null>(null);

// Картинка из markdown: внешние URL (http/https/data) — напрямую; локальный путь файла
// проекта (например, картинка, скачанная Claude) — грузим через API и показываем как data-URL.
function ChatImage({ src, alt }: { src?: string; alt?: string }) {
  const project = useContext(ChatProjectContext);
  const isRemote = !!src && /^(https?:|data:)/i.test(src);
  const [resolved, setResolved] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!src || isRemote || !project) return;
    let cancelled = false;
    // Путь относительно корня проекта (Claude мог дать абсолютный путь внутри проекта)
    let rel = src.replace(/\\/g, '/');
    const root = project.rootPath.replace(/\\/g, '/');
    if (rel.toLowerCase().startsWith(root.toLowerCase())) rel = rel.slice(root.length);
    rel = rel.replace(/^\/+/, '');
    api.files.getContent(project.id, rel)
      .then(r => {
        if (cancelled) return;
        if (r.isImage && r.base64) setResolved(`data:${r.mimeType ?? 'image/png'};base64,${r.base64}`);
        else setFailed(true);
      })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, [src, isRemote, project]);

  const finalSrc = isRemote ? src : resolved;

  if (failed) return <span style={{ fontSize: 13, color: C.textMuted }}>🖼 {alt || src}</span>;
  if (!finalSrc) return <span style={{ fontSize: 13, color: C.textMuted }}>Загрузка изображения…</span>;

  return (
    <a href={finalSrc} target="_blank" rel="noopener noreferrer" style={{ display: 'block', margin: '6px 0' }}>
      <img src={finalSrc} alt={alt ?? ''} loading="lazy" onError={() => setFailed(true)}
        style={{ maxWidth: '100%', height: 'auto', display: 'block', borderRadius: 8, border: `1px solid ${C.border}` }} />
    </a>
  );
}

// Рендер текста Claude с поддержкой Markdown
function MarkdownContent({ text }: { text: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      urlTransform={(url, key) => (key === 'src' ? url : defaultUrlTransform(url))}
      components={{
        p: ({ children }) => (
          <p style={{ margin: '0 0 8px 0', lineHeight: 1.6 }}>{children}</p>
        ),
        h1: ({ children }) => (
          <h1 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 20, fontWeight: 600, margin: '10px 0 6px', color: C.textHeading, letterSpacing: '-0.01em' }}>{children}</h1>
        ),
        h2: ({ children }) => (
          <h2 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 17, fontWeight: 600, margin: '8px 0 5px', color: C.textHeading, letterSpacing: '-0.01em' }}>{children}</h2>
        ),
        h3: ({ children }) => (
          <h3 style={{ fontFamily: '"PT Serif", Georgia, serif', fontSize: 15, fontWeight: 600, margin: '6px 0 4px', color: C.textHeading }}>{children}</h3>
        ),
        pre: ({ children }) => <>{children}</>,
        code: ({ className, children, ...props }) => {
          const language = /language-(\w+)/.exec(className || '')?.[1];
          const text = String(children).replace(/\n$/, '');
          if (language) {
            return (
              <SyntaxHighlighter
                language={language}
                style={oneDark}
                customStyle={{ borderRadius: 8, fontSize: 12.5, margin: '6px 0', padding: '10px 14px', fontFamily: FONT.mono, overflowX: 'auto' }}
              >
                {text}
              </SyntaxHighlighter>
            );
          }
          if (text.includes('\n')) {
            // Код без указания языка — на светлой панели вывода (лёгкий тёплый фон вместо тёмного)
            return (
              <pre style={{ background: C.outputBg, border: `1px solid ${C.outputBorder}`, borderRadius: 8, padding: '10px 14px', margin: '6px 0', overflowX: 'auto' }}>
                <code style={{ fontFamily: FONT.mono, fontSize: 12.5, color: C.textPrimary, lineHeight: 1.5 }} {...props}>{text}</code>
              </pre>
            );
          }
          return (
            <code style={{ fontFamily: FONT.mono, background: '#EDE7DA', padding: '1px 5px', borderRadius: 4, fontSize: '0.88em', color: '#5A3322' }} {...props}>
              {children}
            </code>
          );
        },
        ul: ({ children }) => <ul style={{ paddingLeft: 18, margin: '2px 0 8px' }}>{children}</ul>,
        ol: ({ children }) => <ol style={{ paddingLeft: 18, margin: '2px 0 8px' }}>{children}</ol>,
        li: ({ children }) => <li style={{ marginBottom: 3, lineHeight: 1.6 }}>{children}</li>,
        blockquote: ({ children }) => (
          <blockquote style={{ borderLeft: `3px solid ${C.accent}`, paddingLeft: 12, margin: '6px 0', color: C.textSecondary, fontStyle: 'italic' }}>
            {children}
          </blockquote>
        ),
        a: ({ children, href }) => (
          <a href={href} style={{ color: C.accent, textDecoration: 'underline' }} target="_blank" rel="noopener noreferrer">
            {children}
          </a>
        ),
        // Картинки из markdown: внешние URL — напрямую, локальные пути файлов проекта — через API
        img: ({ src, alt }) => <ChatImage src={typeof src === 'string' ? src : undefined} alt={alt ?? ''} />,
        strong: ({ children }) => <strong style={{ fontWeight: 600 }}>{children}</strong>,
        hr: () => <hr style={{ border: 'none', borderTop: `1px solid ${C.border}`, margin: '10px 0' }} />,
        table: ({ children }) => (
          <div style={{ overflowX: 'auto', margin: '6px 0' }}>
            <table style={{ borderCollapse: 'collapse', minWidth: '100%', fontSize: 13 }}>{children}</table>
          </div>
        ),
        th: ({ children }) => (
          <th style={{ border: `1px solid ${C.border}`, padding: '6px 10px', background: '#EDE7DA', fontWeight: 600, textAlign: 'left' }}>{children}</th>
        ),
        td: ({ children }) => (
          <td style={{ border: `1px solid ${C.border}`, padding: '6px 10px' }}>{children}</td>
        ),
      }}
    >
      {text}
    </ReactMarkdown>
  );
}

// Чипы-подсказки для empty state
const HINTS = ['Объясни структуру проекта', 'Найди и почини падающие тесты'];

// Модальный пикер вложений
interface AttachPickerProps {
  projectId: string;
  onPick: (path: string) => void;
  onClose: () => void;
}

function AttachPicker({ projectId, onPick, onClose }: AttachPickerProps) {
  const [files, setFiles] = useState<FileEntry[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.files.list(projectId)
      .then(setFiles)
      .finally(() => setLoading(false));
  }, [projectId]);

  const visibleFiles = files.filter(f => !f.isDirectory);

  return (
    <Modal
      title="Прикрепить файл"
      width={MODAL_W.form}
      onClose={onClose}
      // Контент — это сам список со своими отступами; убираем стандартный паддинг карточки
      cardStyle={{ maxHeight: '70vh' }}
    >
      <div style={{ margin: '-4px -8px', maxHeight: '52vh', overflowY: 'auto' }}>
        {loading && (
          <div style={{ padding: 16, color: C.textMuted, fontSize: 13, textAlign: 'center' }}>
            Загрузка…
          </div>
        )}
        {!loading && visibleFiles.map(f => (
          <div
            key={f.path}
            onClick={() => { onPick(f.path); onClose(); }}
            style={{
              padding: '10px 12px', cursor: 'pointer', fontSize: 13, borderRadius: R.md,
              color: C.textPrimary, display: 'flex', alignItems: 'center', gap: 8,
              fontFamily: FONT.mono,
            }}
            onMouseEnter={e => (e.currentTarget.style.background = C.accentLight)}
            onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
          >
            <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {f.path}
            </span>
          </div>
        ))}
        {!loading && visibleFiles.length === 0 && (
          <div style={{ padding: 16, color: C.textMuted, fontSize: 13, textAlign: 'center' }}>
            Файлы не найдены
          </div>
        )}
      </div>
    </Modal>
  );
}

export function ChatPanel({ session, project, onOpenFile, pendingMessage, onPendingMessageSent, onSessionUpdated, dockMode, onToggleDock, isMobile, onBack }: Props) {
  const { items, isWaiting, isJoined, send, allowPermission, denyPermission, allowAlways, answerQuestion, respondPlan, interrupt, toggleThinking } = useSession(session.id, project.id);
  const online = useOnline();
  const [mode, setMode] = useState<'auto' | 'plan' | 'ask'>(session.mode);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const [showAttachPicker, setShowAttachPicker] = useState(false);
  const [showEdit, setShowEdit] = useState(false);
  const [miniText, setMiniText] = useState('');
  const bottomRef = useRef<HTMLDivElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  // Плавающий composer переменной высоты — измеряем, чтобы лента упиралась ровно под него
  const composerWrapRef = useRef<HTMLDivElement>(null);
  const [composerH, setComposerH] = useState(96);
  // Прилипание к низу: автоскролл при новых сообщениях только если пользователь уже внизу
  const atBottomRef = useRef(true);
  // Показывать плавающую кнопку «вниз», когда пользователь отлистал вверх
  const [showScrollDown, setShowScrollDown] = useState(false);
  // Контекст проекта для резолва локальных путей картинок в сообщениях
  const projectCtx = useMemo(() => ({ id: project.id, rootPath: project.rootPath }), [project.id, project.rootPath]);
  const pendingRef = useRef<string | undefined>(pendingMessage);
  pendingRef.current = pendingMessage;

  // Измеряем высоту плавающего composer → задаём нижний отступ ленты (упор ровно под него)
  useEffect(() => {
    const el = composerWrapRef.current;
    if (!el) return;
    const update = () => setComposerH(el.offsetHeight);
    update();
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => ro.disconnect();
  }, [online, dockMode]);

  const handleMessagesScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    atBottomRef.current = atBottom;
    setShowScrollDown(!atBottom);
  };

  // Программный скролл в конец ленты (клик по плавающей кнопке)
  const scrollToBottom = () => {
    atBottomRef.current = true;
    setShowScrollDown(false);
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  useEffect(() => {
    // Прокручиваем вниз только если пользователь у нижней точки (не отрываем его от истории)
    if (atBottomRef.current) {
      bottomRef.current?.scrollIntoView({ behavior: 'instant' });
      setShowScrollDown(false);
    } else {
      // пришёл новый контент, а пользователь читает выше — подсветим кнопку «вниз»
      setShowScrollDown(true);
    }
  }, [items]);

  // При раскрытии дока — моментально проматываем в конец и возобновляем прилипание
  useEffect(() => {
    if (dockMode === 'expanded') {
      atBottomRef.current = true;
      setShowScrollDown(false);
      bottomRef.current?.scrollIntoView({ behavior: 'instant' });
    }
  }, [dockMode]);

  // Автоотправка первого сообщения сразу после присоединения к сессии
  useEffect(() => {
    if (isJoined && pendingRef.current) {
      const msg = pendingRef.current;
      pendingRef.current = undefined;
      onPendingMessageSent?.();
      send(msg, [], mode);
    }
  }, [isJoined]);

  const handleSend = async (text: string) => {
    if (!text.trim() && attachedFiles.length === 0) return;
    const paths = [...attachedFiles];
    setAttachedFiles([]);
    atBottomRef.current = true; // своё сообщение — прыгаем вниз и снова прилипаем
    await send(text, paths, mode);
  };

  const handleHint = (hint: string) => {
    atBottomRef.current = true;
    send(hint, [], mode);
  };

  const handleRetry = () => {
    const lastUser = [...items].reverse().find(it => it.kind === 'user_message');
    if (lastUser && lastUser.kind === 'user_message') { atBottomRef.current = true; send(lastUser.text, lastUser.attachedPaths ?? [], mode); }
  };

  // Режим «План» — персистентный: после одобрения остаёмся в нём (следующие задачи тоже
  // планируются). Исполнение именно этого плана гарантирует backend (один ход без plan-режима).
  const handleRespondPlan = (requestId: string, approve: boolean, feedback?: string) => {
    respondPlan(requestId, approve, feedback);
  };

  // Индекс последнего result — у него показываем плашку токенов/времени, у прошлых скрываем
  const lastResultIndex = items.reduce((acc, it, i) => (it.kind === 'result' ? i : acc), -1);

  // Фаза режима «План» (для контекстного индикатора и подписи WaitingIndicator)
  const planPhase = derivePlanPhase(items, mode, isWaiting);
  const planningKind = planPhase === 'planning' ? 'planning' : planPhase === 'replanning' ? 'replanning' : undefined;

  // Индикатор работы: показываем пока после последнего user_message нет result/error
  const lastUserMsgIdx = items.reduce((acc, it, i) => it.kind === 'user_message' ? i : acc, -1);
  const turnSettled = lastUserMsgIdx < 0 ||
    items.slice(lastUserMsgIdx + 1).some(it => it.kind === 'result' || it.kind === 'error' || it.kind === 'session_ended');

  // Номера версий plan_review: счётчик с последнего user_message включительно (1, 2, …).
  // Также помечаем, был ли в текущем ходе отклонённый план — тогда показываем бейдж даже для v1.
  const planVersions = useMemo(() => {
    let counter = 0;
    let rejectedSeen = false;
    const result = new Map<number, { version: number; hadRejected: boolean }>();
    items.forEach((it, i) => {
      if (it.kind === 'user_message') { counter = 0; rejectedSeen = false; }
      if (it.kind === 'plan_review') {
        counter++;
        result.set(i, { version: counter, hadRejected: rejectedSeen });
        if (it.resolved && it.approved === false) rejectedSeen = true;
      }
    });
    return result;
  }, [items]);

  // Индекс последнего одобренного plan_review и конец «зоны реализации» (до следующего
  // user_message или result) — действия в этой зоне оборачиваем success-коннектором.
  const execZone = useMemo(() => {
    let approvedIdx = -1;
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind === 'plan_review' && it.resolved && it.approved) { approvedIdx = i; break; }
      if (it.kind === 'user_message') break;
    }
    if (approvedIdx < 0) return null;
    let endIdx = items.length;
    for (let i = approvedIdx + 1; i < items.length; i++) {
      if (items[i].kind === 'user_message' || items[i].kind === 'result') { endIdx = i; break; }
    }
    return { start: approvedIdx + 1, end: endIdx };
  }, [items]);

  // Индекс последнего одобренного плана во всей ленте — только у него показываем
  // подсказку «Перейти в Авто» (у старых одобренных планов она неактуальна)
  const lastApprovedPlanIdx = useMemo(() => {
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind === 'plan_review' && it.resolved && it.approved) return i;
    }
    return -1;
  }, [items]);

  // Единый рендер одного элемента ленты (используется в основном рендере и в доке)
  const renderItem = (item: ChatItem, i: number) => (
    <ChatItemView
      key={i}
      item={item}
      index={i}
      online={online}
      streaming={isWaiting && i === items.length - 1}
      isLastResult={i === lastResultIndex}
      onToggleThinking={toggleThinking}
      onAllowPermission={allowPermission}
      onDenyPermission={denyPermission}
      onAllowAlways={allowAlways}
      onAnswerQuestion={answerQuestion}
      onRespondPlan={handleRespondPlan}
      planVersion={planVersions.get(i)?.version}
      planShowBadge={!!planVersions.get(i) && (planVersions.get(i)!.version > 1 || planVersions.get(i)!.hadRejected)}
      planShowSwitch={i === lastApprovedPlanIdx && mode === 'plan'}
      onSwitchToAuto={() => setMode('auto')}
      onOpenFile={onOpenFile}
      onRevert={path => api.files.revert(project.id, path)}
      onRetry={handleRetry}
    />
  );

  // Блок действий: подряд идущие карточки инструментов + изменения файлов объединяем
  // в один контур (внешние линии сверху/снизу + разделители между соседями), чтобы
  // file_changed между инструментами не разрывал стопку. Контур рисуем только если в
  // блоке есть хотя бы один инструмент; одиночные file_changed остаются обычными карточками.
  const renderItems = () => {
    const isTool = (it: ChatItem) => it.kind === 'tool_use' && it.name !== 'TodoWrite' && !it.parentToolUseId && it.name.toLowerCase() !== 'workflow';
    const inBlock = (it: ChatItem) => isTool(it) || it.kind === 'file_changed';
    // Строим карту дочерних tool_use для Workflow-блоков
    const childrenByParentId = new Map<string, ToolUseItem[]>();
    for (const it of items) {
      if (it.kind === 'tool_use' && it.parentToolUseId) {
        const arr = childrenByParentId.get(it.parentToolUseId) ?? [];
        arr.push(it as ToolUseItem);
        childrenByParentId.set(it.parentToolUseId, arr);
      }
    }
    // ID субагентов и их инструментов, которые рендерятся внутри WorkflowBlockView
    const suppressedByWorkflow = new Set<string>();
    for (const it of items) {
      if (it.kind !== 'tool_use' || it.name.toLowerCase() !== 'workflow') continue;
      for (const agent of (childrenByParentId.get(it.id) ?? [])) {
        suppressedByWorkflow.add(agent.id);
        for (const tool of (childrenByParentId.get(agent.id) ?? [])) suppressedByWorkflow.add(tool.id);
      }
    }
    // Дочерние вызовы субагента (не-Workflow) — рисуем единой непрерывной линией-коннектором слева
    const isSubTool = (it: ChatItem) => it.kind === 'tool_use' && !!it.parentToolUseId && !suppressedByWorkflow.has(it.id);
    // Узлы ленты с пометкой стартового индекса — нужно для обёртки success-коннектором
    const nodes: Array<{ node: React.ReactNode; start: number }> = [];
    const pushNode = (node: React.ReactNode, start: number) => nodes.push({ node, start });
    let i = 0;
    let prevNodeWasBlock = false;
    while (i < items.length) {
      // Workflow-блок рендерим специальным компонентом
      if (items[i].kind === 'tool_use' && (items[i] as ToolUseItem).name.toLowerCase() === 'workflow') {
        const wf = items[i] as ToolUseItem;
        pushNode(<WorkflowBlockView key={`wf-${i}`} workflow={wf} agents={childrenByParentId.get(wf.id) ?? []} childrenByParentId={childrenByParentId} />, i);
        i++; prevNodeWasBlock = false; continue;
      }
      // Субагенты Workflow и их инструменты рендерятся внутри WorkflowBlockView
      if (items[i].kind === 'tool_use' && suppressedByWorkflow.has((items[i] as ToolUseItem).id)) {
        i++; continue;
      }
      if (isSubTool(items[i])) {
        const start = i;
        const sub: Array<[ChatItem, number]> = [];
        while (i < items.length && isSubTool(items[i])) { sub.push([items[i], i]); i++; }
        // Один контейнер с borderLeft на всю стопку дочерних → линия не прерывается gap'ом ленты
        const subDiv = (
          <div key={`sub-${start}`} style={{ marginLeft: 8, paddingLeft: 14, borderLeft: `2px solid ${C.border}` }}>
            {sub.map(([it, idx], gi) => (
              <div key={idx} style={gi === 0 ? undefined : { borderTop: `1px solid ${C.bgInset}` }}>{renderItem(it, idx)}</div>
            ))}
          </div>
        );
        if (prevNodeWasBlock && nodes.length > 0) {
          // Прижать к шапке: объединяем дочерние инструменты с предшествующим блоком без gap
          const prev = nodes[nodes.length - 1];
          nodes[nodes.length - 1] = {
            node: <Fragment key={`merged-${prev.start}`}>{prev.node}{subDiv}</Fragment>,
            start: prev.start,
          };
        } else {
          pushNode(subDiv, start);
        }
        prevNodeWasBlock = false;
      } else if (inBlock(items[i])) {
        const start = i;
        const slice: Array<[ChatItem, number]> = [];
        while (i < items.length && inBlock(items[i])) { slice.push([items[i], i]); i++; }
        // Один контур: инструменты и изменения файлов — компактными строками (в т.ч. одиночные)
        pushNode(
          <div key={`grp-${start}`} style={{ borderTop: `1px solid ${C.bgInset}`, borderBottom: `1px solid ${C.bgInset}` }}>
            {slice.map(([it, idx], gi) => (
              <div key={idx} style={gi === 0 ? undefined : { borderTop: `1px solid ${C.bgInset}` }}>
                {it.kind === 'file_changed'
                  ? <FileChangedRow item={it} online={online} onOpenFile={onOpenFile} onRevert={path => api.files.revert(project.id, path)} />
                  : renderItem(it, idx)}
              </div>
            ))}
          </div>,
          start
        );
        prevNodeWasBlock = true;
      } else {
        pushNode(renderItem(items[i], i), i); i++;
        prevNodeWasBlock = false;
      }
    }

    // success-коннектор: непрерывные узлы из «зоны реализации» (после одобренного плана)
    // оборачиваем в одну левую зелёную линию — «эти правки реализуют план».
    if (!execZone) return nodes.map(n => n.node);
    const result: React.ReactNode[] = [];
    let j = 0;
    while (j < nodes.length) {
      const inZone = (n: { start: number }) => n.start >= execZone.start && n.start < execZone.end;
      if (inZone(nodes[j])) {
        const group: React.ReactNode[] = [];
        const groupStart = nodes[j].start;
        while (j < nodes.length && inZone(nodes[j])) { group.push(nodes[j].node); j++; }
        result.push(
          <div key={`exec-${groupStart}`} style={{ marginLeft: 8, paddingLeft: 14, borderLeft: `3px solid ${C.success}`, display: 'flex', flexDirection: 'column', gap: 14, marginTop: -14 }}>
            {group}
          </div>
        );
      } else {
        result.push(nodes[j].node); j++;
      }
    }
    return result;
  };

  // Dock: свёрнутая полоска
  if (dockMode === 'collapsed') {
    // Свежий ответ Claude (а не эхо запроса пользователя) — для превью в свёрнутом доке
    const lastAnswer = (() => {
      for (let i = items.length - 1; i >= 0; i--) {
        if (items[i].kind === 'text') return (items[i] as Extract<ChatItem, { kind: 'text' }>).text;
      }
      return '';
    })();

    const handleMiniSend = () => {
      if (!miniText.trim() || isWaiting || !online) return;
      atBottomRef.current = true;
      send(miniText, [], mode);
      setMiniText('');
    };

    return (
      <Toolbar noBorder style={{ height: 56, gap: 10 }}>
        <div style={{ width: 28, height: 28, borderRadius: '50%', background: C.accent, flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <div style={{ width: 10, height: 10, borderRadius: '50%', background: C.bgMain }} />
        </div>
        <span style={{ flex: 1, minWidth: 0, fontSize: 13, color: isWaiting ? C.textMuted : C.textSecondary, fontStyle: isWaiting ? 'italic' : 'normal', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {isWaiting ? 'Claude печатает…' : (lastAnswer || session.name || 'Новый чат')}
        </span>
        <input
          value={miniText}
          onChange={e => setMiniText(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleMiniSend(); } }}
          placeholder={online ? 'Ответить…' : 'Офлайн'}
          disabled={!online}
          style={{ width: 180, padding: '6px 10px', border: `1px solid ${C.border}`, borderRadius: 8, fontSize: 13, background: C.bgMain, outline: 'none', fontFamily: 'inherit', color: C.textHeading }}
        />
        <button
          onClick={handleMiniSend}
          disabled={!miniText.trim() || isWaiting || !online}
          style={{ width: 28, height: 28, borderRadius: '50%', border: 'none', cursor: miniText.trim() && !isWaiting && online ? 'pointer' : 'default', background: miniText.trim() && !isWaiting && online ? C.accent : C.divider, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}
        >
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="#FFF" strokeWidth="2.5" strokeLinecap="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg>
        </button>
        <ToolbarIconButton onClick={onToggleDock} title="Развернуть чат">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M18 15l-6-6-6 6"/></svg>
        </ToolbarIconButton>
      </Toolbar>
    );
  }

  // Dock: развёрнутая панель
  if (dockMode === 'expanded') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgMain, position: 'relative' }}>
        <ChatHeaderBar
          session={session}
          project={project}
          online={online}
          mode={mode}
          onOpenSettings={() => setShowEdit(true)}
          onToggleDock={onToggleDock}
        />

        {/* Сообщения (нижний отступ = высота плавающего composer) */}
        <div ref={scrollRef} onScroll={handleMessagesScroll} style={{ flex: 1, overflowY: 'auto', overflowX: 'hidden', paddingTop: 12, paddingLeft: 16, paddingRight: 16, paddingBottom: composerH + 8 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <ChatProjectContext.Provider value={projectCtx}>{renderItems()}</ChatProjectContext.Provider>
            {online && !turnSettled && !items.some(it => (it.kind === 'permission_request' || it.kind === 'plan_review') && !it.resolved) && (
              <WaitingIndicator planning={planningKind} />
            )}
            <div ref={bottomRef} />
          </div>
        </div>

        {/* Composer — плавающий над доком, контент виден под ним */}
        <div ref={composerWrapRef} style={{ position: 'absolute', left: 0, right: 0, bottom: 0, padding: '0 16px 12px', pointerEvents: 'none' }}>
          <div style={{ width: '100%', pointerEvents: 'auto', borderRadius: 14, boxShadow: '0 6px 22px rgba(60,50,35,0.13)' }}>
            {online ? (
              <Composer
                onSend={handleSend}
                onStop={interrupt}
                onAttach={() => setShowAttachPicker(true)}
                isGenerating={isWaiting}
                mode={mode}
                onModeChange={setMode}
                attachments={attachedFiles}
                onRemoveAttachment={path => setAttachedFiles(prev => prev.filter(p => p !== path))}
              />
            ) : <OfflineComposerStub />}
          </div>
        </div>

        {showAttachPicker && (
          <AttachPicker
            projectId={project.id}
            onPick={path => setAttachedFiles(prev => prev.includes(path) ? prev : [...prev, path])}
            onClose={() => setShowAttachPicker(false)}
          />
        )}

        {showEdit && (
          <EditSessionDialog
            session={session}
            onSaved={s => onSessionUpdated?.(s)}
            onClose={() => setShowEdit(false)}
          />
        )}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', position: 'relative', background: C.bgMain }}>
      <ChatHeaderBar
        session={session}
        project={project}
        online={online}
        mode={mode}
        onOpenSettings={() => setShowEdit(true)}
        isMobile={isMobile}
        onBack={onBack}
      />

      {/* Сообщения (нижний отступ = высота плавающего composer + зазор) */}
      <div ref={scrollRef} onScroll={handleMessagesScroll} style={{ flex: 1, overflowY: 'auto', overflowX: 'hidden', paddingTop: isMobile ? 16 : 20, paddingLeft: isMobile ? 12 : 24, paddingRight: isMobile ? 12 : 24, paddingBottom: composerH + 8 }}><div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
        {/* Empty state */}
        {items.length === 0 && online && (
          <div style={{
            flex: 1, display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center',
            gap: 12, paddingTop: 40,
          }}>
            {/* Логотип */}
            <div style={{
              width: 46, height: 46, borderRadius: 13, background: C.accent,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}>
              <div style={{
                width: 22, height: 22, borderRadius: '50%', background: '#FFF',
              }} />
            </div>

            {/* Заголовок */}
            <div style={{
              fontFamily: '"PT Serif", Georgia, serif',
              fontWeight: 500, fontSize: 20, color: C.textHeading, letterSpacing: '-0.01em',
            }}>
              Чем помочь?
            </div>

            {/* Подзаголовок */}
            <div style={{ fontSize: 13, color: '#8A8070', textAlign: 'center' }}>
              Опишите задачу или начните с подсказки
            </div>

            {/* Чипы */}
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center', marginTop: 4 }}>
              {HINTS.map(hint => (
                <button
                  key={hint}
                  onClick={() => handleHint(hint)}
                  style={{
                    background: '#FFF', border: `1px solid ${C.borderLight}`,
                    borderRadius: 10, padding: '9px 12px',
                    fontSize: 13, color: C.textPrimary, cursor: 'pointer',
                    fontFamily: 'inherit',
                  }}
                  onMouseEnter={e => (e.currentTarget.style.background = C.accentLight)}
                  onMouseLeave={e => (e.currentTarget.style.background = '#FFF')}
                >
                  {hint}
                </button>
              ))}
            </div>
          </div>
        )}

        <ChatProjectContext.Provider value={projectCtx}>{renderItems()}</ChatProjectContext.Provider>

        {(isWaiting || items.some(it => it.kind === 'tool_use' && it.result === undefined)) && !items.some(it => (it.kind === 'permission_request' || it.kind === 'plan_review') && !it.resolved) && (
          <WaitingIndicator planning={planningKind} />
        )}
        <div ref={bottomRef} />
      </div></div>

      {/* Плавающая кнопка «вниз» — появляется, когда лента отлистана вверх */}
      {showScrollDown && items.length > 0 && (
        <button
          onClick={scrollToBottom}
          title="Вниз чата"
          style={{
            position: 'absolute', right: isMobile ? 16 : 24, bottom: composerH + 14,
            width: 44, height: 44, borderRadius: '50%', border: 'none',
            background: C.accent, color: C.onAccent, cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            boxShadow: SHADOW.fab, zIndex: 15,
          }}
        >
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 5v14" /><path d="m19 12-7 7-7-7" />
          </svg>
        </button>
      )}

      {/* Composer — плавающий над лентой; фон прозрачный, контент виден под/вокруг него */}
      <div ref={composerWrapRef} style={{
        position: 'absolute', left: 0, right: 0, bottom: 0,
        padding: isMobile ? '0 12px 12px' : '0 24px 18px',
        pointerEvents: 'none',
      }}>
        <div style={{ maxWidth: 760, margin: '0 auto', pointerEvents: 'auto', borderRadius: 14, boxShadow: '0 6px 22px rgba(60,50,35,0.13)' }}>
          {online ? (
            <Composer
              onSend={handleSend}
              onStop={interrupt}
              onAttach={() => setShowAttachPicker(true)}
              isGenerating={isWaiting}
              mode={mode}
              onModeChange={setMode}
              attachments={attachedFiles}
              onRemoveAttachment={path => setAttachedFiles(prev => prev.filter(p => p !== path))}
              isMobile={isMobile}
            />
          ) : <OfflineComposerStub />}
        </div>
      </div>

      {/* Пикер вложений */}
      {showAttachPicker && (
        <AttachPicker
          projectId={project.id}
          onPick={path => setAttachedFiles(prev => prev.includes(path) ? prev : [...prev, path])}
          onClose={() => setShowAttachPicker(false)}
        />
      )}

      {/* Настройки чата */}
      {showEdit && (
        <EditSessionDialog
          session={session}
          onSaved={s => onSessionUpdated?.(s)}
          onClose={() => setShowEdit(false)}
        />
      )}
    </div>
  );
}

// Карточка плана задач (TodoWrite) — закреплённый чек-лист с прогрессом
interface TodoEntry { content: string; status: string; activeForm?: string }

function TodoPlanView({ input }: { input: unknown }) {
  const todos = (() => {
    const t = (input as { todos?: unknown } | null)?.todos;
    return Array.isArray(t) ? (t as TodoEntry[]) : [];
  })();
  if (todos.length === 0) return null;
  const done = todos.filter(t => t.status === 'completed').length;

  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderRadius: 12, background: C.bgWhite,
      overflow: 'hidden', boxShadow: '0 2px 8px rgba(60,50,35,0.04)',
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '10px 13px', borderBottom: '1px solid #EFE9DD' }}>
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#D97757" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M9 11l3 3L22 4" /><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" />
        </svg>
        <span style={{ fontFamily: FONT.serif, fontSize: 14, fontWeight: 700, color: C.textHeading }}>План</span>
        <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>
          {done}/{todos.length}
        </span>
      </div>
      <div style={{ padding: '7px 13px 10px', display: 'flex', flexDirection: 'column', gap: 1 }}>
        {todos.map((t, i) => {
          const isDone = t.status === 'completed';
          const isActive = t.status === 'in_progress';
          const label = isActive && t.activeForm ? t.activeForm : t.content;
          return (
            <div key={i} style={{ display: 'flex', alignItems: 'flex-start', gap: 9, padding: '4px 0' }}>
              <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}>
                {isDone ? (
                  <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
                    <circle cx="8" cy="8" r="8" fill="#5E8B4E" />
                    <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                ) : isActive ? (
                  <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
                    <circle cx="8" cy="8" r="7" fill="#D97757" />
                    <circle cx="8" cy="8" r="2.6" fill="#FBF1EA" />
                  </svg>
                ) : (
                  <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
                    <circle cx="8" cy="8" r="6.5" stroke="#C9BFAD" strokeWidth="1.5" />
                  </svg>
                )}
              </span>
              <span style={{
                fontSize: 13, lineHeight: 1.4,
                color: isDone ? C.textMuted : isActive ? C.textHeading : C.textSecondary,
                textDecoration: isDone ? 'line-through' : 'none',
                fontWeight: isActive ? 600 : 400,
              }}>
                {label}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

// Иконка и цвет по типу инструмента — чтобы read/edit/bash/web/mcp различались с первого взгляда
function toolMeta(name: string): { color: string; icon: React.ReactNode } {
  const n = name.toLowerCase();
  const svg = (children: React.ReactNode) => (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{children}</svg>
  );
  if (n.startsWith('mcp__'))
    return { color: '#8E4A82', icon: svg(<><path d="M9 2v6M15 2v6" /><path d="M6 8h12v3a6 6 0 0 1-12 0z" /><path d="M12 17v5" /></>) };
  if (['read', 'glob', 'grep', 'ls'].includes(n))
    return { color: C.info, icon: svg(<><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" /><circle cx="12" cy="12" r="3" /></>) };
  if (['edit', 'write', 'multiedit', 'notebookedit'].includes(n))
    return { color: '#C2693B', icon: svg(<><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></>) };
  if (n.startsWith('bash') || n.includes('shell'))
    return { color: C.success, icon: svg(<><polyline points="4 17 10 11 4 5" /><line x1="12" y1="19" x2="20" y2="19" /></>) };
  if (['websearch', 'webfetch'].includes(n))
    return { color: '#8E4A82', icon: svg(<><circle cx="12" cy="12" r="10" /><line x1="2" y1="12" x2="22" y2="12" /><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" /></>) };
  if (n === 'task')
    return { color: '#B05C38', icon: svg(<><circle cx="12" cy="8" r="4" /><path d="M4 21a8 8 0 0 1 16 0" /></>) };
  if (n === 'skill')
    return { color: '#8E4A82', icon: svg(<><path d="M12 3l1.9 5.2L19 10l-5.1 1.8L12 17l-1.9-5.2L5 10l5.1-1.8z" /><path d="M19 15l.7 2 2 .7-2 .7-.7 2-.7-2-2-.7 2-.7z" /></>) };
  return { color: C.info, icon: svg(<path d="M14.7 6.3a4 4 0 0 0-5.4 5.4l-6 6 2 2 6-6a4 4 0 0 0 5.4-5.4l-2.3 2.3-2-2 2.3-2.3z" />) };
}

// Русские названия инструментов для ленты чата
const TOOL_LABELS: Record<string, string> = {
  read: 'Чтение', edit: 'Правка', write: 'Запись', multiedit: 'Правки',
  notebookedit: 'Правка ноутбука', bash: 'Команда', bashoutput: 'Вывод команды',
  glob: 'Поиск файлов', grep: 'Поиск', ls: 'Список', task: 'Субагент', agent: 'Субагент',
  websearch: 'Веб-поиск', webfetch: 'Загрузка страницы', skill: 'Навык',
  todowrite: 'План задач', exitplanmode: 'План', toolsearch: 'Поиск инструментов',
  killshell: 'Остановка команды',
};
// Имя инструмента для показа: MCP → «server · tool», известные — по-русски, прочее — как есть
function toolLabel(name: string): string {
  if (name.startsWith('mcp__')) return name.slice(5).replace(/__/g, ' · ');
  return TOOL_LABELS[name.toLowerCase()] ?? name;
}

// Склонение слова «действие»
function toolWord(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'действие';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'действия';
  return 'действий';
}

// Путь относительно корня проекта (везде в чате показываем относительные пути).
// Вне проекта — возвращаем как есть. Регистронезависимо (Windows: C:\ vs c:\).
function relPath(p: string, root?: string | null): string {
  if (!root || !p) return p;
  const np = p.replace(/\\/g, '/');
  const nr = root.replace(/\\/g, '/').replace(/\/+$/, '');
  if (np.toLowerCase() === nr.toLowerCase()) return '.';
  if (np.toLowerCase().startsWith(nr.toLowerCase() + '/')) return np.slice(nr.length + 1);
  return p;
}

// Делает пути относительными в произвольном тексте (командах, выводе, плане):
// «<root>\sub\file» → «sub\file», голый «<root>» → «.». Учитывает оба разделителя и регистр (Windows).
function stripRoot(text: string, root?: string | null): string {
  if (!root || !text) return text;
  const nr = root.replace(/[\\/]+$/, '');
  const variants = Array.from(new Set([nr, nr.replace(/\\/g, '/'), nr.replace(/\//g, '\\')]));
  let out = text;
  for (const v of variants) {
    if (!v) continue;
    const esc = v.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    // после корня: разделитель + остаток пути (до пробела/кавычки) → остаток; иначе корень → «.»
    out = out.replace(new RegExp(esc + '([\\\\/]([^\\s"\'`]*))?', 'gi'), (_m, _g1, rest) => (rest ? rest : '.'));
  }
  return out;
}

// Inline-diff для Edit/MultiEdit/Write: удалённые строки красным, добавленные зелёным
function DiffBody({ hunks }: { hunks: Array<{ old?: string; new?: string }> }) {
  const MAX = 240;
  let count = 0;
  const rows: React.ReactNode[] = [];
  const pushLines = (text: string, kind: 'del' | 'add') => {
    for (const ln of text.split('\n')) {
      if (count >= MAX) return;
      rows.push(
        <div key={count} style={{
          display: 'flex', gap: 7, padding: '0 9px',
          background: kind === 'del' ? '#FBEAE7' : '#EAF4E6',
          color: kind === 'del' ? '#A8392C' : '#37722B',
          whiteSpace: 'pre-wrap', wordBreak: 'break-word',
        }}>
          <span style={{ userSelect: 'none', opacity: 0.55, flexShrink: 0 }}>{kind === 'del' ? '−' : '+'}</span>
          <span style={{ flex: 1 }}>{ln || ' '}</span>
        </div>
      );
      count++;
    }
  };
  hunks.forEach(h => { if (h.old) pushLines(h.old, 'del'); if (h.new) pushLines(h.new, 'add'); });
  return (
    <div style={{
      margin: '0 0 9px', borderRadius: 7, overflow: 'hidden', border: `1px solid ${C.bgInset}`,
      fontFamily: FONT.mono, fontSize: 11.5, lineHeight: 1.55,
      maxHeight: 320, overflowY: 'auto',
    }}>
      {rows}
      {count >= MAX && <div style={{ padding: '2px 9px', color: C.textMuted, fontStyle: 'italic' }}>…(обрезано)</div>}
    </div>
  );
}

// Строка инструмента с раскрываемым телом результата (вывод Bash/Read и т.п.)
function ToolUseView({ item }: { item: Extract<ChatItem, { kind: 'tool_use' }> }) {
  const meta = toolMeta(item.name);
  const [open, setOpen] = useState(false);
  const project = useContext(ChatProjectContext);
  const n = item.name.toLowerCase();
  const inp = (item.input ?? {}) as Record<string, any>;
  // Во время стриминга показываем накопленный partial_json («печатает команду»), затем — разобранный аргумент.
  // Пути показываем относительно корня проекта: file_path/path — целиком, в командах и
  // glob-шаблонах вырезаем абсолютный корень из текста (там путь — часть строки).
  const pathVal = inp.file_path ?? inp.path ?? inp.notebook_path;
  const toolArg = item.streamingArg ?? String(
    (inp.command != null ? stripRoot(String(inp.command), project?.rootPath) : null)
    ?? (pathVal != null ? relPath(String(pathVal), project?.rootPath) : null)
    ?? (inp.pattern != null ? stripRoot(String(inp.pattern), project?.rootPath) : null)
    ?? inp.query ?? inp.url ?? inp.description ?? inp.prompt ?? '');
  // Аргумент-путь (Read/Edit/…) — на мобиле обрезаем слева, чтобы было видно имя файла
  const argIsPath = inp.command == null && pathVal != null && item.streamingArg == null;
  // Имя инструмента по-русски (MCP → «server · tool»)
  const displayName = toolLabel(item.name);
  // Inline-diff из input (доступен сразу, не дожидаясь tool_result)
  const editHunks: Array<{ old?: string; new?: string }> =
    n === 'edit' && (typeof inp.old_string === 'string' || typeof inp.new_string === 'string')
      ? [{ old: inp.old_string, new: inp.new_string }]
    : n === 'multiedit' && Array.isArray(inp.edits)
      ? inp.edits.map((e: any) => ({ old: e.old_string, new: e.new_string }))
    : n === 'write' && typeof inp.content === 'string'
      ? [{ new: inp.content }]
    : [];
  const hasDiff = editHunks.length > 0;
  const hasResult = item.result !== undefined && item.result.trim().length > 0;
  const hasBody = hasDiff || hasResult;
  // Консольные инструменты (Bash/shell) → тёмный «терминальный» вывод.
  // Остальные (Read/Grep/Glob/MCP и пр.) → светлая «панель вывода», чтобы текст/код не давил тёмным фоном.
  const isConsole = n.startsWith('bash') || n.includes('shell');

  return (
    <div>
      <div
        style={{ padding: '4px 0', display: 'flex', alignItems: 'center', gap: 10, cursor: hasBody ? 'pointer' : 'default' }}
        onClick={() => hasBody && setOpen(o => !o)}
      >
        {item.result === undefined && <ToolSpinner />}
        <span style={{ display: 'flex', alignItems: 'center', gap: 5, flexShrink: 0, color: meta.color }}>
          {meta.icon}
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>{displayName}</span>
        </span>
        {toolArg
          ? <span className={argIsPath ? 'cc-trunc-left' : undefined} style={{ fontFamily: FONT.mono, fontSize: 12.5, flex: 1, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{toolArg}</span>
          : <span style={{ flex: 1 }} />}
        {item.result !== undefined && (
          <span style={{ fontSize: 11, color: item.isError ? '#C0392B' : C.textMuted, flexShrink: 0 }}>
            {item.isError ? 'ошибка' : 'готово'}
          </span>
        )}
        {hasBody && (
          <span style={{ color: C.textMuted, fontSize: 11, flexShrink: 0, display: 'inline-block', transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s' }}>▾</span>
        )}
      </div>
      {open && hasDiff && <DiffBody hunks={editHunks} />}
      {open && !hasDiff && hasResult && (
        <pre style={{
          margin: '0 0 9px', padding: '8px 10px', borderRadius: 7,
          // Bash → тёмный терминал; остальное → светлая панель вывода
          background: isConsole ? C.termBg : C.outputBg,
          border: isConsole ? 'none' : `1px solid ${C.outputBorder}`,
          // На светлой панели ошибку красим в danger; на тёмной — светлый «терминальный» оттенок
          color: isConsole
            ? (item.isError ? C.termError : C.termText)
            : (item.isError ? C.dangerText : C.textPrimary),
          fontFamily: FONT.mono,
          fontSize: 11.5, lineHeight: 1.5, maxHeight: 280, overflow: 'auto',
          whiteSpace: 'pre-wrap', wordBreak: 'break-word',
        }}>
          {(() => {
            const r = stripRoot(item.result!, project?.rootPath);
            return r.length > 4000 ? r.slice(0, 4000) + '\n…(обрезано)' : r;
          })()}
        </pre>
      )}
    </div>
  );
}

type ToolUseItem = Extract<ChatItem, { kind: 'tool_use' }>;

function parseTranscriptDir(result: string | undefined): string | null {
  if (!result) return null;
  const m = result.match(/Transcript dir:\s*(.+)/);
  return m ? m[1].trim() : null;
}

function parseWorkflowMeta(input: unknown): { description?: string; phases?: { title: string; detail?: string }[] } | null {
  const inp = input as Record<string, unknown> | null;
  const script = typeof inp?.script === 'string' ? inp.script : null;
  if (!script) return null;

  const metaStart = script.indexOf('export const meta');
  if (metaStart === -1) return null;
  const braceStart = script.indexOf('{', metaStart);
  if (braceStart === -1) return null;

  let depth = 0, metaEnd = -1;
  for (let i = braceStart; i < script.length; i++) {
    if (script[i] === '{') depth++;
    else if (script[i] === '}') { depth--; if (depth === 0) { metaEnd = i; break; } }
  }
  if (metaEnd === -1) return null;
  const metaStr = script.slice(braceStart, metaEnd + 1);

  const descMatch = metaStr.match(/description:\s*['"`]([^'"`]+)['"`]/);
  const description = descMatch?.[1];

  const phases: { title: string; detail?: string }[] = [];
  const phasesPos = metaStr.indexOf('phases:');
  if (phasesPos !== -1) {
    const bracketStart = metaStr.indexOf('[', phasesPos);
    if (bracketStart !== -1) {
      let bd = 0, bracketEnd = -1;
      for (let i = bracketStart; i < metaStr.length; i++) {
        if (metaStr[i] === '[') bd++;
        else if (metaStr[i] === ']') { bd--; if (bd === 0) { bracketEnd = i; break; } }
      }
      if (bracketEnd !== -1) {
        const phasesStr = metaStr.slice(bracketStart + 1, bracketEnd);
        const phaseRe = /\{[^}]*title:\s*['"`]([^'"`]+)['"`](?:[^}]*detail:\s*['"`]([^'"`]+)['"`])?[^}]*\}/g;
        let m;
        while ((m = phaseRe.exec(phasesStr)) !== null) phases.push({ title: m[1], detail: m[2] });
      }
    }
  }
  return { description, phases: phases.length > 0 ? phases : undefined };
}

function WorkflowBlockView({ workflow, agents, childrenByParentId }: {
  workflow: ToolUseItem;
  agents: ToolUseItem[];
  childrenByParentId: Map<string, ToolUseItem[]>;
}) {
  const [expanded, setExpanded] = useState(false);
  const [expandedAgents, setExpandedAgents] = useState<Set<string>>(new Set());
  const [expandedTranscriptAgents, setExpandedTranscriptAgents] = useState<Set<string>>(new Set());

  // Локальный фоллбэк — используется только для старых сессий без серверного ватчера
  const [localAgents, setLocalAgents] = useState<WorkflowAgentInfo[] | null>(null);
  const [localLoading, setLocalLoading] = useState(false);

  const isDone = workflow.result !== undefined;

  // Серверные агенты (реалтайм через SignalR — приходят от WorkflowWatcher)
  const serverAgents = workflow.workflowAgents;
  const serverDone = workflow.workflowDone;

  // Итоговые значения: сервер приоритетнее фоллбэка
  const transcriptAgents = serverAgents ?? localAgents;
  const transcriptLoading = serverAgents !== undefined ? false : localLoading;
  const hasTranscriptDir = isDone && !!parseTranscriptDir(workflow.result as string | undefined);
  // isSettled: result получен И (сервер подтвердил / нет transcript dir / фоллбэк загружен)
  // isDone=false → спиннер (workflow tool ещё не вернул result)
  const isSettled = isDone && (
    serverDone === true ||
    !hasTranscriptDir ||
    (serverAgents === undefined && localAgents !== null)
  );

  const meta = parseWorkflowMeta(workflow.input);
  const phases = meta?.phases ?? [];
  const doneCount = agents.filter(a => a.result !== undefined).length;
  const totalCount = agents.length;
  const progress = totalCount > 0 ? doneCount / totalCount : isSettled ? 1 : 0;

  // Прогресс по фазам: сколько фаз завершено (оцениваем по transcript агентам)
  const transcriptDone = transcriptAgents?.filter(a => a.isDone === true).length ?? 0;
  const transcriptTotal = transcriptAgents?.length ?? 0;
  const completedPhaseCount = isSettled
    ? phases.length
    : transcriptTotal > 0
    ? Math.floor((transcriptDone / transcriptTotal) * phases.length)
    : 0;

  // Фоллбэк-загрузка для старых сессий (где серверный ватчер не работал)
  useEffect(() => {
    if (serverAgents !== undefined) return; // сервер уже обрабатывает
    if (!isDone || localAgents !== null) return;
    const dir = parseTranscriptDir(workflow.result as string | undefined);
    if (!dir) return;
    setLocalLoading(true);
    api.workflow.getAgents(dir)
      .then(r => setLocalAgents(r.agents))
      .catch(() => setLocalAgents([]))
      .finally(() => setLocalLoading(false));
  }, [isDone, localAgents, workflow.result, serverAgents]);

  const toggleTranscriptAgent = (id: string) => setExpandedTranscriptAgents(prev => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });

  const toggleAgent = (id: string) => setExpandedAgents(prev => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });

  const DoneIcon = () => (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.success} strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12" /></svg>
  );

  return (
    <div style={{ border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden', background: C.bgPanel }}>
      {/* Шапка */}
      <div
        onClick={() => setExpanded(e => !e)}
        style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '10px 14px', cursor: 'pointer', userSelect: 'none' as const }}
      >
        <span style={{ flexShrink: 0, display: 'flex', alignItems: 'center', marginTop: meta?.description ? -1 : 0 }}>
          {isSettled
            ? <DoneIcon />
            : <div className="tool-spinner" />}
        </span>
        {/* Название + описание: одна колонка, description под заголовком */}
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textPrimary, flexShrink: 0 }}>Workflow</span>
            {!meta?.description && totalCount > 0 && (
              <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>
                {doneCount}/{totalCount} агентов
              </span>
            )}
            {!meta?.description && totalCount > 0 && (
              <div style={{ flex: 1, height: 3, background: C.borderLight, borderRadius: 2, overflow: 'hidden', minWidth: 40, maxWidth: 80 }}>
                <div style={{ width: `${Math.round(progress * 100)}%`, height: '100%', background: isSettled ? C.success : C.accent, borderRadius: 2, transition: 'width 0.3s ease' }} />
              </div>
            )}
          </div>
          {meta?.description && (
            <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {meta.description}
            </div>
          )}
        </div>
        <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>{expanded ? '▴ скрыть' : '▾ детали'}</span>
      </div>

      {/* Тело */}
      {expanded && (
        <div style={{ borderTop: `1px solid ${C.border}` }}>
          {/* Фазы из meta.phases */}
          {phases.length > 0 && (
            <div style={{ padding: '10px 14px 8px', display: 'flex', flexDirection: 'column', gap: 1 }}>
              {phases.map((phase, idx) => {
                const phaseDone = idx < completedPhaseCount;
                const phaseActive = !isSettled && idx === completedPhaseCount;
                return (
                <div key={idx} style={{ display: 'flex', alignItems: 'flex-start', gap: 8, padding: '5px 0', borderBottom: idx < phases.length - 1 ? `1px solid ${C.borderLight}` : undefined }}>
                  <span style={{ flexShrink: 0, width: 16, display: 'flex', alignItems: 'center', justifyContent: 'center', marginTop: 2 }}>
                    {phaseDone
                      ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke={C.success} strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12" /></svg>
                      : phaseActive
                      ? <div className="tool-spinner" style={{ width: 10, height: 10 }} />
                      : <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.border }} />}
                  </span>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 500, color: C.textPrimary, lineHeight: 1.4 }}>{phase.title}</div>
                    {phase.detail && (
                      <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary, lineHeight: 1.4, marginTop: 1 }}>{phase.detail}</div>
                    )}
                  </div>
                </div>
                );
              })}
            </div>
          )}
          {/* Субагенты из потока (если есть) */}
          {agents.length > 0 && (
            <div style={{ borderTop: phases.length > 0 ? `1px solid ${C.border}` : undefined }}>
              {agents.map((agent, idx) => {
                const tools = childrenByParentId.get(agent.id) ?? [];
                const isAgentExpanded = expandedAgents.has(agent.id);
                const inp = (agent.input ?? {}) as Record<string, unknown>;
                const rawLabel =
                  (typeof inp.description === 'string' ? inp.description : null) ??
                  (typeof inp.label === 'string' ? inp.label : null) ??
                  (typeof inp.prompt === 'string' ? inp.prompt : null) ??
                  (typeof inp.task === 'string' ? inp.task : null) ?? '';
                const label = rawLabel.split('\n')[0].slice(0, 100);
                const agentDone = agent.result !== undefined;
                return (
                  <div key={agent.id} style={{ borderTop: idx > 0 ? `1px solid ${C.bgInset}` : undefined }}>
                    <div
                      onClick={tools.length > 0 ? () => toggleAgent(agent.id) : undefined}
                      style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 14px', cursor: tools.length > 0 ? 'pointer' : 'default' }}
                    >
                      <span style={{ flexShrink: 0, width: 14, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                        {agentDone
                          ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke={C.success} strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12" /></svg>
                          : <div className="tool-spinner" style={{ width: 11, height: 11 }} />}
                      </span>
                      <span style={{ flex: 1, fontFamily: FONT.sans, fontSize: 12.5, color: label ? C.textPrimary : C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {label || `Агент ${idx + 1}`}
                      </span>
                      {tools.length > 0 && (
                        <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>{tools.length} {toolWord(tools.length)}</span>
                      )}
                      {tools.length > 0 && (
                        <span style={{ color: C.textMuted, fontSize: 11, flexShrink: 0, display: 'inline-block', transform: isAgentExpanded ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s' }}>▾</span>
                      )}
                    </div>
                    {isAgentExpanded && tools.length > 0 && (
                      <div style={{ paddingLeft: 22, paddingRight: 14, paddingBottom: 4, borderTop: `1px solid ${C.bgInset}` }}>
                        {tools.map((tool, ti) => (
                          <div key={tool.id} style={ti > 0 ? { borderTop: `1px solid ${C.bgInset}` } : undefined}>
                            <ToolUseView item={tool} />
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}
          {/* Агенты из transcript-файлов (показываем как только приходят через SignalR, не ждём isDone) */}
          {(transcriptLoading || (transcriptAgents && transcriptAgents.length > 0)) && (
            <div style={{ borderTop: `1px solid ${C.border}` }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '7px 14px 6px', background: C.bgInset, borderBottom: `1px solid ${C.borderLight}` }}>
                <span style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase' as const, letterSpacing: '0.04em' }}>Агенты</span>
                {!transcriptLoading && transcriptAgents && (
                  <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, background: C.borderLight, borderRadius: R.sm, padding: '1px 5px', fontWeight: 600, lineHeight: 1.5 }}>
                    {transcriptAgents.length}
                  </span>
                )}
              </div>
              {transcriptLoading && (
                <div style={{ padding: '6px 0' }}>
                  {[80, 65, 90].map((w, i) => (
                    <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '7px 14px', borderTop: i > 0 ? `1px solid ${C.bgInset}` : undefined }}>
                      <div style={{ width: 13, height: 13, borderRadius: '50%', background: C.borderLight, flexShrink: 0 }} />
                      <div style={{ height: 11, width: `${w}%`, maxWidth: 280, borderRadius: 4, background: C.borderLight }} />
                    </div>
                  ))}
                </div>
              )}
              {!transcriptLoading && transcriptAgents && transcriptAgents.length > 0 && (
                <div style={{ padding: '4px 0' }}>
                  {transcriptAgents.map((agent, idx) => {
                    const isOpen = expandedTranscriptAgents.has(agent.id);
                    const hasDetails = !!(agent.summary || agent.tools?.length || agent.files?.length);

                    // Определяем тип агента по инструментам
                    const toolNames = agent.tools?.map(t => t.name.toLowerCase()) ?? [];
                    const hasBash = toolNames.some(n => n.includes('bash') || n.includes('execute') || n.includes('run'));
                    const hasRead = toolNames.some(n => n.includes('read') || n.includes('grep') || n.includes('glob') || n.includes('search'));
                    const hasWrite = toolNames.some(n => n.includes('write') || n.includes('edit') || n.includes('create'));

                    // Иконка типа агента (SVG inline)
                    const agentIconSvg = hasBash
                      ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                          <polyline points="4 17 10 11 4 5" /><line x1="12" y1="19" x2="20" y2="19" />
                        </svg>
                      : hasWrite
                      ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                          <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                        </svg>
                      : hasRead
                      ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                          <circle cx="11" cy="11" r="8" /><line x1="21" y1="21" x2="16.65" y2="16.65" />
                        </svg>
                      : <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                          <circle cx="12" cy="8" r="4" /><path d="M6 20v-2a6 6 0 0 1 12 0v2" />
                        </svg>;

                    // Заголовок строки: первый md-заголовок из summary, иначе первая строка, иначе prompt
                    const summaryFirstLine = agent.summary
                      ? (() => {
                          const h = agent.summary!.match(/^#{1,6}\s+(.+)$/m);
                          if (h) return h[1].replace(/\*\*/g, '').trim();
                          return agent.summary!.replace(/^#+\s*/, '').replace(/\*\*/g, '').split('\n')[0].trim();
                        })()
                      : '';
                    const promptFirstLine = agent.prompt.split('\n')[0].trim();
                    const rowLabel = (summaryFirstLine || promptFirstLine).slice(0, 90) || `Агент ${idx + 1}`;

                    return (
                      <div key={agent.id} style={{ borderTop: idx > 0 ? `1px solid ${C.bgInset}` : undefined }}>
                        <div
                          onClick={hasDetails ? () => toggleTranscriptAgent(agent.id) : undefined}
                          style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 14px', cursor: hasDetails ? 'pointer' : 'default', userSelect: 'none' as const }}
                        >
                          {/* Галочка только если агент завершён (isDone=true), иначе спиннер */}
                          {agent.isDone === true
                            ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke={C.success} strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}><polyline points="20 6 9 17 4 12" /></svg>
                            : <div className="tool-spinner" style={{ width: 11, height: 11, flexShrink: 0 }} />}
                          {/* Иконка типа агента */}
                          {agentIconSvg}
                          {/* Основной текст строки — summary или prompt */}
                          <span style={{ flex: 1, fontFamily: FONT.sans, fontSize: 12, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', lineHeight: 1.4 }}>
                            {rowLabel}
                          </span>
                          {/* Счётчик инструментов */}
                          {agent.tools && agent.tools.length > 0 && (
                            <span style={{ fontFamily: FONT.mono, fontSize: 10, color: C.textMuted, flexShrink: 0 }}>
                              {agent.tools.reduce((s, t) => s + t.count, 0)}
                            </span>
                          )}
                          {hasDetails && (
                            <span style={{ color: C.textMuted, fontSize: 10, flexShrink: 0, display: 'inline-block', transform: isOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}>▾</span>
                          )}
                        </div>
                        {isOpen && hasDetails && (
                          <div style={{ margin: '0 14px 8px', background: C.bgInset, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, overflow: 'hidden' }}>
                            {/* Summary — основной результат */}
                            {agent.summary && (
                              <div style={{ padding: '8px 12px', fontFamily: FONT.sans, fontSize: 12, lineHeight: 1.5, color: C.textSecondary, borderBottom: `1px solid ${C.borderLight}` }}>
                                <MarkdownContent text={agent.summary} />
                              </div>
                            )}
                            {/* Задача (prompt) — второстепенный контекст */}
                            <div style={{ padding: '5px 12px 6px', fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, lineHeight: 1.4, borderBottom: (agent.tools?.length || agent.files?.length) ? `1px solid ${C.borderLight}` : undefined }}>
                              <span style={{ fontWeight: 600, marginRight: 4 }}>Задача:</span>
                              <span style={{ fontStyle: 'italic' }}>{agent.prompt.split('\n')[0].slice(0, 120)}{agent.prompt.length > 120 ? '…' : ''}</span>
                            </div>
                            {/* Инструменты */}
                            {agent.tools && agent.tools.length > 0 && (
                              <div style={{ padding: '6px 12px', display: 'flex', flexWrap: 'wrap' as const, gap: 4, borderBottom: agent.files?.length ? `1px solid ${C.borderLight}` : undefined }}>
                                {agent.tools.map(t => (
                                  <span key={t.name} style={{ fontFamily: FONT.mono, fontSize: 10, color: C.textMuted, background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '1px 5px', lineHeight: 1.6 }}>
                                    {t.name}{t.count > 1 ? ` ×${t.count}` : ''}
                                  </span>
                                ))}
                              </div>
                            )}
                            {/* Файлы */}
                            {agent.files && agent.files.length > 0 && (
                              <div style={{ padding: '5px 12px 7px', display: 'flex', flexWrap: 'wrap' as const, gap: '2px 10px' }}>
                                {agent.files.slice(0, 5).map(f => (
                                  <span key={f} style={{ fontFamily: FONT.mono, fontSize: 10, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' as const, lineHeight: 1.6 }}>
                                    {f.split(/[\\/]/).pop() ?? f}
                                  </span>
                                ))}
                                {agent.files.length > 5 && (
                                  <span style={{ fontFamily: FONT.sans, fontSize: 10, color: C.textMuted }}>+{agent.files.length - 5}</span>
                                )}
                              </div>
                            )}
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          )}
          {/* Ничего нет */}
          {agents.length === 0 && phases.length === 0 && !transcriptLoading && !transcriptAgents?.length && (
            <div style={{ padding: '10px 14px', fontFamily: FONT.sans, fontSize: 12, color: C.textMuted }}>
              {isDone ? 'Детали недоступны' : 'Запуск субагентов…'}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// Уточняющий вопрос Claude (AskUserQuestion) — интерактивная карточка выбора
interface QuestionDef { question: string; header?: string; multiSelect?: boolean; options: Array<{ label: string; description?: string }> }

// Маркер выбора: single → точка-радио, multi → чекбокс
function ChoiceMarker({ multi, selected }: { multi: boolean; selected: boolean }) {
  if (multi) {
    return selected ? (
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="1" y="1" width="14" height="14" rx="4" fill="#D97757" /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
    ) : (
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="1.5" y="1.5" width="13" height="13" rx="4" stroke="#9A8F7E" strokeWidth="1.5" /></svg>
    );
  }
  return selected ? (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="7" fill="#D97757" /><circle cx="8" cy="8" r="2.6" fill="#FBF1EA" /></svg>
  ) : (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="6.5" stroke="#9A8F7E" strokeWidth="1.5" /></svg>
  );
}

function AskQuestionView({ item, online, onAnswer }: {
  item: Extract<ChatItem, { kind: 'ask_question' }>;
  online: boolean;
  onAnswer: (toolUseId: string, answerText: string) => void;
}) {
  const questions = (() => {
    const q = (item.input as { questions?: unknown } | null)?.questions;
    return Array.isArray(q) ? (q as QuestionDef[]) : [];
  })();
  const [selected, setSelected] = useState<Record<number, string[]>>({});
  const [customText, setCustomText] = useState<Record<number, string>>({});
  const [customOpen, setCustomOpen] = useState<Record<number, boolean>>({});
  const [activeTab, setActiveTab] = useState(0);
  if (questions.length === 0) return null;

  const disabled = item.resolved || !online;
  const multiQ = questions.length > 1;

  // Отвеченный вопрос — компактная зелёная плашка «принято» со сводкой выбора по всем вопросам
  if (item.resolved) {
    return (
      <div style={{ border: '1px solid #CADFC4', borderLeft: '3px solid #5E8B4E', borderRadius: 12, padding: '13px 14px', background: '#EEF4EA' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 10, fontSize: 13, fontWeight: 600, color: '#3F6B33' }}>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="8" fill="#5E8B4E" /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#fff" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
          Ответ передан Claude
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {questions.map((q, qi) => {
            const stored = item.answers?.[q.question];
            const chosen = Array.isArray(stored) ? stored : stored ? [stored] : (selected[qi] ?? []);
            if (chosen.length === 0) return null;
            return (
              <div key={qi}>
                <div style={{ fontSize: 12, color: C.textSecondary, marginBottom: 4 }}>{q.header || q.question}</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  {chosen.map((label, li) => (
                    <span key={li} style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 12, fontWeight: 600, color: '#3F6B33', background: C.bgWhite, border: '1px solid #CADFC4', borderRadius: 7, padding: '3px 9px' }}>
                      <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="#5E8B4E" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
                      {label}
                    </span>
                  ))}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    );
  }

  const isAnswered = (qi: number) =>
    (selected[qi]?.length ?? 0) > 0 || (!!customOpen[qi] && (customText[qi]?.trim().length ?? 0) > 0);
  const allAnswered = questions.every((_, qi) => isAnswered(qi));

  const toggleOption = (qi: number, label: string, multi: boolean) => {
    setSelected(prev => {
      const cur = prev[qi] ?? [];
      if (multi) return { ...prev, [qi]: cur.includes(label) ? cur.filter(l => l !== label) : [...cur, label] };
      return { ...prev, [qi]: [label] };
    });
    // single: выбор готовой опции сворачивает «свой вариант»
    if (!multi) {
      setCustomOpen(p => ({ ...p, [qi]: false }));
      setCustomText(p => ({ ...p, [qi]: '' }));
    }
  };
  const toggleCustom = (qi: number, multi: boolean) => {
    const willOpen = !customOpen[qi];
    setCustomOpen(p => ({ ...p, [qi]: willOpen }));
    if (willOpen && !multi) setSelected(p => ({ ...p, [qi]: [] })); // single: «свой вариант» снимает опции
    if (!willOpen) setCustomText(p => ({ ...p, [qi]: '' }));
  };

  const submit = () => {
    // updatedInput как в SDK: исходные questions + answers (вопрос → label/массив/свой текст)
    const answers: Record<string, string | string[]> = {};
    questions.forEach((q, qi) => {
      const labels = selected[qi] ?? [];
      const custom = customOpen[qi] ? (customText[qi]?.trim() ?? '') : '';
      if (q.multiSelect) {
        answers[q.question] = custom ? [...labels, custom] : [...labels];
      } else {
        answers[q.question] = custom || labels[0] || '';
      }
    });
    onAnswer(item.toolUseId, JSON.stringify({ questions, answers }));
  };

  const renderQuestion = (q: QuestionDef, qi: number) => (
    <div>
      <div style={{ fontSize: 13, color: C.textHeading, fontWeight: 600, marginBottom: 9 }}>
        {q.question}
        {q.multiSelect && <span style={{ fontWeight: 400, color: C.textMuted, fontSize: 11 }}> · можно несколько</span>}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {q.options.map(opt => {
          const isSel = (selected[qi] ?? []).includes(opt.label);
          return (
            <button key={opt.label} disabled={disabled} onClick={() => toggleOption(qi, opt.label, !!q.multiSelect)}
              style={{
                textAlign: 'left', padding: '9px 12px', borderRadius: 9, minHeight: 44, boxSizing: 'border-box',
                cursor: disabled ? 'default' : 'pointer',
                border: isSel ? `1.5px solid ${C.accent}` : `1px solid ${C.border}`,
                background: isSel ? C.accentLight : C.bgWhite,
                display: 'flex', alignItems: 'flex-start', gap: 9,
              }}
            >
              {!q.multiSelect && <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}><ChoiceMarker multi={false} selected={isSel} /></span>}
              <span style={{ flex: 1 }}>
                <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>{opt.label}</span>
                {opt.description && <span style={{ display: 'block', fontSize: 12, color: C.textSecondary, marginTop: 2, lineHeight: 1.4 }}>{opt.description}</span>}
              </span>
              {q.multiSelect && <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}><ChoiceMarker multi selected={isSel} /></span>}
            </button>
          );
        })}
        {/* Свой вариант (free-text) */}
        {(() => {
          const open = !!customOpen[qi];
          const filled = open && (customText[qi]?.trim().length ?? 0) > 0;
          return (
            <div style={{ borderRadius: 9, overflow: 'hidden', border: open ? `1.5px solid ${C.accent}` : '1px dashed #C9A98F', background: open ? C.accentLight : 'transparent' }}>
              <div onClick={() => !disabled && toggleCustom(qi, !!q.multiSelect)}
                style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '9px 12px', minHeight: 44, boxSizing: 'border-box', cursor: disabled ? 'default' : 'pointer' }}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#9A8F7E" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></svg>
                <span style={{ flex: 1, fontSize: 13, fontWeight: 600, color: open ? C.textHeading : C.textMuted }}>Свой вариант{open ? '' : '…'}</span>
                {q.multiSelect && <span style={{ flexShrink: 0, display: 'flex' }}><ChoiceMarker multi selected={filled} /></span>}
              </div>
              {open && (
                <div style={{ padding: '0 10px 10px' }}>
                  <textarea
                    value={customText[qi] ?? ''}
                    onChange={e => setCustomText(p => ({ ...p, [qi]: e.target.value }))}
                    onClick={e => e.stopPropagation()}
                    disabled={disabled}
                    placeholder="Введите свой ответ…"
                    rows={2}
                    style={{ width: '100%', boxSizing: 'border-box', borderRadius: 8, border: `1px solid ${C.border}`, background: C.bgWhite, padding: '8px 10px', fontSize: 13, color: C.textHeading, fontFamily: 'inherit', resize: 'none', minHeight: 44, outline: 'none' }}
                  />
                </div>
              )}
            </div>
          );
        })()}
      </div>
    </div>
  );

  const secBtn = (label: string, onClick: () => void): React.ReactNode => (
    <button onClick={onClick} style={{ flex: 1, minHeight: 44, background: C.bgWhite, border: `1px solid ${C.border}`, color: C.textHeading, borderRadius: 9, padding: '9px 16px', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}>{label}</button>
  );
  const answerBtn = (full: boolean): React.ReactNode => (
    <button onClick={submit} disabled={!allAnswered}
      style={{ flex: full ? undefined : 1, width: full ? '100%' : undefined, minHeight: 44, background: C.accent, color: C.onAccent, borderRadius: 9, padding: '9px 16px', border: 'none', cursor: allAnswered ? 'pointer' : 'default', fontSize: 13, fontWeight: 600, opacity: allAnswered ? 1 : 0.5 }}>Ответить</button>
  );

  return (
    <div style={{ border: '1px solid #E6C9B8', borderLeft: `3px solid ${C.accent}`, borderRadius: 12, padding: '13px 14px', background: '#FBF1EA' }}>
      <div style={{ display: 'flex', alignItems: 'center', marginBottom: 11 }}>
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', gap: 7, fontSize: 13, fontWeight: 600, color: C.textHeading }}>
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#D97757" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z" />
          </svg>
          Claude уточняет
        </div>
        {multiQ && <span style={{ fontSize: 12, fontWeight: 600, color: C.textMuted, fontFamily: FONT.mono }}>{activeTab + 1} / {questions.length}</span>}
      </div>

      {multiQ && (
        <div style={{ display: 'flex', gap: 6, overflowX: 'auto', marginBottom: 12, paddingBottom: 2, scrollbarWidth: 'none' }}>
          {questions.map((q, qi) => {
            const ans = isAnswered(qi);
            const active = qi === activeTab;
            return (
              <button key={qi} disabled={disabled} onClick={() => setActiveTab(qi)}
                style={{
                  flexShrink: 0, display: 'flex', alignItems: 'center', gap: 6, padding: '0 11px', height: 28, boxSizing: 'border-box',
                  borderRadius: 14, cursor: disabled ? 'default' : 'pointer', fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap', lineHeight: 1,
                  border: active ? `1.5px solid ${C.accent}` : `1px solid ${C.border}`,
                  background: active ? C.accentLight : C.bgWhite,
                  color: active || ans ? C.textHeading : C.textSecondary,
                }}
              >
                {ans
                  ? <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="#D97757" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
                  : <span style={{ width: 6, height: 6, borderRadius: '50%', background: active ? C.accent : '#C9BEAD', flexShrink: 0 }} />}
                {q.header || `Q${qi + 1}`}
              </button>
            );
          })}
        </div>
      )}

      <div style={{ marginBottom: 11 }}>
        {renderQuestion(questions[multiQ ? activeTab : 0], multiQ ? activeTab : 0)}
      </div>

      {!online ? (
        <div style={{ fontSize: 12, color: C.textMuted }}>Недоступно офлайн</div>
      ) : multiQ ? (
        <div style={{ display: 'flex', gap: 8 }}>
          {activeTab > 0 && secBtn('‹ Назад', () => setActiveTab(t => t - 1))}
          {allAnswered
            ? answerBtn(false)
            : activeTab < questions.length - 1
              ? secBtn('Далее ›', () => setActiveTab(t => t + 1))
              : answerBtn(false)}
        </div>
      ) : (
        answerBtn(true)
      )}
    </div>
  );
}

// Свёрнутый блок исходного плана (disclosure) — для решённых состояний карточки
function CollapsedPlanBody({ plan }: { plan: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ marginTop: 8 }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, background: 'none', border: 'none',
          cursor: 'pointer', padding: 0, fontSize: 12, fontWeight: 600, color: C.textSecondary, fontFamily: 'inherit',
        }}
      >
        <span style={{ display: 'inline-block', transform: open ? 'rotate(180deg)' : 'rotate(0deg)', transition: 'transform 0.2s' }}>▾</span>
        {open ? 'Скрыть план' : 'Показать план'}
      </button>
      {open && (
        <div style={{
          marginTop: 8, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
          padding: '10px 12px', maxHeight: 320, overflow: 'auto', fontSize: 13, color: C.textHeading, wordBreak: 'break-word',
        }}>
          <MarkdownContent text={plan || '_(пустой план)_'} />
        </div>
      )}
    </div>
  );
}

// Карточка согласования плана (ExitPlanMode в режиме «План»):
// показывает план и кнопки «Одобрить и выполнить» / «Отклонить» (с комментарием).
function PlanReviewView({ item, online, onRespond, version, showBadge, showSwitch, onSwitchToAuto }: {
  item: Extract<ChatItem, { kind: 'plan_review' }>;
  online: boolean;
  onRespond: (requestId: string, approve: boolean, feedback?: string) => void;
  version?: number;
  showBadge?: boolean;
  showSwitch?: boolean;
  onSwitchToAuto?: () => void;
}) {
  const [rejecting, setRejecting] = useState(false);
  const [feedback, setFeedback] = useState('');
  const project = useContext(ChatProjectContext);
  // В тексте плана пути показываем относительно корня проекта
  const plan = stripRoot(item.plan, project?.rootPath);
  const planBodyRef = useRef<HTMLDivElement>(null);
  // fade-оверлей снизу появляется только если контент плана не помещается в maxHeight
  const [overflowing, setOverflowing] = useState(false);
  useEffect(() => {
    const el = planBodyRef.current;
    if (!el) return;
    setOverflowing(el.scrollHeight - el.clientHeight > 8);
  }, [plan, rejecting]);

  // === Решённое состояние: одобрено → компактная шапка выполнения ===
  if (item.resolved && item.approved) {
    return (
      <div style={{
        border: `1px solid ${C.successBg}`, borderLeft: `3px solid ${C.success}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.successBg,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, fontWeight: 600, color: C.successText }}>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="8" fill={C.success} /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#fff" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
          План одобрен — выполняется
        </div>
        <CollapsedPlanBody plan={plan} />
        {/* Подсказка про выход в Авто — только у актуального (последнего) одобренного плана */}
        {showSwitch && onSwitchToAuto && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 9, paddingTop: 9, borderTop: '1px solid #CFE3CA', fontSize: 12, color: C.textSecondary }}>
            <span style={{ flex: 1 }}>Чат остаётся в режиме «План» — следующие задачи тоже будут согласованы.</span>
            <button onClick={onSwitchToAuto}
              style={{ flexShrink: 0, background: 'none', border: 'none', cursor: 'pointer', fontSize: 12, fontWeight: 600, color: C.plan, padding: '2px 4px' }}>
              Перейти в Авто
            </button>
          </div>
        )}
      </div>
    );
  }

  // === Решённое состояние: отклонено → компактная строка + комментарий ===
  if (item.resolved && item.approved === false) {
    return (
      <div style={{
        border: `1px solid ${C.border}`, borderLeft: `3px solid ${C.textMuted}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.bgWhite,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, fontWeight: 600, color: C.textSecondary }}>
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M3 2v6h6" /><path d="M3 8a9 9 0 1 0 3-6.7L3 4" /></svg>
          План{version ? ` v${version}` : ''} — отклонён
        </div>
        {item.feedback?.trim() && (
          <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 7, whiteSpace: 'pre-wrap' }}>
            Комментарий: {item.feedback}
          </div>
        )}
        <CollapsedPlanBody plan={plan} />
      </div>
    );
  }

  // === На согласовании ===
  return (
    <div style={{
      border: `1px solid ${C.planBorder}`, borderLeft: `4px solid ${C.plan}`,
      borderRadius: R.xl, padding: '14px 16px', background: C.bgCard, boxShadow: SHADOW.card,
    }}>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10, marginBottom: 4 }}>
        <span style={{
          width: 28, height: 28, borderRadius: R.md, background: C.plan, flexShrink: 0,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <PlanIcon size={15} color="#FFF" />
        </span>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 700, color: C.textHeading, lineHeight: 1.2 }}>
            План готов
          </div>
          <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 2 }}>
            Claude предлагает план. Файлы пока не изменялись.
          </div>
        </div>
        {showBadge && version && (
          <span style={{
            flexShrink: 0, background: C.planLight, color: C.planText, borderRadius: R.sm,
            padding: '2px 8px', fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap',
          }}>
            v{version} · на согласовании
          </span>
        )}
      </div>

      <div style={{ position: 'relative', margin: '12px 0' }}>
        <div ref={planBodyRef} style={{
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
          padding: '10px 12px', maxHeight: 360, overflow: 'auto',
          fontSize: 13.5, color: C.textHeading, wordBreak: 'break-word',
        }}>
          <MarkdownContent text={plan || '_(пустой план)_'} />
        </div>
        {overflowing && (
          // Градиентный fade снизу — подсказка, что план длиннее видимой области
          <div style={{
            position: 'absolute', left: 1, right: 1, bottom: 1, height: 40, borderRadius: `0 0 ${R.lg}px ${R.lg}px`,
            background: `linear-gradient(to bottom, rgba(255,255,255,0), ${C.bgCard})`,
            pointerEvents: 'none',
          }} />
        )}
      </div>

      {!online ? (
        <div style={{ fontSize: 12, color: C.textMuted }}>Недоступно офлайн</div>
      ) : rejecting ? (
        <div>
          <div style={{ fontSize: 12, color: C.textSecondary, marginBottom: 7 }}>
            Claude учтёт это и предложит новый план
          </div>
          <textarea
            value={feedback}
            onChange={e => setFeedback(e.target.value)}
            autoFocus
            placeholder="Что поправить в плане? (необязательно)"
            rows={3}
            style={{ width: '100%', boxSizing: 'border-box', borderRadius: R.lg, border: `1px solid ${C.border}`, background: C.bgWhite, padding: '8px 10px', fontSize: 13, color: C.textHeading, fontFamily: 'inherit', resize: 'none', outline: 'none', marginBottom: 8 }}
          />
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={() => onRespond(item.requestId, false, feedback.trim() || undefined)}
              style={{ flex: 1, minHeight: 40, background: C.plan, color: '#FFF', borderRadius: R.lg, padding: 9, border: 'none', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}>
              Переработать план
            </button>
            <button onClick={() => { setRejecting(false); setFeedback(''); }}
              style={{ flex: 'none', minHeight: 40, background: C.bgWhite, border: `1px solid ${C.border}`, color: C.textSecondary, borderRadius: R.lg, padding: '9px 16px', cursor: 'pointer', fontSize: 13 }}>
              Назад
            </button>
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', gap: 8 }}>
          <button onClick={() => onRespond(item.requestId, true)}
            style={{
              flex: 1, minHeight: 42, background: C.plan, color: '#FFF', borderRadius: R.lg,
              padding: 9, border: 'none', cursor: 'pointer', fontSize: 13.5, fontWeight: 700,
              boxShadow: '0 4px 14px rgba(108,92,176,0.30)',
              display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
            }}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#FFF" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
            Одобрить и выполнить
          </button>
          <button onClick={() => setRejecting(true)}
            style={{ flex: 'none', minHeight: 42, background: 'transparent', border: `1px solid ${C.planBorder}`, color: C.planText, borderRadius: R.lg, padding: '9px 16px', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}>
            Отклонить
          </button>
        </div>
      )}
    </div>
  );
}

// Компактная строка изменённого файла — для использования внутри общего контура
// блока действий (рядом с карточками инструментов). Один ритм со строкой ToolUseView.
function FileChangedRow({ item, online, onOpenFile, onRevert }: {
  item: Extract<ChatItem, { kind: 'file_changed' }>;
  online: boolean;
  onOpenFile: (path: string) => void;
  onRevert: (path: string) => void;
}) {
  const project = useContext(ChatProjectContext);
  const relativePath = relPath(item.path, project?.rootPath);
  return (
    <div style={{ padding: '9px 0', display: 'flex', alignItems: 'center', gap: 10 }}>
      <span style={{ display: 'flex', alignItems: 'center', gap: 5, flexShrink: 0, color: '#C2693B' }}>
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
          <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
        </svg>
      </span>
      <span onClick={() => onOpenFile(item.path)}
        style={{ fontFamily: FONT.mono, fontSize: 12.5, flex: 1, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', cursor: 'pointer', direction: 'rtl', textAlign: 'left' }}>
        {relativePath}
      </span>
      <span style={{ fontSize: 11.5, color: '#27AE60', fontFamily: FONT.mono, flexShrink: 0 }}>+{item.added}</span>
      <span style={{ fontSize: 11.5, color: '#C0392B', fontFamily: FONT.mono, flexShrink: 0 }}>-{item.removed}</span>
      {online && (
        <button onClick={() => onRevert(item.path)}
          style={{ fontSize: 11, padding: '2px 8px', borderRadius: 6, border: '1px solid #E0D8CC', background: '#FFF', cursor: 'pointer', color: '#C0392B', flexShrink: 0 }}>
          Откатить
        </button>
      )}
    </div>
  );
}

// Ответ ассистента. Действия «Копировать/Повторить» — иконками в правом верхнем
// углу: десктоп — fade-in по hover на сообщении, мобайл (тач) — всегда видимы.
function TextMessageView({ text, online, onRetry, streaming }: { text: string; online: boolean; onRetry: () => void; streaming?: boolean }) {
  const [copied, setCopied] = useState(false);
  const copy = () => {
    navigator.clipboard?.writeText(text).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); }).catch(() => {});
  };
  const iconBtn: React.CSSProperties = {
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    width: 26, height: 26, borderRadius: 7, border: 'none', background: '#EDE7DA',
    color: C.textMuted, cursor: 'pointer', fontFamily: 'inherit', padding: 0,
  };
  return (
    <div className="cc-msg" style={{ position: 'relative', display: 'flex', flexDirection: 'column', gap: 6, maxWidth: '100%', overflow: 'hidden' }}>
      <div style={{ fontSize: 14, color: C.textHeading, wordBreak: 'break-word' }}>
        <MarkdownContent text={text} />
        {/* Мигающая каретка стриминга (B2) */}
        {streaming && <span style={{ display: 'inline-block', width: 7, height: 15, marginTop: 3, borderRadius: 1, background: C.accent, animation: 'blink 1s step-start infinite', verticalAlign: 'text-bottom' }} />}
      </div>
      {/* Действия — компактными иконками в правом верхнем углу (CSS управляет hover/тач) */}
      {!streaming && (
        <div className="cc-actions" style={{ position: 'absolute', top: 0, right: 0, display: 'flex', gap: 4 }}>
          <button onClick={copy} style={iconBtn} title={copied ? 'Скопировано' : 'Скопировать ответ'} aria-label="Скопировать ответ"
            onMouseEnter={e => { if (!copied) e.currentTarget.style.background = '#E2DACB'; }}
            onMouseLeave={e => { if (!copied) e.currentTarget.style.background = '#EDE7DA'; }}>
            {copied
              ? <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#5E8B4E" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
              : <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" /><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" /></svg>}
          </button>
          {online && (
            <button onClick={onRetry} style={iconBtn} title="Повторить последний запрос" aria-label="Повторить последний запрос"
              onMouseEnter={e => (e.currentTarget.style.background = '#E2DACB')}
              onMouseLeave={e => (e.currentTarget.style.background = '#EDE7DA')}>
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M1 4v6h6" /><path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10" /></svg>
            </button>
          )}
        </div>
      )}
    </div>
  );
}

interface ItemProps {
  item: ChatItem;
  index: number;
  online: boolean;
  streaming?: boolean;
  isLastResult?: boolean;
  onToggleThinking: (i: number) => void;
  onAllowPermission: (id: string) => void;
  onDenyPermission: (id: string) => void;
  onAllowAlways: (id: string) => void;
  onAnswerQuestion: (toolUseId: string, answerText: string) => void;
  onRespondPlan: (requestId: string, approve: boolean, feedback?: string) => void;
  planVersion?: number;
  planShowBadge?: boolean;
  planShowSwitch?: boolean;
  onSwitchToAuto: () => void;
  onOpenFile: (path: string) => void;
  onRevert: (path: string) => void;
  onRetry: () => void;
}

function ChatItemView({ item, index, online, streaming, isLastResult, onToggleThinking, onAllowPermission, onDenyPermission, onAllowAlways, onAnswerQuestion, onRespondPlan, planVersion, planShowBadge, planShowSwitch, onSwitchToAuto, onOpenFile, onRevert, onRetry }: ItemProps) {
  const project = useContext(ChatProjectContext);
  switch (item.kind) {
    case 'user_message':
      return (
        <div style={{
          alignSelf: 'flex-end', background: '#F1DDD1', color: '#5A3322',
          borderRadius: '18px 18px 4px 18px', padding: '12px 17px',
          maxWidth: '80%', fontSize: 14,
        }}>
          {item.text}
          {item.attachedPaths && item.attachedPaths.length > 0 && (
            <div style={{ marginTop: 6, display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {item.attachedPaths.map(p => (
                <span key={p} style={{
                  background: 'rgba(90,51,34,0.1)', borderRadius: 5,
                  padding: '1px 6px', fontSize: 11,
                }}>
                  {relPath(p, project?.rootPath)}
                </span>
              ))}
            </div>
          )}
        </div>
      );

    case 'session_started':
      // Старт чата не показываем — тех-инфа (модель/режим/cwd/MCP) дублирует шапку и раздувает чат
      return null;

    case 'text':
      return <TextMessageView text={item.text} online={online} onRetry={onRetry} streaming={streaming} />;

    case 'thinking': {
      const hasText = item.text.trim().length > 0;

      // Завершён без текста — не рендерить
      if (!streaming && !hasText) return null;

      // Стриминг, текст ещё не накоплен — тихий индикатор «клод думает»
      if (streaming && !hasText) {
        return (
          <div style={{ display: 'flex', alignItems: 'center', gap: 5, height: 24, paddingLeft: 2 }}>
            {[0, 1, 2].map(i => (
              <span
                key={i}
                style={{
                  display: 'inline-block', width: 5, height: 5, borderRadius: '50%',
                  background: C.textMuted,
                  animation: `thinkingDot 1.2s ease-in-out ${i * 0.2}s infinite`,
                }}
              />
            ))}
          </div>
        );
      }

      // Есть текст (стриминг или завершён) — компактная collapsible строка
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
          {/* Триггер-строка — одна строчка, без фона/рамки */}
          <div
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 5,
              cursor: 'pointer', userSelect: 'none',
              padding: '2px 0',
              width: 'fit-content',
            }}
            onClick={() => onToggleThinking(index)}
          >
            <span style={{ color: C.textMuted, display: 'flex', alignItems: 'center', flexShrink: 0 }}>
              <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 3a6 6 0 0 0-4 10.5V17h8v-3.5A6 6 0 0 0 12 3z" />
                <path d="M9 20h6M10 22h4" />
              </svg>
            </span>
            <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>
              Размышление
            </span>
            {streaming && (
              <span style={{
                width: 5, height: 5, borderRadius: '50%', background: C.textMuted,
                animation: 'thinkingDot 1.2s ease-in-out infinite', flexShrink: 0,
              }} />
            )}
            {!streaming && (
              <span title="приблизительно, по объёму текста" style={{ fontSize: 10, color: C.textMuted, fontFamily: FONT.mono, opacity: 0.7 }}>
                ~{Math.max(1, Math.round(item.text.length / 4))} ток.
              </span>
            )}
            <span style={{
              color: C.textMuted, fontSize: 10, opacity: 0.7,
              transform: item.expanded ? 'rotate(180deg)' : 'rotate(0deg)',
              transition: 'transform 0.2s',
              display: 'inline-block',
            }}>▾</span>
          </div>
          {/* Раскрытое содержимое — левая полоска как у цитаты */}
          {item.expanded && (
            <div style={{
              marginTop: 4,
              paddingLeft: 10,
              borderLeft: `2px solid ${C.borderLight}`,
              fontSize: 11.5, fontStyle: 'italic', lineHeight: 1.65,
              color: C.textMuted,
              whiteSpace: 'pre-wrap',
              fontFamily: FONT.sans,
            }}>
              {item.text}
            </div>
          )}
        </div>
      );
    }

    case 'tool_use':
      // План задач рисуем отдельной карточкой-чек-листом. Линию-коннектор для дочерних
      // вызовов субагента (parentToolUseId) рисует renderItems — единой непрерывной полосой.
      return item.name === 'TodoWrite' ? <TodoPlanView input={item.input} /> : <ToolUseView item={item} />;

    case 'ask_question':
      return <AskQuestionView item={item} online={online} onAnswer={onAnswerQuestion} />;

    case 'plan_review':
      return <PlanReviewView item={item} online={online} onRespond={onRespondPlan} version={planVersion} showBadge={planShowBadge} showSwitch={planShowSwitch} onSwitchToAuto={onSwitchToAuto} />;

    case 'permission_request': {
      // Что именно собирается выполнить Claude — команда/путь/аргументы
      const detail = (() => {
        const inp = item.toolInput as Record<string, unknown> | null;
        if (!inp) return '';
        if (typeof inp.command === 'string') return stripRoot(inp.command, project?.rootPath);
        if (typeof inp.file_path === 'string') return relPath(inp.file_path, project?.rootPath);
        if (typeof inp.path === 'string') return relPath(inp.path, project?.rootPath);
        try { const s = JSON.stringify(inp, null, 2); return s === '{}' ? '' : s; } catch { return ''; }
      })();
      // Консольная команда (Bash/shell) → тёмный «терминал»; прочее (путь файла и т.п.) → светлая панель
      const pn = item.toolName.toLowerCase();
      const isConsoleReq = pn.startsWith('bash') || pn.includes('shell');
      return (
        <div style={{
          border: '1px solid #E6C9B8', borderLeft: `3px solid ${C.accent}`,
          borderRadius: 12, padding: '13px 14px', background: '#FBF1EA',
        }}>
          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4, color: C.textHeading }}>
            Запрос разрешения
          </div>
          <div style={{ fontSize: 12, color: '#5A5040', marginBottom: 10 }}>
            Claude хочет выполнить <span style={{ fontWeight: 600 }}>{item.toolName}</span>:
          </div>
          <div style={{
            background: isConsoleReq ? C.termBg : C.outputBg,
            border: isConsoleReq ? 'none' : `1px solid ${C.outputBorder}`,
            borderRadius: 7, padding: '8px 11px',
            color: isConsoleReq ? C.termText : C.textPrimary, fontFamily: FONT.mono,
            fontSize: 12, marginBottom: 12, lineHeight: 1.5,
            whiteSpace: 'pre-wrap', wordBreak: 'break-word', maxHeight: 180, overflow: 'auto',
          }}>
            {detail || item.toolName}
          </div>
          {item.resolved ? (
            <div style={{ fontSize: 12, color: '#8A8070' }}>Решение принято</div>
          ) : online ? (
            <>
              <div style={{ display: 'flex', gap: 8 }}>
                <button
                  onClick={() => onAllowPermission(item.requestId)}
                  style={{
                    flex: 1, background: C.accent, color: C.onAccent,
                    borderRadius: 9, padding: 9, border: 'none',
                    cursor: 'pointer', fontSize: 13, fontWeight: 600,
                  }}
                >
                  Разрешить
                </button>
                <button
                  onClick={() => onDenyPermission(item.requestId)}
                  style={{
                    flex: 1, background: C.bgWhite, border: `1px solid ${C.border}`,
                    color: C.textSecondary, borderRadius: 9, padding: 9,
                    cursor: 'pointer', fontSize: 13,
                  }}
                >
                  Отклонить
                </button>
              </div>
              <button
                onClick={() => onAllowAlways(item.requestId)}
                style={{
                  marginTop: 8, width: '100%', background: 'none', border: 'none',
                  cursor: 'pointer', fontSize: 12, color: '#B05C38', padding: '4px 0',
                }}
              >
                Всегда разрешать «{item.toolName}» в этом чате
              </button>
            </>
          ) : (
            <div style={{ fontSize: 12, color: C.textMuted }}>Недоступно офлайн</div>
          )}
        </div>
      );
    }

    case 'file_changed': {
      const fileName = relPath(item.path, project?.rootPath);
      return (
        <div style={{
          border: `1px solid ${C.borderLight}`, borderRadius: 14, overflow: 'hidden',
          background: C.bgWhite, boxShadow: '0 2px 10px rgba(60,50,35,0.05)',
        }}>
          <div style={{
            display: 'flex', alignItems: 'center', gap: 11,
            padding: '12px 13px', cursor: 'pointer',
            borderBottom: '1px solid #EFE9DD',
          }}
            onClick={() => onOpenFile(item.path)}
          >
            <div style={{
              width: 28, height: 28, borderRadius: 8,
              background: '#FBEBE0', color: '#C2693B',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              flexShrink: 0,
            }}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
              </svg>
            </div>
            <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {fileName}
            </span>
            <span style={{ fontSize: 11.5, color: '#27AE60', fontFamily: FONT.mono }}>
              +{item.added}
            </span>
            <span style={{ fontSize: 11.5, color: '#C0392B', fontFamily: FONT.mono }}>
              -{item.removed}
            </span>
          </div>
          <div style={{ padding: '8px 13px', display: 'flex', gap: 6 }}>
            <button
              onClick={() => onOpenFile(item.path)}
              style={{
                fontSize: 12, padding: '4px 10px', borderRadius: 6,
                border: `1px solid ${C.borderLight}`, background: '#FFF', cursor: 'pointer', color: C.textPrimary,
              }}
            >
              Открыть
            </button>
            {online && (
              <button
                onClick={() => onRevert(item.path)}
                style={{
                  fontSize: 12, padding: '4px 10px', borderRadius: 6,
                  border: '1px solid #E0D8CC', background: '#FFF',
                  cursor: 'pointer', color: '#C0392B',
                }}
              >
                Откатить
              </button>
            )}
          </div>
        </div>
      );
    }

    case 'result': {
      const ok = item.subtype === 'success';
      // Склонение числительного: 1 шаг, 2 шага, 5 шагов
      const stepWord = (n: number) => {
        const m10 = n % 10, m100 = n % 100;
        if (m10 === 1 && m100 !== 11) return 'шаг';
        if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'шага';
        return 'шагов';
      };
      const fmtTok = (n: number) => n >= 1000 ? (n / 1000).toFixed(n >= 10000 ? 0 : 1) + 'k' : String(n);
      const fmtCost = (c: number) => '$' + (c < 0.01 ? c.toFixed(4) : c < 1 ? c.toFixed(3) : c.toFixed(2));
      const u = item.usage;
      const sep = <span style={{ opacity: 0.45 }}>·</span>;

      // Ошибочный итог хода — показываем причину и предлагаем повторить
      if (!ok) {
        const REASONS: Record<string, string> = {
          error_max_turns: 'достигнут лимит ходов',
          error_during_execution: 'сбой во время выполнения',
          error_max_budget_usd: 'исчерпан бюджет',
          error_max_structured_output_retries: 'не удалось получить структурированный ответ',
        };
        // Конкретная причина по api_error_status имеет приоритет над общим subtype
        const apiReason = (status?: string): string | null => {
          if (!status) return null;
          const s = status.toLowerCase();
          if (s.includes('overload')) return 'серверы Anthropic перегружены';
          if (s.includes('rate') || s.includes('429')) return 'превышен лимит запросов к API';
          if (s.includes('credit') || s.includes('billing') || s.includes('payment') || s.includes('402')) return 'проблема с биллингом или кредитами';
          if (s.includes('401') || s.includes('authentication')) return 'ошибка авторизации — проверьте API-ключ';
          if (s.includes('403') || s.includes('permission')) return 'доступ запрещён (403)';
          if (s.includes('404') || s.includes('not_found')) return 'ресурс не найден (404)';
          if (s.includes('529')) return 'сервис временно перегружен (529)';
          if (s.includes('500') || s.includes('internal')) return 'внутренняя ошибка сервера';
          if (s.includes('timeout')) return 'таймаут запроса к API';
          return `ошибка API: ${status}`;
        };
        const reason = apiReason(item.apiErrorStatus) ?? REASONS[item.subtype] ?? `ход завершился с ошибкой (${item.subtype})`;
        return (
          <div style={{
            alignSelf: 'center', maxWidth: '100%',
            background: '#FDECEA', border: '1px solid #F5C6CB', borderRadius: 8,
            padding: '8px 12px', fontSize: 12.5, color: '#C0392B',
            display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 9, flexWrap: 'wrap',
          }}>
            <span style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
              <span style={{ fontWeight: 700 }}>✗</span>
              <span>{reason}</span>
              <span style={{ opacity: 0.65, fontFamily: FONT.mono, fontSize: 11 }}>
                · {item.numTurns} {stepWord(item.numTurns)} · {(item.durationMs / 1000).toFixed(1)}с
              </span>
            </span>
            {online && (
              <button
                onClick={onRetry}
                style={{
                  fontSize: 12, padding: '4px 10px', borderRadius: 6,
                  border: '1px solid #C0392B', background: '#FFF', cursor: 'pointer',
                  color: '#C0392B', whiteSpace: 'nowrap', flexShrink: 0,
                }}
              >
                Повторить
              </button>
            )}
          </div>
        );
      }

      // Плашка токенов/времени — только у последнего хода (экономия места); у прошлых скрываем
      if (!isLastResult) return null;

      return (
        <div style={{
          fontSize: 11, color: '#8A8070', alignSelf: 'center',
          background: '#E8E2D6', borderRadius: 8, padding: '4px 11px',
          fontFamily: FONT.mono,
          display: 'flex', alignItems: 'center', gap: 7, flexWrap: 'wrap', justifyContent: 'center',
        }}>
          <span style={{ color: ok ? C.success : '#C0392B', fontWeight: 700 }}>{ok ? '✓' : '✗'}</span>
          <span>{item.numTurns} {stepWord(item.numTurns)}</span>
          {sep}
          <span>{(item.durationMs / 1000).toFixed(1)}с</span>
          {u && (u.inputTokens > 0 || u.outputTokens > 0) && (
            <>
              {sep}
              <span title="входные · выходные токены">↑{fmtTok(u.inputTokens)} ↓{fmtTok(u.outputTokens)}</span>
            </>
          )}
          {typeof item.totalCostUsd === 'number' && item.totalCostUsd > 0 && (
            <>
              {sep}
              <span style={{ color: '#B05C38', fontWeight: 700 }}>{fmtCost(item.totalCostUsd)}</span>
            </>
          )}
          {item.permissionDenials && item.permissionDenials.length > 0 && (
            <>
              {sep}
              <span title={`Запрещено: ${item.permissionDenials.join(', ')}`} style={{ color: '#C0392B', fontWeight: 700 }}>
                ⊘ {item.permissionDenials.length} {item.permissionDenials.length === 1 ? 'запрет' : 'запрета(ов)'}
              </span>
            </>
          )}
        </div>
      );
    }

    case 'rate_limit': {
      // Мягкий лимит API: ход не упал, claude ждёт сброса окна — янтарный информационный баннер
      const TYPES: Record<string, string> = {
        five_hour: '5-часовой лимит',
        seven_day: 'недельный лимит',
        weekly: 'недельный лимит',
      };
      const label = TYPES[item.limitType] ?? (item.limitType ? `лимит (${item.limitType})` : 'лимит запросов');
      // "rejected" — лимит реально достигнут; всё прочее (allowed_warning) — приближение
      const reached = !item.status || item.status === 'rejected';
      const verb = reached ? 'Достигнут' : 'Приближается';
      let when = '';
      if (item.resetsAt) {
        const dt = new Date(item.resetsAt);
        if (!isNaN(dt.getTime())) {
          const sameDay = dt.toDateString() === new Date().toDateString();
          const hhmm = dt.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
          when = sameDay
            ? `сбросится в ${hhmm}`
            : `сбросится ${dt.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })} в ${hhmm}`;
        }
      }
      return (
        <div style={{
          alignSelf: 'center', maxWidth: '100%',
          background: '#FBF0DC', border: '1px solid #EAD2A0', borderRadius: 8,
          padding: '7px 12px', fontSize: 12.5, color: '#9A6B1E',
          display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', justifyContent: 'center',
        }}>
          <span>⏳</span>
          <span>{verb} {label}{when ? <span style={{ opacity: 0.75 }}> · {when}</span> : null}</span>
        </div>
      );
    }

    case 'compact_boundary': {
      const fmtTok = (nn: number) => nn >= 1000 ? (nn / 1000).toFixed(nn >= 10000 ? 0 : 1) + 'k' : String(nn);
      return (
        <div style={{ alignSelf: 'stretch', display: 'flex', alignItems: 'center', gap: 10, color: C.textMuted, fontSize: 11, margin: '2px 0' }}>
          <div style={{ flex: 1, height: 1, background: C.border }} />
          <span style={{ display: 'flex', alignItems: 'center', gap: 5, whiteSpace: 'nowrap' }}>
            <span style={{ color: '#B89B6E' }}>✦</span>
            контекст свёрнут
            {typeof item.preTokens === 'number' && item.preTokens > 0 && <span style={{ opacity: 0.7 }}>· было {fmtTok(item.preTokens)} токенов</span>}
          </span>
          <div style={{ flex: 1, height: 1, background: C.border }} />
        </div>
      );
    }

    case 'resumed':
      // Разделитель «продолжение чата» убран — декоративный, без полезной нагрузки
      return null;

    case 'interrupted':
      return (
        <div style={{
          alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'wrap', justifyContent: 'center',
          background: '#F3ECE2', border: `1px solid ${C.border}`, borderRadius: 8, padding: '6px 12px', fontSize: 12, color: C.textSecondary,
        }}>
          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <svg width="11" height="11" viewBox="0 0 24 24" fill="#9A8F7E"><rect x="5" y="5" width="14" height="14" rx="2" /></svg>
            Ход остановлен пользователем
          </span>
          {online && (
            <button onClick={onRetry} style={{ fontSize: 12, padding: '3px 10px', borderRadius: 6, border: '1px solid #C9BEAD', background: '#FFF', cursor: 'pointer', color: '#5A5043', whiteSpace: 'nowrap' }}>Повторить</button>
          )}
        </div>
      );

    case 'truncated':
      return (
        <div style={{
          alignSelf: 'center', maxWidth: '100%', display: 'flex', alignItems: 'center', gap: 8,
          background: '#FBF0DC', border: '1px solid #EAD2A0', borderRadius: 8, padding: '6px 12px',
          fontSize: 12.5, color: '#9A6B1E',
        }}>
          <span>✂</span>
          <span>Ответ обрезан — достигнут лимит токенов в ответе</span>
        </div>
      );

    case 'redacted_thinking':
      return (
        <div style={{
          display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px',
          background: '#EFEAE0', border: `1px solid ${C.border}`, borderRadius: 10,
          fontSize: 12.5, fontStyle: 'italic', color: '#8A8070',
        }}>
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="11" width="18" height="11" rx="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" /></svg>
          Размышление скрыто (зашифровано провайдером)
        </div>
      );

    case 'session_ended':
      return (
        <div style={{
          alignSelf: 'center', maxWidth: '100%', display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'wrap', justifyContent: 'center',
          background: '#FDECEA', border: '1px solid #F5C6CB', borderRadius: 8, padding: '7px 12px', fontSize: 12.5, color: '#C0392B',
        }}>
          <span>⚠ Сессия прервана — Claude завершился неожиданно</span>
          {online && (
            <button onClick={onRetry} style={{ fontSize: 12, padding: '3px 10px', borderRadius: 6, border: '1px solid #C0392B', background: '#FFF', cursor: 'pointer', color: '#C0392B', whiteSpace: 'nowrap' }}>Повторить</button>
          )}
        </div>
      );

    case 'error':
      return (
        <div style={{
          background: '#FDECEA', borderRadius: 8, padding: '8px 12px',
          fontSize: 13, color: '#C0392B', border: '1px solid #F5C6CB',
          display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10,
        }}>
          <span>⚠ {item.text}</span>
          {item.canRetry && online && (
            <button
              onClick={onRetry}
              style={{
                fontSize: 12, padding: '4px 10px', borderRadius: 6,
                border: '1px solid #C0392B', background: '#FFF',
                cursor: 'pointer', color: '#C0392B', whiteSpace: 'nowrap', flexShrink: 0,
              }}
            >
              Повторить
            </button>
          )}
        </div>
      );

    default:
      return null;
  }
}
