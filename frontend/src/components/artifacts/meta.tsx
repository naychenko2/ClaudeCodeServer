// Видимость и счётчики панелей-артефактов сессии.
//
// Раньше здесь лежал реестр на восемь категорий (plan/todos/notes/comments/agents/
// files/links/context) — вкладки старой ArtifactsPanel. Панель разобрали на отдельные
// панели воркспейса, и из сессионных выжили ровно три: план, агенты, контекст
// (SESSION_KEYS в pages/workspace/panelCatalog.ts). Остальные категории вместе
// с их иконками и заголовками остались кодом, который никто не звал: единственный
// потребитель — useSessionPanels — спрашивает бейдж только у agents и context
// (у plan своя логика на месте). Мёртвые ветки удалены 2026-08-21.
//
// Плана хода это не касается: его чек-лист рисует карточка в ленте (TodoPlanView),
// а «на каком я шаге» — подпись индикатора ожидания (planHint в useSessionArtifacts).
import type { SessionArtifacts } from '../../hooks/useSessionArtifacts';

// Категории, у которых спрашивают бейдж. Не путать с PanelKey воркспейса
// (pages/workspace/panelCatalog.ts) — это разные наборы, пересекающиеся по именам.
export type PanelKey = 'agents' | 'context';

export interface PanelBadgeOpts {
  // Собеседник-персона — включает категорию «Контекст»
  personaId?: string | null;
}

export interface PanelBadge {
  visible: boolean;
  // Текст для чипа шапки панельки: '2/5' или null (без счётчика)
  badge: string | null;
}

export function panelBadge(key: PanelKey, a: SessionArtifacts, opts: PanelBadgeOpts): PanelBadge {
  switch (key) {
    case 'agents': {
      // Все агенты сессии (одиночные + внутри workflow); «завершено» = done + error,
      // чтобы счётчик доходил до N/N, когда никто не пашет (см. коммент в старой панели)
      const all = [...a.agents, ...a.workflows.flatMap(w => w.agents)];
      const running = all.filter(x => x.status === 'running').length;
      const total = all.length + a.workflows.filter(w => !w.agents.length).length;
      return { visible: total > 0, badge: `${total - running}/${total}` };
    }
    case 'context':
      return { visible: !!opts.personaId, badge: null };
  }
}
