import type { CSSProperties, ReactNode, Ref, RefObject } from 'react';
import { X } from 'lucide-react';
import { C, FONT, FS } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { Toolbar, ToolbarIconButton } from './Toolbar';
import { BackButton } from './ui/BackButton';
import { useEscapeClose } from '../lib/useEscapeClose';

// «Просмотр в центре» — ОБЩАЯ оболочка FileViewer (файл) и FeaturePage (страница фичи):
// корень (bgCard, flex-колонка, height 100%), шапка Toolbar (close-контроль слева + имя
// mono 700 + слоты leftExtra/leading/trailing) и Esc-закрытие (useEscapeClose).
//
// Порядок контролов шапки и правило Esc — ОДНИ для обоих: правка оболочки доезжает
// и до файла, и до страницы (Глеб M1: раньше это были дословные копии). FileViewer
// передаёт refs (stripRef/fixedLeftRef) для замера useToolbarOverflow; FeaturePage — нет.
export function CenterViewShell({
  isMobile, onClose,
  closeLabel, closeTitle = 'Закрыть', mobileTitle,
  closeTestId,
  name, nameTitle, nameTestId,
  leftExtra, leading, trailing,
  rootRef, stripRef, fixedLeftRef,
  rootStyle, rootTestId,
  children,
}: {
  // Мобила? (FileViewer — опциональный, undefined = desktop; FeaturePage — всегда boolean).
  isMobile?: boolean;
  // Закрытие (кнопка слева): mobile — BackButton, desktop — X.
  onClose: () => void;
  // Мобила: подпись BackButton («Файлы» / «К чату «…»»).
  closeLabel?: ReactNode;
  // Title desktop-контроля (X) — «Закрыть».
  closeTitle?: string;
  // Title mobile-контроля (BackButton) — «К списку файлов» / «К чату «…»».
  mobileTitle?: string;
  // data-testid close-контроля (тесты FeaturePage: feature-page-close); FileViewer — нет.
  closeTestId?: string;
  // Имя (mono 700, единственный гибкий элемент, ellipsis). Внутренности — span'ы
  // (nameBase+nameExt у файла) или текст (название у страницы).
  name: ReactNode;
  // title имени (полный путь у файла / название у страницы).
  nameTitle?: string;
  // data-testid имени (тесты FeaturePage: feature-page-title); FileViewer — нет.
  nameTestId?: string;
  // Внутри fixedLeftRef, после close (FileViewer: ☰ + back/forward; FeaturePage: пусто).
  leftExtra?: ReactNode;
  // Вне fixedLeftRef, перед именем (FileViewer: FileTypeTile; FeaturePage: глиф).
  leading?: ReactNode;
  // После имени (FileViewer: бейджи+вкладки+действие+правая группа; FeaturePage: тег статуса).
  trailing?: ReactNode;
  // Refs: FileViewer — замер useToolbarOverflow (rootRef/stripRef/fixedLeftRef); FeaturePage — нет.
  rootRef?: Ref<HTMLDivElement>;
  stripRef?: RefObject<HTMLDivElement | null>;
  fixedLeftRef?: RefObject<HTMLDivElement | null>;
  // Доп. стили корня (мержатся после базовых).
  rootStyle?: CSSProperties;
  // data-testid корня (тесты FeaturePage: feature-page); FileViewer — нет.
  rootTestId?: string;
  // Тело (прокрутка, контент) — рендерит вызывающий.
  children: ReactNode;
}) {
  useEscapeClose(onClose, rootRef);
  return (
    <div
      ref={rootRef}
      data-testid={rootTestId}
      style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgCard, position: 'relative', ...rootStyle }}
    >
      <Toolbar isMobile={isMobile}>
        <div ref={stripRef} style={{ display: 'flex', alignItems: 'center', gap: 8, flex: 1, minWidth: 0 }}>
          <div ref={fixedLeftRef} style={{ display: 'flex', alignItems: 'center', gap: 8, flexShrink: 0 }}>
            {isMobile ? (
              <span data-testid={closeTestId}>
                {/* m2 (Майя): полный текст возврата («К чату «…»»/«К списку») — в title И aria-label;
                    видимая подпись остаётся короткой (closeLabel), высота 32 px — паритет с файлом. */}
                <BackButton onClick={onClose} title={mobileTitle} ariaLabel={mobileTitle} style={{ height: 32 }}>
                  <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textSecondary }}>{closeLabel}</span>
                </BackButton>
              </span>
            ) : (
              <span data-testid={closeTestId}>
                <ToolbarIconButton onClick={onClose} title={closeTitle}>
                  <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                </ToolbarIconButton>
              </span>
            )}
            {leftExtra}
          </div>
          {leading}
          {/* m1 (Майя, регрессия оболочки): ellipsis — на ВНУТРЕННЕМ span, а не на flex-родителе.
              textOverflow:ellipsis на display:flex не срабатывает (текст = анонимный flex-item →
              обрезка посреди буквы без «…»). Внешний span — flex-родитель (minWidth:0, чтобы
              цепочка сжималась), внутренний — блок с overflow/ellipsis/nowrap и minWidth:0. */}
          <span
            data-testid={nameTestId}
            title={nameTitle}
            style={{
              display: 'flex', alignItems: 'baseline', flex: '1 1 auto', minWidth: 0,
              fontFamily: FONT.mono, fontWeight: 700, fontSize: FS.base, color: C.textHeading,
            }}
          >
            <span
              style={{
                flex: '1 1 auto', minWidth: 0,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}
            >
              {name}
            </span>
          </span>
          {trailing}
        </div>
      </Toolbar>
      {children}
    </div>
  );
}
