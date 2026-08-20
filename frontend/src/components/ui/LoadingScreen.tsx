import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { C, FONT, FS, SHADOW, SP, Z } from '../../lib/design';
import { PageCanvas } from './PageCanvas';

// Заставка, пока приложение поднимается: проверка входа при старте и подгрузка
// отложенных экранов. Раньше на это время показывался пустой прямоугольник —
// при каждом холодном старте PWA получалась вспышка пустоты.
//
// Три яруса, каждый со своей задержкой появления, — чтобы заставка не мельтешила
// на быстром старте и договаривала только когда ждать действительно приходится:
//   знак + имя  — через 260мс (.cc-boot-veil), дышит медленно: «процесс жив»;
//   строка hint — через 1.4с (.cc-boot-hint): что именно сейчас делается.
// Крутить здесь шутливые фразы (как WaitingIndicator в чате) намеренно не стали:
// заставку видят часто и мельком, и остроты на пятый раз работают против.
// overlay — показать заставку ПОВЕРХ работающего приложения, а не вместо него. Нужно, когда
// ждать приходится уже после старта: например, пока идёт выкатка на бой и продукт перезапускается.
// children — то, что можно сделать во время ожидания (например «свернуть»); появляются под hint,
// потому что запирать пользователя на минуты без выхода нельзя.
export function LoadingScreen({ hint, overlay, children }: {
  hint?: string;
  overlay?: boolean;
  children?: ReactNode;
}) {
  return (
    <PageCanvas style={{
      alignItems: 'center',
      justifyContent: 'center',
      ...(overlay ? { position: 'fixed' as const, inset: 0, zIndex: Z.modal } : null),
    }}>
      <div className="cc-boot-veil" style={{
        display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.md,
      }}>
        <img
          className="cc-boot-logo"
          src="/favicon.svg" alt="" width={54} height={54}
          style={{ display: 'block', filter: SHADOW.islandDrop }}
        />
        <div style={{
          fontFamily: FONT.serif, fontSize: FS.h2, fontWeight: 500,
          color: C.textHeading, letterSpacing: '-0.01em', lineHeight: 1,
        }}>
          Home AI
        </div>
        {hint && (
          <div className="cc-boot-hint" style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1 }}>
            {hint}
          </div>
        )}
        {children}
      </div>
    </PageCanvas>
  );
}

/// Та же заставка, но поверх работающего приложения — для ожиданий, которые случаются уже после
/// старта: выкатка на бой, переход на новую версию фронта.
///
/// Портал в body обязателен, и это не перестраховка: без него заставка попадает в
/// stacking-контекст своего родителя (шапки, панели), где собственный zIndex ничего не решает, —
/// и поверх неё продолжает висеть плавающая кнопка ассистента. Modal порталит по той же причине.
export function LoadingOverlay({ hint, children }: { hint?: string; children?: ReactNode }) {
  return createPortal(<LoadingScreen overlay hint={hint}>{children}</LoadingScreen>, document.body);
}
