// Зона персоны — единая точка правды на фронте (зеркало бэкового PersonaZone).
// Ручные сравнения scope с 'project'/'sphere' вне этого файла не плодим: их ловит
// PersonaZoneGuardTests. Зона сферы fail-closed: нет сферы у проекта — персона не видна.
import type { Persona } from '../types';

type ZonePersona = Pick<Persona, 'scope' | 'projectId' | 'sphereId'>;

// Сфера проекта по его id (undefined — проект вне сферы или неизвестен)
export type SphereOf = (projectId: string) => string | undefined;

export const isProjectPersona = (p: Pick<Persona, 'scope'> | null | undefined): boolean => p?.scope === 'project';
export const isSpherePersona = (p: Pick<Persona, 'scope'> | null | undefined): boolean => p?.scope === 'sphere';
export const isGlobalPersona = (p: Pick<Persona, 'scope'> | null | undefined): boolean => p?.scope === 'global';

// Персона узкой зоны — проектной или сферной (не глобальная)
export const isZonedPersona = (p: Pick<Persona, 'scope'> | null | undefined): boolean => !!p && p.scope !== 'global';

// Проект «своей» команды: projectId проектной персоны, иначе undefined
export function ownProjectId(p: ZonePersona | null | undefined): string | undefined {
  return p?.scope === 'project' ? p.projectId : undefined;
}

// Проектная персона именно этого проекта (членство в команде проекта, без сфер)
export function isProjectTeam(p: ZonePersona, projectId: string | undefined): boolean {
  return p.scope === 'project' && p.projectId === projectId;
}

// Персона команды именно этой сферы
export function isSphereTeam(p: ZonePersona, sphereId: string | undefined): boolean {
  return p.scope === 'sphere' && !!p.sphereId && p.sphereId === sphereId;
}

// Видна ли персоне работа в проекте projectId. Глобальная видна везде; проектная — в своём;
// сферная — в проектах своей сферы (без sphereOf или без проекта — не видна)
export function visibleIn(p: ZonePersona, projectId: string | undefined, sphereOf?: SphereOf): boolean {
  switch (p.scope) {
    case 'global': return true;
    case 'project': return !!projectId && p.projectId === projectId;
    case 'sphere': {
      if (!projectId || !p.sphereId || !sphereOf) return false;
      return sphereOf(projectId) === p.sphereId;
    }
    default: return false;
  }
}

// Подписи зоны: full — для строк и подсказок, short — для чипа визитки
export interface ZoneNames {
  projectName?: (projectId: string | undefined) => string | undefined;
  sphereName?: (sphereId: string | undefined) => string | undefined;
}

export function zoneLabel(p: ZonePersona, names: ZoneNames = {}, short = false): string {
  switch (p.scope) {
    case 'project': {
      const name = names.projectName?.(p.projectId);
      return short ? `Проект${name ? ` · ${name}` : ''}` : (name ? `В проекте «${name}»` : 'В проекте');
    }
    case 'sphere': {
      const name = names.sphereName?.(p.sphereId);
      return short ? `Сфера${name ? ` · ${name}` : ''}` : (name ? `В сфере «${name}»` : 'В сфере');
    }
    default:
      return short ? 'Все данные' : 'Во всех моих данных';
  }
}

// Значение единого селекта зоны: 'global' | 'sphere:{id}' | 'project:{id}'
export function zoneToValue(p: ZonePersona): string {
  if (p.scope === 'sphere') return `sphere:${p.sphereId ?? ''}`;
  if (p.scope === 'project') return `project:${p.projectId ?? ''}`;
  return 'global';
}

export function zoneFromValue(value: string): { scope: Persona['scope']; projectId?: string; sphereId?: string } {
  if (value.startsWith('sphere:')) return { scope: 'sphere', sphereId: value.slice(7) };
  if (value.startsWith('project:')) return { scope: 'project', projectId: value.slice(8) };
  return { scope: 'global' };
}

// Событие «открыть выбор собеседника» — слушает CompanionSelector композера
export const OPEN_COMPANION_PICKER_EVENT = 'cc-open-companion-picker';

// Отказ хода: проект чата вышел из сферы персоны («Проект больше не в сфере «…» — смените
// собеседника»). Бэк пока не ставит action, поэтому узнаём и по тексту, и по action
export function isZoneRefusal(item: { text: string; action?: string | null }): boolean {
  return item.action === 'change-companion'
    || /^Проект больше не в сфере .+ — смените собеседника$/.test(item.text.trim());
}

// Строка о последствиях выбора зоны (форма и мастер персоны); projectCount — проектов в сфере
export function zoneHint(zone: Pick<Persona, 'scope'>, projectCount: number): string {
  switch (zone.scope) {
    case 'sphere':
      return `Персона работает во всех проектах сферы — сейчас их ${projectCount} — и в тех, что появятся в ней позже. Пишет в память сферы.`;
    case 'project':
      return 'Персона видит только этот проект.';
    default:
      return 'Персона видит все ваши проекты и сферы, а в проекте сферы — и память этой сферы.';
  }
}
