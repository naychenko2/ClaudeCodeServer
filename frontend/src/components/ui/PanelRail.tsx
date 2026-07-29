import { Fragment, type ReactNode } from 'react';
import { ChevronsLeft, ChevronsRight, Columns2, Rows2, Square, type LucideIcon } from 'lucide-react';
import { C, FONT, ISLAND } from '../../lib/design';
import { ICON_STROKE } from './icons';
import { ToolbarIconButton } from '../Toolbar';

// Вертикальная рельса иконок у края окна — полукапсула-остров, из которой
// открываются панели-карточки. Общая для ОБЕИХ зон: RightPanelStack (инструменты
// проекта и сессии) и LeftPanelStack (сайдбары разделов).
//
// Зеркальность задаётся одним пропом side: он разворачивает скругления, бордер,
// зазор до центра и стрелки сворачивания (они всегда указывают К краю окна).
// Раньше это были два дословно скопированных блока в обоих стеках — правка
// одного молча расходилась с другим.

// Ширина рельсы и зазор между рельсой и зоной панелей. Значения общие: рельсы
// обязаны быть зеркальны, иначе одна зона визуально «толще» другой.
export const RAIL_W = 40;
export const RAIL_GAP = 4;

// Одна иконка рельсы. key нужен только React'у — сам ключ панели рельса не
// трактует, всю логику (что открыто, что видно) решает вызывающий стек.
export interface RailItem {
  key: string;
  title: string;
  Icon: LucideIcon;
  active: boolean;
  // Число в кружке над иконкой. null/0 — кружок не рисуем.
  badge?: number | null;
  onClick: () => void;
}

interface Props {
  // Сторона окна. Разворачивает капсулу и стрелки сворачивания.
  side: 'left' | 'right';
  // Группы иконок сверху вниз; между НЕПУСТЫМИ группами — сепаратор. Пустые
  // отбрасываются: так разделитель сам исчезает, когда группа скрыта целиком
  // (напр. сессионные кнопки без плана и агентов).
  groups: RailItem[][];
  // false — рельса плавно схлопывается (width→0, opacity→0), оставаясь в DOM.
  // Анимация синхронна с появлением/скрытием панелей рядом.
  visible?: boolean;
  // Зазор со стороны центра. Обычно его даёт зона панелей (её сплиттер или
  // крайний разделитель колонок), но при закрытых панелях задаётся здесь —
  // иначе рельса липнет к контенту.
  gapToCenter?: number;
  // Тумблер режима зоны (сверху). Не передан — не рендерится: так компактный
  // режим правой зоны и одно-панельная левая обходятся без него.
  modeToggle?: { soloMode: boolean; onToggle: () => void };
  // Кнопка «свернуть все» (снизу). Не передана — не рендерится.
  collapse?: { collapsed: boolean; disabled: boolean; onToggle: () => void };
}

// Разделитель групп внутри рельсы. margin разный у верхнего/групповых/нижнего —
// отсюда параметр, чтобы вертикальный ритм иконок остался прежним.
function RailSep({ margin }: { margin: string }) {
  return <div style={{ width: 22, height: 1, background: C.border, flexShrink: 0, margin }} />;
}

export function PanelRail({ side, groups, visible = true, gapToCenter = 0, modeToggle, collapse }: Props) {
  const isLeft = side === 'left';

  const renderItem = (it: RailItem): ReactNode => (
    <ToolbarIconButton key={it.key} onClick={it.onClick} active={it.active} title={it.title}>
      <div style={{ position: 'relative', display: 'flex' }}>
        <it.Icon size={17} strokeWidth={ICON_STROKE} />
        {it.badge ? (
          <span style={{
            position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
            borderRadius: 7, background: C.accent, color: C.onAccent,
            fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
          }}>
            {it.badge}
          </span>
        ) : null}
      </div>
    </ToolbarIconButton>
  );

  // Пустые группы отбрасываем ДО отрисовки сепараторов — иначе между скрытой
  // группой и соседней остался бы висячий разделитель.
  const shownGroups = groups.filter(g => g.length > 0);

  return (
    <div style={{
      width: visible ? RAIL_W : 0,
      opacity: visible ? 1 : 0,
      pointerEvents: visible ? 'auto' : 'none',
      transition: 'width 0.15s ease-out, opacity 0.12s ease-out',
      flexShrink: 0, alignSelf: 'flex-start',
      display: 'flex', flexDirection: 'column', alignItems: 'center',
      // Тон шапок островов и сайдбаров — единая «оправа» интерфейса
      gap: 6, paddingTop: 7, paddingBottom: 7, background: C.bgMain,
      borderTop: `1px solid ${C.border}`, borderBottom: `1px solid ${C.border}`,
      boxSizing: 'border-box', overflow: 'hidden',
      // Рельса — полукапсула-остров у края окна: тень как у остальных островов
      boxShadow: ISLAND.shadow,
      // Скруглена и обведена только сторона, обращённая к центру; прижатая к
      // краю окна — прямая и без бордера.
      ...(isLeft
        ? {
            borderRight: `1px solid ${C.border}`,
            borderTopRightRadius: ISLAND.radius, borderBottomRightRadius: ISLAND.radius,
            marginRight: gapToCenter,
          }
        : {
            borderLeft: `1px solid ${C.border}`,
            borderTopLeftRadius: ISLAND.radius, borderBottomLeftRadius: ISLAND.radius,
            marginLeft: gapToCenter,
          }),
    }}>
      {/* Переключатель режима зоны. Справа multi — это раскладка КОЛОНКАМИ,
          слева колонок нет и панели стакаются СТОПКОЙ: отсюда разные иконка
          и подсказка, чтобы кнопка не обещала несуществующего. */}
      {modeToggle && (() => {
        const MultiIcon = isLeft ? Rows2 : Columns2;
        const multiWord = isLeft ? 'Панели стопкой' : 'Раскладка колонками';
        const multiHint = isLeft ? 'чтобы открывать несколько' : 'для раскладки колонками';
        return (
          <>
            <ToolbarIconButton
              onClick={modeToggle.onToggle}
              title={modeToggle.soloMode
                ? `Одна панель — нажмите, ${multiHint}`
                : `${multiWord} — нажмите для режима одной панели`}
            >
              {modeToggle.soloMode
                ? <Square size={15} strokeWidth={ICON_STROKE} />
                : <MultiIcon size={15} strokeWidth={ICON_STROKE} />}
            </ToolbarIconButton>
            <RailSep margin="1px 0 2px" />
          </>
        );
      })()}

      {shownGroups.map((group, gi) => (
        <Fragment key={gi}>
          {gi > 0 && <RailSep margin="2px 0" />}
          {group.map(renderItem)}
        </Fragment>
      ))}

      {/* Свернуть все панели / вернуть спрятанный набор как был. Стрелки всегда
          указывают К краю окна при сворачивании и от него — при разворачивании. */}
      {collapse && (() => {
        const CollapseIcon = collapse.collapsed
          ? (isLeft ? ChevronsRight : ChevronsLeft)
          : (isLeft ? ChevronsLeft : ChevronsRight);
        return (
          <>
            <RailSep margin="2px 0 1px" />
            <div style={{ opacity: collapse.disabled ? 0.3 : 1 }}>
              <ToolbarIconButton
                onClick={collapse.onToggle}
                disabled={collapse.disabled}
                title={collapse.collapsed ? 'Открыть свёрнутые панели' : 'Свернуть все панели'}
              >
                <div style={{ display: 'flex', color: collapse.disabled ? C.textMuted : undefined }}>
                  <CollapseIcon size={16} strokeWidth={ICON_STROKE} />
                </div>
              </ToolbarIconButton>
            </div>
          </>
        );
      })()}
    </div>
  );
}
