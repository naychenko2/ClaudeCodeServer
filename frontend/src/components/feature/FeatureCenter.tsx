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
export function FeatureCenter({ open, featureId, projectId, isMobile, onBack, onOpenFile, yielded = false, children }: {
  // Страница открыта (featureId ≠ null).
  open: boolean;
  // Id эпика открытой страницы; null — страница закрыта.
  featureId: string | null;
  // Проект (область запроса индекса); null — чат вне проектов.
  projectId: string | null;
  isMobile: boolean;
  // Исполнитель «← назад»: закрывает страницу и ведёт в target.
  onBack: (t: FeaturePageBackTarget) => void;
  // Открыть файл проекта (существующий просмотр; «Открыть макет» в «Планы и макеты»).
  onOpenFile?: (path: string) => void;
  // Страница уступает центр контенту (файл): центр показан, страница скрыта, НО
  // остаётся смонтированной — состояние (вкладка, выбранный документ) переживает
  // возврат. Как «остальные переходы»: файл вытесняет страницу, закрыл файл —
  // страница вернулась (DesktopWorkspace: yielded = файл открыт).
  yielded?: boolean;
  // Контент центра.
  children: ReactNode;
}) {
  // Страница фичи — как открытый файл (Глеб M2 = Майя M1): на десктопе в Island
  // (R14, рамка, тень, зазор centerGap — ровно как centerIsland у файла), на мобиле —
  // без острова (как и файл). Чат под страницей остаётся в DOM (display:none, не unmount).
  const page = (
    <FeaturePage featureId={featureId!} projectId={projectId} isMobile={isMobile} onBack={onBack} onOpenFile={onOpenFile} />
  );
  // Видимость центра и страницы — взаимоисключающая: страница открыта, но не
  // уступила (yielded=false) → центр скрыт, страница видна; уступила (yielded=true)
  // → центр виден (файл), страница скрыта; страница закрыта (open=false) → центр
  // виден, страница не смонтирована.
  const centerHidden = open && !yielded;
  const pageMounted = open && featureId != null;
  const pageVisible = pageMounted && !yielded;
  return (
    <>
      {/* Центр: display:none при открытой (не уступившей) странице (не размонтируется).
          minHeight/minWidth: 0 — иначе flex-ребёнок растёт по контенту ленты и композер уезжает за экран.
          Направление повторяет родителя: на десктопе — строка (split чат|файл), на мобиле — колонка. */}
      <div data-testid="feature-center-center" style={{ flex: 1, minWidth: 0, minHeight: 0, display: centerHidden ? 'none' : 'flex', flexDirection: isMobile ? 'column' : 'row', overflow: 'hidden' }}>
        {children}
      </div>
      {/* Страница фичи: сосед центра, смонтирована при open. Десктоп — в Island, мобила — без.
          Обёртка flex:1 — чтобы Island/страница (flex:1) заполняли её; display:none при
          yielded прячет страницу, не размонтируя (состояние сохраняется). */}
      {pageMounted && (
        <div data-testid="feature-center-page" style={{ flex: 1, minWidth: 0, display: pageVisible ? 'flex' : 'none' }}>
          {isMobile
            ? page
            : <Island bg={C.bgMain} style={{ flex: 1, minWidth: 0, margin: `0 ${ISLAND.centerGap}px` }}>{page}</Island>}
        </div>
      )}
    </>
  );
}
