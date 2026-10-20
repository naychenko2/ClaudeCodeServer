import type { ReactNode } from 'react';
import { C, ISLAND } from '../../lib/design';
import { Island } from '../ui/Island';
import { FeaturePage, type FeaturePageBackTarget } from './FeaturePage';

// Центр + страница фичи-сосед (волна 2 «Обзор фич», Ф2.3/C1).
//
// Страница фичи — СОСЕД центра, а не новая ветка раскладки: при открытой странице
// центр (чат/файл/…) прячется `display:none`, а НЕ размонтируется — иначе рвётся
// стрим хода и теряется черновик композера (ChatPanel). Страница рисуется рядом.
//
// Общий паттерн мобильной и десктопной раскладки: одна реализация, не копипаста.
// `children` — контент центра (в мобильной ветке — тернарник чат/задача/персона,
// в десктопе — весь набор центральных режимов DesktopWorkspace).
export function FeatureCenter({ open, featureId, projectId, isMobile, onBack, children }: {
  // Страница открыта (featureId ≠ null).
  open: boolean;
  // Id эпика открытой страницы; null — страница закрыта.
  featureId: string | null;
  // Проект (область запроса индекса); null — чат вне проектов.
  projectId: string | null;
  isMobile: boolean;
  // Исполнитель «← назад»: закрывает страницу и ведёт в target.
  onBack: (t: FeaturePageBackTarget) => void;
  // Контент центра.
  children: ReactNode;
}) {
  // Страница фичи — как открытый файл (Глеб M2 = Майя M1): на десктопе в Island
  // (R14, рамка, тень, зазор centerGap — ровно как centerIsland у файла), на мобиле —
  // без острова (как и файл). Чат под страницей остаётся в DOM (display:none, не unmount).
  const page = (
    <FeaturePage featureId={featureId!} projectId={projectId} isMobile={isMobile} onBack={onBack} />
  );
  return (
    <>
      {/* Центр: display:none при открытой странице (не размонтируется).
          minHeight/minWidth: 0 — иначе flex-ребёнок растёт по контенту ленты и композер уезжает за экран.
          Направление повторяет родителя: на десктопе — строка (split чат|файл), на мобиле — колонка. */}
      <div style={{ flex: 1, minWidth: 0, minHeight: 0, display: open ? 'none' : 'flex', flexDirection: isMobile ? 'column' : 'row', overflow: 'hidden' }}>
        {children}
      </div>
      {/* Страница фичи: сосед центра, показана при open. Десктоп — в Island, мобила — без. */}
      {open && featureId && (
        isMobile
          ? page
          : <Island bg={C.bgMain} style={{ flex: 1, minWidth: 0, margin: `0 ${ISLAND.centerGap}px` }}>{page}</Island>
      )}
    </>
  );
}
