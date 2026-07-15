// Реестр механик «Обсудить с командой»: карточки раскрывашки композера,
// настройки и билдеры текста хода. Под капотом каждая механика — обычное
// сообщение со скиллом (/panel-of-experts, /oh-my-claudecode:*) или
// промпт-обвязкой persona_ask; оркестрирует CLI, бэкенд не участвует.
import type { LucideIcon } from 'lucide-react';
import {
  BadgeCheck, FlaskConical, GraduationCap, HelpCircle, MessagesSquare,
  Rocket, Route, Scale,
} from 'lucide-react';

export type TeamMechanicId =
  | 'discuss' | 'panel' | 'consensus' | 'interview'
  | 'autopilot' | 'qa' | 'trace' | 'sci';

export type TeamMechanicGroup = 'Обсудить' | 'Спланировать' | 'Сделать' | 'Проверить' | 'Исследовать';

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
    id: 'qa', group: 'Проверить', name: 'QA-цикл', icon: BadgeCheck, cost: 2,
    desc: 'Чинит до зелёной проверки', placeholder: 'Комментарий к прогону (необязательно)…',
    requiredSkill: 'oh-my-claudecode:ultraqa',
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
      return `/oh-my-claudecode:ralplan ${flags ? flags + ' ' : ''}"${t}"`;
    }
    case 'interview':
      return `/oh-my-claudecode:deep-interview --${s.depth} "${t}"`;
    case 'autopilot':
      return `/oh-my-claudecode:autopilot "${t}"`;
    case 'qa':
      return `/oh-my-claudecode:ultraqa --${s.qaTarget}${t ? ` ${t}` : ''}`;
    case 'trace':
      return `/oh-my-claudecode:trace "${t}"`;
    case 'sci':
      return `/oh-my-claudecode:sciomc "${t}"`;
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
  }
}
