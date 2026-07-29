import { Fragment, useState } from 'react';
import { ChevronsLeft, ChevronsRight, Columns2, Square, X, type LucideIcon } from 'lucide-react';
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

// Иконка панели. У ОТКРЫТОЙ панели под курсором иконка подменяется на закрывающую:
// это и есть кнопка «закрыть». Своего крестика в шапке у панели больше нет —
// клик по активной иконке и раньше закрывал панель, теперь он ещё и выглядит
// как закрытие, а шапка не тратит место на дубль.
// hover держим здесь, а не в IconButton: тому он нужен только для собственных
// цветов и наружу не отдаётся.
// soleIcon — рельса из ОДНОЙ иконки: тогда открытая панель показывает не себя, а
// сворачивание, и постоянно, а не по наведению. Выбирать в такой зоне не из чего,
// поэтому иконка панели там ничего не сообщает, а активная подсветка врала бы про
// выбор; кнопки «свернуть все» внизу при одной панели тоже нет — её роль забирает
// эта же кнопка.
function RailButton({ item, soleIcon: SoleIcon }: { item: RailItem; soleIcon?: LucideIcon }) {
  const [hover, setHover] = useState(false);
  const sole = !!SoleIcon && item.active;
  const closing = !sole && item.active && hover;
  const Icon = sole ? SoleIcon : closing ? X : item.Icon;
  return (
    <span
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{ display: 'flex' }}
    >
      <ToolbarIconButton
        onClick={item.onClick}
        active={item.active && !sole}
        title={item.active ? `Скрыть «${item.title}»` : item.title}
      >
        <div style={{ position: 'relative', display: 'flex' }}>
          <Icon size={17} strokeWidth={ICON_STROKE} />
          {/* Кружок с числом при закрывающей иконке прячем: рядом с «закрыть» счётчик
              читается как часть действия, а не как содержимое панели */}
          {item.badge && !closing && !sole ? (
            <span style={{
              position: 'absolute', top: -6, right: -7, minWidth: 14, height: 14, padding: '0 3px',
              borderRadius: 7, background: C.accent, color: C.onAccent,
              fontFamily: FONT.sans, fontSize: 9, fontWeight: 700, lineHeight: '14px', textAlign: 'center',
            }}>
              {item.badge}
            </span>
          ) : null}
        </div>
      </ToolbarIconButton>
    </span>
  );
}

export function PanelRail({ side, groups, visible = true, gapToCenter = 0, modeToggle, collapse }: Props) {
  const isLeft = side === 'left';

  // Пустые группы отбрасываем ДО отрисовки сепараторов — иначе между скрытой
  // группой и соседней остался бы висячий разделитель.
  const shownGroups = groups.filter(g => g.length > 0);

  // Вся рельса — одна иконка: её открытая панель показывает сворачивание (стрелки
  // к краю окна, как у кнопки «свернуть все»). Иначе иконки обычные, а крестик
  // подставляется по наведению.
  const soleItem = shownGroups.reduce((n, g) => n + g.length, 0) === 1;
  const soleIcon: LucideIcon | undefined = soleItem ? (isLeft ? ChevronsLeft : ChevronsRight) : undefined;

  return (
    <div style={{
      width: visible ? RAIL_W : 0,
      opacity: visible ? 1 : 0,
      pointerEvents: visible ? 'auto' : 'none',
      transition: 'width 0.15s ease-out, opacity 0.12s ease-out',
      flexShrink: 0, alignSelf: 'flex-start',
      display: 'flex', flexDirection: 'column', alignItems: 'center',
      // Тон шапок островов и сайдбаров — единая «оправа» интерфейса.
      // Вертикальный отступ подобран так, чтобы капсула с ОДНОЙ иконкой была
      // ровно в высоту шапки панели (ISLAND.headerH), а центр первой кнопки
      // сел на линию её заголовка: рельса теперь всегда на виду рядом с шапкой.
      gap: 6, paddingTop: 4, paddingBottom: 4, background: C.bgMain,
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
      {/* Переключатель режима зоны. multi — раскладка КОЛОНКАМИ; обе зоны её
          умеют, поэтому иконка и подсказка у них одни и те же. */}
      {modeToggle && (
        <>
          <ToolbarIconButton
            onClick={modeToggle.onToggle}
            title={modeToggle.soloMode
              ? 'Одна панель — нажмите для раскладки колонками'
              : 'Раскладка колонками — нажмите для режима одной панели'}
          >
            {modeToggle.soloMode
              ? <Square size={15} strokeWidth={ICON_STROKE} />
              : <Columns2 size={15} strokeWidth={ICON_STROKE} />}
          </ToolbarIconButton>
          <RailSep margin="1px 0 2px" />
        </>
      )}

      {shownGroups.map((group, gi) => (
        <Fragment key={gi}>
          {gi > 0 && <RailSep margin="2px 0" />}
          {group.map(it => <RailButton key={it.key} item={it} soleIcon={soleIcon} />)}
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
