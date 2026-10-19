import type { ReactNode } from 'react';
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
  return (
    <>
      {/* Центр: display:none при открытой странице (не размонтируется). */}
      <div style={{ flex: 1, display: open ? 'none' : 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        {children}
      </div>
      {/* Страница фичи: сосед центра, показана при open. */}
      {open && featureId && (
        <FeaturePage featureId={featureId} projectId={projectId} isMobile={isMobile} onBack={onBack} />
      )}
    </>
  );
}
