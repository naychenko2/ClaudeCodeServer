// Общие метаданные проактивности и специальностей персон: иконки/тона триггеров,
// подписи действий, человекопонятные детали правила, плюрализация счётчиков.
// Единственный источник для PersonaAutomationPanel / PersonaPreview / TeamCommandCenter —
// до выноса каждая панель держала свою копию, и новый триггер требовал правок в 3+ местах.

import type { LucideIcon } from 'lucide-react';
import { Clock, FileText, StickyNote, GitBranch, ListChecks, AtSign } from 'lucide-react';
import type { AutomationActionWeight, AutomationTriggerType, PersonaAutomationRule, Project } from '../../types';
import { C } from '../../lib/design';

export const TRIGGER_META: Record<AutomationTriggerType, { label: string; Icon: LucideIcon; bg: string; fg: string; hint: string }> = {
  timer:      { label: 'Таймер',         Icon: Clock,      bg: C.accentLight, fg: C.accent,          hint: 'по расписанию — время или интервал' },
  file:       { label: 'Файлы',          Icon: FileText,   bg: C.bgSelected,  fg: C.textSecondary,   hint: 'новые/изменённые файлы проекта' },
  note:       { label: 'Заметки',        Icon: StickyNote, bg: C.successBg,   fg: C.successText,     hint: 'новые/изменённые заметки' },
  gitCommit:  { label: 'Коммиты',        Icon: GitBranch,  bg: C.infoBg,      fg: C.info,             hint: 'новый коммит в репозитории' },
  taskStatus: { label: 'Статус задачи',   Icon: ListChecks, bg: C.planLight,   fg: C.plan,             hint: 'смена статуса задачи' },
  mention:    { label: 'Упоминание',     Icon: AtSign,     bg: C.warningBg,   fg: C.warning,          hint: '@упоминание в чате' },
};

// Порядок триггеров в сетке шага «Событие» степпера создания
export const TRIGGER_TYPE_ORDER: AutomationTriggerType[] = ['timer', 'file', 'note', 'gitCommit', 'taskStatus', 'mention'];

export const ACTION_META: Record<AutomationActionWeight, { label: string }> = {
  gate: { label: 'Сообщить' },
  work: { label: 'Полный ход' },
};

// Форма аргументов триггера (AutomationTrigger.args): фронт сам собирает её в
// buildArgs (automationForm), сервер возвращает как есть. Структурный тип вместо
// Record<string, any> — тот же JSON-мешок, но с известными ключами.
export interface TriggerArgs {
  schedule?: TriggerArgs;   // timer: вложенное расписание (те же type/time/…)
  type?: string;            // тип расписания (daily/weekdays/weekly/interval)
  time?: string;            // HH:mm
  intervalMinutes?: number;
  weekdays?: number[];      // ISO 1..7
  folder?: string;          // file/gitCommit: папка без проекта
  projectId?: string;
  glob?: string;            // file
  kinds?: string[];         // file: created/changed
  source?: string;          // note: personal | projectId
  tags?: string[];          // note
  section?: string;         // note
  from?: string;            // taskStatus
  to?: string;              // taskStatus
}

// Короткие подписи статусов задач для детали триггера taskStatus
const TASK_STATUS_SHORT: Record<string, string> = {
  Todo: 'К выполнению',
  InProgress: 'В работе',
  Done: 'Готово',
};

// Человекопонятная подпись параметров триггера (часть сводки-подзаголовка карточки).
// projects опциональны: без списка имена проектов в подписи опускаются.
export function triggerDetails(rule: PersonaAutomationRule, projects: Project[] = []): string {
  const a = (rule.trigger.args?.schedule ?? rule.trigger.args ?? {}) as TriggerArgs;
  switch (rule.trigger.type) {
    case 'timer': {
      if (a.intervalMinutes) return `каждые ${a.intervalMinutes} мин`;
      const sched = rule.trigger.args?.schedule as TriggerArgs | undefined;
      const type = sched?.type ?? a.type;
      const kind = type === 'weekdays' ? 'по будням'
        : type === 'weekly' ? 'по выбранным дням'
        : 'ежедневно';
      const time = sched?.time ?? a.time;
      return time ? `${kind} в ${time}` : kind;
    }
    case 'file': {
      const args = (rule.trigger.args ?? {}) as TriggerArgs;
      const glob = String(args.glob ?? '**/*');
      if (typeof args.folder === 'string') return `${glob} · 📁 ${args.folder || 'основная папка'}`;
      const proj = projects.find(p => p.id === args.projectId);
      return proj ? `${glob} · ${proj.name}` : glob;
    }
    case 'note': {
      const args = (rule.trigger.args ?? {}) as TriggerArgs;
      const src = args.source ?? args.projectId;
      if (!src || src === 'personal') return 'личный vault';
      const proj = projects.find(p => p.id === src);
      return proj ? `проект «${proj.name}»` : 'заметки';
    }
    case 'gitCommit': {
      const args = (rule.trigger.args ?? {}) as TriggerArgs;
      if (typeof args.folder === 'string') return `📁 ${args.folder || 'основная папка'}`;
      const proj = projects.find(p => p.id === args.projectId);
      return proj ? proj.name : 'репозиторий проекта';
    }
    case 'taskStatus': {
      const args = (rule.trigger.args ?? {}) as TriggerArgs;
      const parts: string[] = [];
      if (args.from) parts.push(TASK_STATUS_SHORT[String(args.from)] ?? String(args.from));
      if (args.to) parts.push(TASK_STATUS_SHORT[String(args.to)] ?? String(args.to));
      return parts.length ? parts.join(' → ') : 'любая смена';
    }
    case 'mention':
      return 'когда упоминают в чате';
    default:
      return '';
  }
}

// «N правил · M активно» для заголовков секций
export function rulesPlural(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'правило';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'правила';
  return 'правил';
}

export function rulesCounter(rules: PersonaAutomationRule[]): string {
  if (rules.length === 0) return 'нет правил';
  const enabled = rules.filter(r => r.enabled).length;
  return `${rules.length} ${rulesPlural(rules.length)}${enabled ? ` · ${enabled} активно` : ''}`;
}

// Подписи функциональных специальностей больше не хардкодятся здесь —
// единый источник lib/specialties.ts (каталог + функция specialtyLabel).
// Хардкод-карта не знала три профильные роли (backendExecutor / frontendExecutor /
// devopsExecutor) и называла наставника «Ментор» вместо каталожного «Наставник».
