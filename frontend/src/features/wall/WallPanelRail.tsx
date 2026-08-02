// Правая рельса стены — панели ФОКУСНОГО чата и его ПРОЕКТА.
// Группы иконок (как в воркспейсе): [панели проекта: Файлы, Документация,
// Изменения, Задачи, Граф, Команда] [инструменты: Терминал, Сервисы — гейт
// toolsEnabled] [панели сессии: План, Агенты, Персона].
//
// Раскладки панелей на стене нет, механика своя:
//  • hover по иконке → peek-попап (usePanelPeek, паузы 500/160), уход закрывает;
//  • клик (или булавка в попапе) → карточка ЗАКРЕПЛЯЕТСЯ поверх колонок;
//  • смена фокуса перезаполняет контент под новый чат/проект;
//  • ИСКЛЮЧЕНИЕ — Терминал: peek отключён, только закрепление (в карточке живой
//    xterm, а peek рождал бы и убивал его каждым наведением — TerminalView
//    делает dispose на анмаунте).
// Капсула — по контенту (alignSelf), но ЯКОРЬ карточки — полновысотная обёртка
// в WallPage (иначе peek/закреп схлопнулся бы до высоты капсулы).
import { Fragment, type ReactNode } from 'react';
import { Pin, X } from 'lucide-react';
import type { Project, Session } from '../../types';
import { C, FONT, FS, SHADOW, Z } from '../../lib/design';
import { RailCapsule, RailIconButton, RailSep } from '../../components/ui';
import { PanelShell } from '../../components/ui/PanelShell';
import { ICON_STROKE } from '../../components/ui/icons';
import { PANEL_META, SESSION_KEYS, TOOLS_KEYS, type PanelKey } from '../../pages/workspace/panelCatalog';
import { useSessionPanels } from '../../pages/workspace/useSessionPanels';
import { usePanelPeek } from '../../pages/workspace/panelPeek';
import { WALL_PROJECT_KEYS } from './useWallProjectPanels';

// Ширина карточки панели (peek и закреплённой) — как колонка зоны воркспейса
const CARD_W = 380;

export function WallPanelRail({ session, project, projectPanels, pinned, onPin }: {
  // Фокусный чат стены (null — стена пуста, рельса не рисуется вовсе)
  session: Session | null;
  project: Project | undefined;
  // Контент панелей проекта и инструментов фокусного проекта (buildWallProjectPanels
  // + терминал/сервисы из WallPage) — undefined у ключа = панель недоступна
  projectPanels: Partial<Record<PanelKey, ReactNode>>;
  // Закреплённая оверлеем панель (состояние у WallPage — оверлей рисуется там же)
  pinned: PanelKey | null;
  onPin: (k: PanelKey | null) => void;
}) {
  const sessionPanels = useSessionPanels(session, project?.id, project?.rootPath);
  const peeked = usePanelPeek();

  if (!session) return null;

  const contentOf = (k: PanelKey): ReactNode =>
    (SESSION_KEYS.includes(k) ? sessionPanels.content[k] : projectPanels[k]) ?? null;

  const available = (k: PanelKey): boolean => {
    if (SESSION_KEYS.includes(k)) return sessionPanels.visible(k, k === pinned);
    return contentOf(k) != null;
  };

  // Группы иконок; пустые группы PanelRail-механика не рисует — здесь фильтруем сами
  const groups: PanelKey[][] = [
    WALL_PROJECT_KEYS.filter(available),
    TOOLS_KEYS.filter(available),
    SESSION_KEYS.filter(available),
  ].filter(g => g.length > 0);

  // Пока панель закреплена, peek выключен ЦЕЛИКОМ: попап под закреплённой карточкой
  // не виден, а его hold/hide-таймеры дёргали бы чужой слой. Терминал peek не имеет
  // никогда (живой xterm). Хочешь другую панель — клик заменит закреплённую.
  const rawPeek = pinned === null && peeked.key ? peeked.key : null;
  const peek = rawPeek === 'terminal' ? null : rawPeek;

  const card = (k: PanelKey, mode: 'peek' | 'pinned') => {
    const { title, Icon } = PANEL_META[k];
    return (
      <PanelShell
        icon={<Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
        title={title}
        badge={SESSION_KEYS.includes(k) ? sessionPanels.headerBadge(k) : null}
        iconAction={mode === 'peek'
          ? { Icon: Pin, title: 'Закрепить поверх колонок', onClick: () => { peeked.clear(); onPin(k); } }
          : { Icon: X, title: 'Закрыть', onClick: () => onPin(null) }}
        fill
        style={{ width: CARD_W, boxShadow: SHADOW.peek }}
      >
        {contentOf(k)}
      </PanelShell>
    );
  };

  return (
    <>
      {/* Капсула по контенту — рельса не тянется на всю высоту холста */}
      <RailCapsule side="right" style={{ alignSelf: 'flex-start' }}>
        {groups.map((keys, gi) => (
          <Fragment key={gi}>
            {gi > 0 && <RailSep />}
            {keys.map(k => {
              const { title, Icon } = PANEL_META[k];
              const badge = SESSION_KEYS.includes(k) ? sessionPanels.railBadge(k) : null;
              return (
                <RailIconButton
                  key={k}
                  side="right"
                  label={pinned === k ? `Скрыть «${title}»` : title}
                  active={pinned === k}
                  onClick={() => { peeked.clear(); onPin(pinned === k ? null : k); }}
                  // Peek не заводим: при закреплённой панели (см. выше) и у Терминала
                  onHoverChange={h => {
                    if (pinned !== null || k === 'terminal') return;
                    if (h) peeked.show(k); else peeked.hide();
                  }}
                >
                  <span style={{ position: 'relative', display: 'flex' }}>
                    <Icon size={16} strokeWidth={ICON_STROKE} />
                    {badge !== null && (
                      <span style={{
                        position: 'absolute', top: -6, right: -8, minWidth: 13, height: 13,
                        padding: '0 3px', borderRadius: 7, background: C.accent, color: C.onAccent,
                        fontSize: 9, fontWeight: 700, lineHeight: '13px', textAlign: 'center',
                        boxSizing: 'border-box', pointerEvents: 'none',
                      }}>
                        {badge}
                      </span>
                    )}
                  </span>
                </RailIconButton>
              );
            })}
          </Fragment>
        ))}
        <RailSep />
        {/* Чьи панели: подпись проекта фокуса — сразу под иконками (капсула по контенту) */}
        <div style={{
          padding: '2px 0 6px', writingMode: 'vertical-rl', transform: 'rotate(180deg)',
          fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
          maxHeight: 160, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {project ? project.name : 'Чат вне проекта'}
        </div>
      </RailCapsule>

      {/* Peek-попап / закреплённая карточка у правой рельсы поверх колонок.
          Слой держит курсор (hold), чтобы в попап можно было въехать мышью */}
      {(peek || pinned) && (
        <div
          onMouseEnter={peek ? peeked.hold : undefined}
          onMouseLeave={peek ? peeked.hide : undefined}
          style={{
            position: 'absolute', top: 0, bottom: 0, right: '100%', zIndex: Z.dropdown,
            display: 'flex', alignItems: 'stretch', padding: '0 8px', pointerEvents: 'auto',
          }}
        >
          {card((pinned ?? peek)!, pinned ? 'pinned' : 'peek')}
        </div>
      )}
    </>
  );
}
