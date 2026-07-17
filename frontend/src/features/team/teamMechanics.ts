// Реестр механик «Обсудить с командой»: карточки раскрывашки композера,
// настройки и билдеры текста хода. Под капотом каждая механика — обычное
// сообщение со скиллом (/panel-of-experts, /oh-my-claudecode:*) или
// промпт-обвязкой persona_ask; оркестрирует CLI, бэкенд не участвует.
import type { LucideIcon } from 'lucide-react';
import {
  BadgeCheck, Boxes, FlaskConical, GraduationCap, HelpCircle, MessagesSquare,
  Rocket, Route, Scale, ScanSearch, Swords,
} from 'lucide-react';

export type TeamMechanicId =
  | 'discuss' | 'panel' | 'consensus' | 'interview'
  | 'autopilot' | 'implement' | 'qa' | 'review' | 'redteam' | 'trace' | 'sci';

export type TeamMechanicGroup = 'Обсудить' | 'Спланировать' | 'Сделать' | 'Проверить' | 'Исследовать';

// Оси ревью-консилиума и углы атаки красной команды (совпадают с каталогами
// одноимённых workflow-скриптов review-consilium.js / red-team.js)
export type ReviewLens = 'correctness' | 'security' | 'tests' | 'architecture' | 'performance';
export type AttackAngle = 'edge-cases' | 'security' | 'wrong-assumptions' | 'load-scale' | 'failure-modes';

export const REVIEW_LENSES: ReadonlyArray<[ReviewLens, string]> = [
  ['correctness', 'Корректность'], ['security', 'Безопасность'], ['tests', 'Тесты'],
  ['architecture', 'Архитектура'], ['performance', 'Производительность'],
];
export const ATTACK_ANGLES: ReadonlyArray<[AttackAngle, string]> = [
  ['edge-cases', 'Краевые случаи'], ['security', 'Безопасность'],
  ['wrong-assumptions', 'Неверные допущения'], ['load-scale', 'Нагрузка'], ['failure-modes', 'Отказы'],
];

// Участник для механик с персонами (совместим с PersonaLite из lib/personas)
export interface TeamPersonaRef {
  id: string;
  handle: string;
  name?: string;
  role?: string;
}

export interface TeamMechanicSettings {
  participants: TeamPersonaRef[];   // discuss: до 2; panel (experts=personas): до 4
  rounds: 1 | 2 | 3;                // panel
  expertsMode: 'roles' | 'personas'; // panel: анонимные роли или персоны
  attachContext: boolean;           // panel: приложить краткий контекст чата
  interviewFirst: boolean;          // consensus: интервью перед планом (--interactive)
  deliberate: boolean;              // consensus: тщательный режим
  depth: 'quick' | 'standard' | 'deep'; // interview
  untilDone: boolean;               // autopilot: включить цикл «до готово» (work-loop)
  qaTarget: 'tests' | 'build' | 'lint' | 'typecheck'; // qa
  reviewLenses: ReviewLens[];       // review: оси ревью
  reviewVerify: boolean;            // review: adversarial-проверка находок
  attackAngles: AttackAngle[];      // redteam: углы атаки
  implWorktree: boolean;            // implement: параллельно в worktree (иначе последовательно)
  implVerify: boolean;              // implement: финальная проверка тестами/сборкой
}

export const DEFAULT_TEAM_SETTINGS: TeamMechanicSettings = {
  participants: [],
  rounds: 3,
  expertsMode: 'personas',
  attachContext: false,
  interviewFirst: false,
  deliberate: false,
  depth: 'standard',
  untilDone: true,
  qaTarget: 'tests',
  reviewLenses: ['correctness', 'security', 'tests'],
  reviewVerify: true,
  attackAngles: ['edge-cases', 'wrong-assumptions', 'failure-modes'],
  implWorktree: false,
  implVerify: true,
};

export interface TeamMechanic {
  id: TeamMechanicId;
  group: TeamMechanicGroup;
  name: string;
  icon: LucideIcon;
  desc: string;
  /** Ориентир тяжести: 1 — дёшево, 3 — жжёт токены */
  cost: 1 | 2 | 3;
  placeholder: string;
  /** Имя скилла, который должен быть в окружении (см. api.skills); null — работает всегда */
  requiredSkill: string | null;
  /** Карточка следующей итерации — показывается задизейбленной */
  soon?: boolean;
}

export const TEAM_MECHANICS: TeamMechanic[] = [
  {
    id: 'discuss', group: 'Обсудить', name: 'Дискуссия', icon: MessagesSquare, cost: 1,
    desc: 'Быстро спросить мнения персон', placeholder: 'Вопрос для дискуссии…',
    requiredSkill: null,
  },
  {
    id: 'panel', group: 'Обсудить', name: 'Панель экспертов', icon: GraduationCap, cost: 2,
    desc: 'Дебаты: идеи → критика → синтез', placeholder: 'Тема для панели экспертов…',
    requiredSkill: 'panel-of-experts',
  },
  {
    id: 'consensus', group: 'Спланировать', name: 'Консенсус-план', icon: Scale, cost: 2,
    desc: 'План через спор до одобрения критика', placeholder: 'Задача для планирования…',
    requiredSkill: 'oh-my-claudecode:ralplan',
  },
  {
    id: 'interview', group: 'Спланировать', name: 'Интервью', icon: HelpCircle, cost: 1,
    desc: 'Вопросы до кристальной постановки', placeholder: 'Идея, которую нужно прояснить…',
    requiredSkill: 'oh-my-claudecode:deep-interview',
  },
  {
    id: 'autopilot', group: 'Сделать', name: 'Автопилот', icon: Rocket, cost: 3,
    desc: 'От идеи до работающего кода', placeholder: 'Что построить?…',
    requiredSkill: 'oh-my-claudecode:autopilot',
  },
  {
    id: 'implement', group: 'Сделать', name: 'Командная реализация', icon: Boxes, cost: 3,
    desc: 'Разбить и раздать исполнителям', placeholder: 'Что реализовать командой?…',
    requiredSkill: 'team-implement',
  },
  {
    id: 'qa', group: 'Проверить', name: 'QA-цикл', icon: BadgeCheck, cost: 2,
    desc: 'Чинит до зелёной проверки', placeholder: 'Комментарий к прогону (необязательно)…',
    requiredSkill: 'oh-my-claudecode:ultraqa',
  },
  {
    id: 'review', group: 'Проверить', name: 'Ревью-консилиум', icon: ScanSearch, cost: 2,
    desc: 'Ревью с N линз + проверка находок', placeholder: 'Что ревьюим (пусто — текущий дифф)…',
    requiredSkill: 'review-consilium',
  },
  {
    id: 'redteam', group: 'Проверить', name: 'Красная команда', icon: Swords, cost: 2,
    desc: 'Атака решения с разных углов', placeholder: 'Что проверить на прочность?…',
    requiredSkill: 'red-team',
  },
  {
    id: 'trace', group: 'Исследовать', name: 'Трассировка', icon: Route, cost: 2,
    desc: 'Конкурирующие гипотезы «почему»', placeholder: 'Наблюдение, которое нужно объяснить…',
    requiredSkill: 'oh-my-claudecode:trace',
  },
  {
    id: 'sci', group: 'Исследовать', name: 'Анализ кода', icon: FlaskConical, cost: 2,
    desc: 'Параллельные учёные + отчёт', placeholder: 'Цель анализа…',
    requiredSkill: 'oh-my-claudecode:sciomc',
  },
];

export function teamMechanic(id: TeamMechanicId): TeamMechanic {
  return TEAM_MECHANICS.find(m => m.id === id)!;
}

// Фиксированное начало обвязки дискуссии — по нему же детектится бейдж в ленте
const DISCUSS_PREFIX = 'Обсуди со мной и командой вопрос';

// Тема в двойных кавычках для string-механик (ralplan/deep-interview/autopilot/trace/sci):
// внутренние прямые кавычки заменяются на «ёлочки» (попарно), иначе цитата рвётся, а
// декодер quotedTopic режет тему по первой внутренней кавычке. JSON-механики не трогаем —
// JSON.stringify экранирует сам.
function quoteTopic(t: string): string {
  let open = true;
  const safe = t.replace(/"/g, () => {
    const ch = open ? '«' : '»';
    open = !open;
    return ch;
  });
  return `"${safe}"`;
}

/**
 * Собирает текст хода для механики. Тема берётся из композера; настройки — из раскрывашки.
 * Для autopilot с untilDone цикл «до готово» включается ОТДЕЛЬНО (PUT /chats/{id}/loop) —
 * в тексте хода это не отражается.
 */
export function buildTeamTurnText(
  id: TeamMechanicId,
  topic: string,
  s: TeamMechanicSettings,
  chatContext?: string,
): string {
  const t = topic.trim();
  switch (id) {
    case 'discuss': {
      // Обвязка — язык cross-examination из hyperplan OmO (перенесена из DiscussTeamDialog).
      // Инструмент консультации НЕ называем: в чате с файловыми сабагентами-персонами это
      // Task(subagent_type=handle), в остальных — persona_ask; выбор описан системным
      // блоком о консультациях (BuildMentionsHint), жёсткая ссылка на persona_ask ломалась.
      const mentions = s.participants.map(p => `@${p.handle}`).join(' и ');
      return (
        `${DISCUSS_PREFIX}: ${t}\n\n` +
        `Спроси мнение ${mentions} способом из системного блока о консультациях с персонами ` +
        `(вопрос формулируй самодостаточно, с нужным контекстом). Собери позиции. ` +
        `При разногласиях устрой один раунд перекрёстной проверки: слабый тезис атакуй ` +
        `конкретным контраргументом и перешли автору на защиту, сильный отмечай ` +
        `«УСТОЯЛ — причина». Дистиллируй итог, оставив только обоснованное: к чему пришли, ` +
        `что осталось спорным (с аргументами сторон). Заверши своим взвешенным выводом.`
      );
    }
    case 'panel': {
      // Workflow-скрипт парсит JSON-args из строки сам; participants — handle персон
      // для ролей по порядку [Генератор, Критик, Адвокат, Модератор] (см. panel-of-experts.js)
      const args: Record<string, unknown> = { topic: t, rounds: s.rounds };
      if (s.attachContext && chatContext) args.brief = chatContext;
      if (s.expertsMode === 'personas' && s.participants.length > 0)
        args.participants = s.participants.map(p => p.handle);
      return `/panel-of-experts ${JSON.stringify(args)}`;
    }
    case 'consensus': {
      const flags = [
        s.interviewFirst ? '--interactive' : null,
        s.deliberate ? '--deliberate' : null,
      ].filter(Boolean).join(' ');
      return `/oh-my-claudecode:ralplan ${flags ? flags + ' ' : ''}${quoteTopic(t)}`;
    }
    case 'interview':
      return `/oh-my-claudecode:deep-interview --${s.depth} ${quoteTopic(t)}`;
    case 'autopilot':
      return `/oh-my-claudecode:autopilot ${quoteTopic(t)}`;
    case 'qa':
      return `/oh-my-claudecode:ultraqa --${s.qaTarget}${t ? ` ${t}` : ''}`;
    case 'trace':
      return `/oh-my-claudecode:trace ${quoteTopic(t)}`;
    case 'sci':
      return `/oh-my-claudecode:sciomc ${quoteTopic(t)}`;
    case 'review': {
      // review-consilium.js парсит JSON-args сам; participants — handle персон по порядку осей
      const args: Record<string, unknown> = { lenses: s.reviewLenses, verify: s.reviewVerify };
      if (t) args.target = t;
      if (s.participants.length > 0) args.participants = s.participants.map(p => p.handle);
      return `/review-consilium ${JSON.stringify(args)}`;
    }
    case 'redteam': {
      const args: Record<string, unknown> = { angles: s.attackAngles };
      if (t) args.target = t;
      if (s.participants.length > 0) args.participants = s.participants.map(p => p.handle);
      return `/red-team ${JSON.stringify(args)}`;
    }
    case 'implement': {
      // executors — handle персон-исполнителей (round-robin по под-задачам в team-implement.js)
      const args: Record<string, unknown> = { task: t, worktree: s.implWorktree, verify: s.implVerify };
      if (s.participants.length > 0) args.executors = s.participants.map(p => p.handle);
      return `/team-implement ${JSON.stringify(args)}`;
    }
  }
}

// Детект механики по тексту отправленного сообщения — для бейджа в ленте
// (аналог lib/ultrawork.ts: фронт распознаёт то же правило сам, история хранит исходный текст)
const DETECT_PREFIXES: ReadonlyArray<[string, TeamMechanicId]> = [
  ['/panel-of-experts', 'panel'],
  ['/oh-my-claudecode:ralplan', 'consensus'],
  ['/oh-my-claudecode:deep-interview', 'interview'],
  ['/oh-my-claudecode:autopilot', 'autopilot'],
  ['/oh-my-claudecode:ultraqa', 'qa'],
  ['/oh-my-claudecode:trace', 'trace'],
  ['/oh-my-claudecode:sciomc', 'sci'],
  ['/review-consilium', 'review'],
  ['/red-team', 'redteam'],
  ['/team-implement', 'implement'],
  [DISCUSS_PREFIX, 'discuss'],
];

export function detectTeamMechanic(text: string | null | undefined): TeamMechanicId | null {
  const trimmed = (text ?? '').trimStart();
  for (const [prefix, id] of DETECT_PREFIXES)
    if (trimmed.startsWith(prefix)) return id;
  return null;
}

// Подпись ориентировочной тяжести для зоны настроек
export function costEstimate(id: TeamMechanicId, s: TeamMechanicSettings): string {
  switch (id) {
    case 'discuss': return `≈${s.participants.length + 1} вызова`;
    case 'panel': return `до ${s.rounds * 4 + 1} субагентов`;
    case 'consensus': return 'консенсус до 5 итераций';
    case 'interview': return `${{ quick: '3–5', standard: '5–9', deep: '9+' }[s.depth]} вопросов`;
    case 'autopilot': return s.untilDone ? 'фазы + автопродолжение' : 'до готового кода';
    case 'qa': return 'до 5 циклов починки';
    case 'trace': return '3 гипотезы + опровержение';
    case 'sci': return 'параллельные агенты + синтез';
    case 'review': {
      const n = s.reviewLenses.length || 1;
      return s.reviewVerify ? `${n} осей + проверка находок` : `${n} осей ревью`;
    }
    case 'redteam': return `${s.attackAngles.length || 1} углов атаки + синтез`;
    case 'implement': return s.implWorktree ? 'параллельно в worktree + merge' : 'последовательная раздача';
  }
}

// === Декодер командного хода → человекочитаемое описание (обратный buildTeamTurnText) ===
// Текст механики уходит в историю дословно (его читает модель/CLI), но человеку
// показываем красиво: механика + тема + чипы параметров. Используется в ленте
// (карточка вместо сырого JSON) и в превью списка чатов.

export interface TeamTurnInfo {
  id: TeamMechanicId;
  topic: string;
  chips: string[];
}

const LENS_LABEL = new Map<string, string>(REVIEW_LENSES);
const ANGLE_LABEL = new Map<string, string>(ATTACK_ANGLES);
const DEPTH_LABEL: Record<string, string> = { quick: 'быстро', standard: 'стандарт', deep: 'глубоко' };
const QA_LABEL: Record<string, string> = { tests: 'тесты', build: 'сборка', lint: 'линт', typecheck: 'типы' };

// Русская форма числительного (1 раунд / 2 раунда / 5 раундов)
function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

function parseJsonArgs(text: string): Record<string, unknown> {
  const i = text.indexOf('{');
  if (i === -1) return {};
  try { return JSON.parse(text.slice(i)) as Record<string, unknown>; } catch { return {}; }
}

function quotedTopic(text: string): string {
  return text.match(/"([^"]*)"/)?.[1] ?? '';
}

function handleChips(arr: unknown): string[] {
  return Array.isArray(arr) ? arr.filter((x): x is string => typeof x === 'string').map(h => `@${h}`) : [];
}

export function describeTeamTurn(text: string | null | undefined): TeamTurnInfo | null {
  const id = detectTeamMechanic(text);
  if (!id) return null;
  const t = (text ?? '').trim();
  const chips: string[] = [];
  let topic = '';

  switch (id) {
    case 'discuss': {
      topic = t.slice(DISCUSS_PREFIX.length).replace(/^:\s*/, '').split('\n')[0].trim();
      chips.push(...Array.from(new Set(t.match(/@[a-zA-Z0-9_-]+/g) ?? [])));
      break;
    }
    case 'panel': {
      const a = parseJsonArgs(t);
      topic = String(a.topic ?? '');
      if (a.rounds) chips.push(`${a.rounds} ${plural(Number(a.rounds), 'раунд', 'раунда', 'раундов')}`);
      chips.push(...handleChips(a.participants));
      break;
    }
    case 'review': {
      const a = parseJsonArgs(t);
      topic = String(a.target ?? '') || 'текущий дифф';
      for (const l of (Array.isArray(a.lenses) ? a.lenses : [])) chips.push(LENS_LABEL.get(String(l)) ?? String(l));
      if (a.verify) chips.push('проверка находок');
      chips.push(...handleChips(a.participants));
      break;
    }
    case 'redteam': {
      const a = parseJsonArgs(t);
      topic = String(a.target ?? '') || 'текущее решение';
      for (const g of (Array.isArray(a.angles) ? a.angles : [])) chips.push(ANGLE_LABEL.get(String(g)) ?? String(g));
      chips.push(...handleChips(a.participants));
      break;
    }
    case 'implement': {
      const a = parseJsonArgs(t);
      topic = String(a.task ?? '');
      if (a.worktree) chips.push('в worktree');
      if (a.verify) chips.push('с проверкой');
      chips.push(...handleChips(a.executors));
      break;
    }
    case 'consensus': {
      topic = quotedTopic(t);
      if (t.includes('--interactive')) chips.push('интервью');
      if (t.includes('--deliberate')) chips.push('тщательно');
      break;
    }
    case 'interview': {
      topic = quotedTopic(t);
      const m = t.match(/--(quick|standard|deep)/);
      if (m) chips.push(DEPTH_LABEL[m[1]]);
      break;
    }
    case 'qa': {
      const m = t.match(/--(tests|build|lint|typecheck)/);
      if (m) chips.push(QA_LABEL[m[1]]);
      topic = t.replace(/^\/oh-my-claudecode:ultraqa\s*--\w+\s*/, '').trim();
      break;
    }
    case 'autopilot':
    case 'trace':
    case 'sci':
      topic = quotedTopic(t);
      break;
  }

  return { id, topic, chips };
}

// Короткое превью командного хода для списка чатов («Панель экспертов: тема»)
// вместо сырого JSON в lastMessage. null — не механика (показать оригинал).
export function teamTurnPreview(text: string | null | undefined): string | null {
  const info = describeTeamTurn(text);
  if (!info) return null;
  return `${teamMechanic(info.id).name}${info.topic ? `: ${info.topic}` : ''}`;
}
