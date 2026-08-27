// Резолвер «релевантной персоны» для лица продукта: персона открытого чата →
// дефолт-персона проекта (в контексте проекта) → личная дефолт-персона; null —
// нейтральные иконки (fallback до онбординга). Используется аватарами
// в AiLauncher / EmptyState / ChatItemView.

import { useEffect, useSyncExternalStore } from 'react';
import type { Persona, Project } from '../types';
import { ensurePersonasLoaded, getPersonaById, usePersonasVersion } from './personas';
import { useMe } from './defaultPersona';
import { useChatPersonaId } from './ai/chatContext';
import { getNav, NAV_CHANGE_EVENT } from './nav';

// Подписка на дефолт-персону проекта текущего раздела (nav вне React) — для резолва
// в глобальных местах вроде FAB AI-хаба. Снимок — примитив (id либо null), чтобы
// useSyncExternalStore не перерисовывал на каждом NAV_CHANGE без смены сути.
function useNavProjectDefault(): string | null {
  const read = () => {
    const nav = getNav();
    const p = nav?.screen === 'project' ? (nav.project as Project | undefined) : undefined;
    return p?.defaultPersonaId ?? null;
  };
  return useSyncExternalStore(
    fn => {
      window.addEventListener(NAV_CHANGE_EVENT, fn);
      window.addEventListener('popstate', fn);
      return () => {
        window.removeEventListener(NAV_CHANGE_EVENT, fn);
        window.removeEventListener('popstate', fn);
      };
    },
    read,
    () => null,
  );
}

// Резолв релевантной персоны. Аргументы — для мест, знающих свой контекст точно
// (пустой чат: personaId сессии + дефолт его проекта); без аргументов контекст
// берётся из глобальных сторов (открытый чат, nav-проект, me) — путь AI-хаба.
// Семантика полей opts: undefined — «не знаю, возьми из глобального стора»,
// null — «знаю, что на этом уровне персоны нет» (уровень пропускается).
export function useContextPersona(opts?: { personaId?: string | null; projectDefaultId?: string | null }): Persona | null {
  usePersonasVersion();
  const me = useMe();
  const chatPersonaId = useChatPersonaId();
  const navProjectDefault = useNavProjectDefault();
  // Стор персон мог быть ещё не загружен (аватары вне раздела персон) — подтягиваем
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  // 1) персона открытого чата
  const chatId = opts?.personaId !== undefined ? opts.personaId : chatPersonaId;
  if (chatId) return getPersonaById(chatId) ?? null;

  // 2) дефолт проекта (в контексте проекта)
  const projectDefault = opts?.projectDefaultId !== undefined ? opts.projectDefaultId : navProjectDefault;
  if (projectDefault) return getPersonaById(projectDefault) ?? null;

  // 3) личная дефолт-персона
  return me.defaultPersonaId ? (getPersonaById(me.defaultPersonaId) ?? null) : null;
}
