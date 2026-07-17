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

// Короткие подписи статусов задач для детали триггера taskStatus
const TASK_STATUS_SHORT: Record<string, string> = {
  Todo: 'К выполнению',
  InProgress: 'В работе',
  Done: 'Готово',
};

// Человекопонятная подпись параметров триггера (часть сводки-подзаголовка карточки).
// projects опциональны: без списка имена проектов в подписи опускаются.
export function triggerDetails(rule: PersonaAutomationRule, projects: Project[] = []): string {
  const a = ((rule.trigger.args?.schedule as Record<string, any>) ?? rule.trigger.args ?? {}) as Record<string, any>;
  switch (rule.trigger.type) {
    case 'timer': {
      if (a.intervalMinutes) return `каждые ${a.intervalMinutes} мин`;
      const sched = rule.trigger.args?.schedule as Record<string, any> | undefined;
      const type = sched?.type ?? a.type;
      const kind = type === 'weekdays' ? 'по будням'
        : type === 'weekly' ? 'по выбранным дням'
        : 'ежедневно';
      const time = sched?.time ?? a.time;
      return time ? `${kind} в ${time}` : kind;
    }
    case 'file': {
      const args = (rule.trigger.args ?? {}) as Record<string, any>;
      const glob = String(args.glob ?? '**/*');
      if (typeof args.folder === 'string') return `${glob} · 📁 ${args.folder || 'основная папка'}`;
      const proj = projects.find(p => p.id === args.projectId);
      return proj ? `${glob} · ${proj.name}` : glob;
    }
    case 'note': {
      const args = (rule.trigger.args ?? {}) as Record<string, any>;
      const src = args.source ?? args.projectId;
      if (!src || src === 'personal') return 'личный vault';
      const proj = projects.find(p => p.id === src);
      return proj ? `проект «${proj.name}»` : 'заметки';
    }
    case 'gitCommit': {
      const args = (rule.trigger.args ?? {}) as Record<string, any>;
      if (typeof args.folder === 'string') return `📁 ${args.folder || 'основная папка'}`;
      const proj = projects.find(p => p.id === args.projectId);
      return proj ? proj.name : 'репозиторий проекта';
    }
    case 'taskStatus': {
      const args = (rule.trigger.args ?? {}) as Record<string, any>;
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

// Подписи функциональных специальностей (роли оркестрации конвейера + расширенные роли команды)
export const SPECIALTY_LABEL: Record<string, string> = {
  analyst: 'Аналитик', planner: 'Планировщик', reviewer: 'Ревьюер', executor: 'Исполнитель',
  secretary: 'Секретарь', coordinator: 'Координатор', mentor: 'Ментор', designer: 'Дизайнер',
  consultant: 'Консультант', librarian: 'Библиотекарь', tester: 'Тестировщик',
};
