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
    if (!persona || !rule) return { kind: 'automation', label: 'Автоматизация (правило удалено)', tone: 'warning' };
    // Персона может быть глобальной (раздел «Персоны») или проектной (вкладка «Команда»
    // внутри её проекта) — у каждой свой URL-раздел. Суффикс /automation — сразу открыть
    // вкладку «Проактивность» в студии персоны, а не общий профиль.
    const url = persona.scope === 'project' && persona.projectId
      ? `#/project/${persona.projectId}/persona/${persona.id}/automation`
      : `#/personas/${persona.id}/automation`;
    return {
      kind: 'automation',
      label: `Автоматизация: ${rule.name}`,
      tone: 'warning',
      onOpen: () => window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url } })),
    };
  }

  return null;
}

// === Персистентность фильтра «Тип чата» (localStorage) ===
// Раздельно по областям: глобальный список чатов (scopeKey='global') и каждый проект
// отдельно (scopeKey=projectId) — переключение проекта не должно смешивать их настройки.

const VISIBLE_ORIGINS_PREFIX = 'cc_chat_visible_origins:';
const DEFAULT_VISIBLE_ORIGINS: Session['origin'][] = ['manual', 'automation'];

export function loadVisibleOrigins(scopeKey: string): Set<Session['origin']> {
  try {
    const raw = localStorage.getItem(VISIBLE_ORIGINS_PREFIX + scopeKey);
    if (raw) {
      const arr = JSON.parse(raw) as Session['origin'][];
      if (Array.isArray(arr)) return new Set(arr);
    }
  } catch { /* повреждённое значение — дефолт */ }
  return new Set(DEFAULT_VISIBLE_ORIGINS);
}

export function persistVisibleOrigins(scopeKey: string, v: Set<Session['origin']>): void {
  try { localStorage.setItem(VISIBLE_ORIGINS_PREFIX + scopeKey, JSON.stringify([...v])); } catch { /* квота/приватный режим */ }
}
