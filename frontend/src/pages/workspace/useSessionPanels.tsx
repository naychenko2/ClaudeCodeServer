// Панели ТЕКУЩЕЙ СЕССИИ (План, Агенты, Персона) — контент, видимость иконки и
// числа-кружки.
//
// Раньше всё это жило внутри правой зоны, которая сама дёргала useSessionArtifacts.
// Из-за этого сессионные панели были прибиты к правой рельсе: перенести их влево
// было нечем — там просто не существовало их контента. Хук поднимает сборку на
// уровень экрана, и обе зоны получают эти панели наравне с остальными.
import type { ReactNode } from 'react';
import type { Session } from '../../types';
import { C, FONT } from '../../lib/design';
import { plural } from '../../lib/spend';
import { useSessionArtifacts } from '../../hooks/useSessionArtifacts';
import { PlanSection } from '../../components/artifacts/PlanSection';
import { AgentsSection } from '../../components/artifacts/AgentsSection';
import { ContextSection } from '../../components/artifacts/ContextSection';
// В meta.tsx свой PanelKey (категории артефактов) — берём оттуда только panelBadge,
// а ключи панелей остаются из реестра зон
import { panelBadge } from '../../components/artifacts/meta';
import type { PanelKey, RailBadgeInfo } from './panelCatalog';

// Пустой стейт панельки (открыта, но контента ещё нет)
function emptyPanel(text: string): ReactNode {
  return (
    <div style={{ padding: '20px 14px', fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, textAlign: 'center' }}>
      {text}
    </div>
  );
}

export interface SessionPanels {
  // Контент сессионных панелей — вливается в общий набор panels зоны
  content: Partial<Record<PanelKey, ReactNode>>;
  // Показывать ли иконку в рельсе. Сессионные кнопки видны ТОЛЬКО когда есть что
  // открывать (План — если был план, Агенты — если есть контент, Персона — если
  // собеседник персона); иначе иконка скрыта целиком, а с ней и разделитель групп.
  visible: (k: PanelKey, isOpen: boolean) => boolean;
  // Кружки над иконкой рельсы: primary — «сколько требует внимания», secondary —
  // второй индикатор (у сессионных не используется), hint — расшифровка в тултипе
  railBadge: (k: PanelKey) => RailBadgeInfo | null;
  // Значок в шапке карточки
  headerBadge: (k: PanelKey) => string | null;
}

export function useSessionPanels(session: Session | null, projectId?: string, rootPath?: string): SessionPanels {
  const sessionId = session?.id ?? null;
  // Артефакты сессии питают План и Агентов (бейджи + содержимое панелек).
  // Персона (context) данные тянет сама через ContextSection.
  const artifacts = useSessionArtifacts(sessionId, projectId, rootPath ?? '', null);
  const plansCount = artifacts.plans.length;
  const badgeOpts = { personaId: session?.personaId ?? null };

  const personaId = session?.personaId;

  return {
    content: {
      plan: plansCount > 0
        ? <PlanSection plans={artifacts.plans} projectId={projectId} />
        : emptyPanel('План появится после ExitPlanMode в чате'),
      agents: <AgentsSection agents={artifacts.agents} workflows={artifacts.workflows} />,
      context: personaId
        ? <ContextSection personaId={personaId} sessionId={sessionId} />
        : emptyPanel('Доступно в чате с персоной'),
    },

    visible: (k, isOpen) => {
      if (k === 'plan') return plansCount > 0 || isOpen;
      if (k === 'agents' || k === 'context') return panelBadge(k, artifacts, badgeOpts).visible || isOpen;
      return true;
    },

    // План — неодобренные (status ≠ approved), Агенты — открытые (running);
    // у Персоны счётчика нет. hint расшифровывает число в тултипе кнопки рельсы
    railBadge: k => {
      if (k === 'plan') {
        const n = artifacts.plans.filter(p => p.status !== 'approved').length;
        return n > 0 ? { primary: n, hint: `${n} ${plural(n, 'ждёт одобрения', 'ждут одобрения', 'ждут одобрения')}` } : null;
      }
      if (k === 'agents') {
        const n = [...artifacts.agents, ...artifacts.workflows.flatMap(w => w.agents)]
          .filter(a => a.status === 'running').length;
        return n > 0 ? { primary: n, hint: `${n} ${plural(n, 'выполняется', 'выполняются', 'выполняются')}` } : null;
      }
      return null;
    },

    headerBadge: k => {
      if (k === 'plan') return plansCount > 1 ? `${plansCount}` : null;
      if (k === 'agents' || k === 'context') return panelBadge(k, artifacts, badgeOpts).badge;
      return null;
    },
  };
}
