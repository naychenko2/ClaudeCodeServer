// Резолв контекста происхождения чата (задача/автоматизация) — единая точка для
// плашек списка чатов, шапки чата и панели артефактов. Полагается на уже загруженные
// сторы задач/персон (ensureTasksLoaded/ensurePersonasLoaded должны быть вызваны
// где-то выше в дереве компонентов).
import type { Session } from '../types';
import { getTaskById, openTaskInSection } from './tasks';
import { getPersonaById } from './personas';

export interface ChatOriginInfo {
  kind: 'task' | 'automation';
  label: string;
  tone: 'info' | 'warning';
  // Отсутствует, если цель (задача/правило) удалена — бейдж показывается, но некликабелен
  onOpen?: () => void;
}

export function resolveChatOrigin(session: Session): ChatOriginInfo | null {
  if (session.origin === 'task') {
    const task = session.taskId ? getTaskById(session.taskId) : undefined;
    if (!task) return { kind: 'task', label: 'Задача (удалена)', tone: 'info' };
    return { kind: 'task', label: `Задача: ${task.title}`, tone: 'info', onOpen: () => openTaskInSection(task) };
  }

  if (session.origin === 'automation') {
    const persona = session.personaId ? getPersonaById(session.personaId) : undefined;
    const rule = persona?.automationRules?.find(r => r.id === session.automationRuleId);
    if (!rule) return { kind: 'automation', label: 'Автоматизация (правило удалено)', tone: 'warning' };
    return {
      kind: 'automation',
      label: `Автоматизация: ${rule.name}`,
      tone: 'warning',
      onOpen: () => window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url: `#/personas/${session.personaId}` } })),
    };
  }

  return null;
}
