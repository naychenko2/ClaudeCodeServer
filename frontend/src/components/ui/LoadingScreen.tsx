import { C, FONT, FS, SHADOW, SP } from '../../lib/design';
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
export function LoadingScreen({ hint }: { hint?: string }) {
  return (
    <PageCanvas style={{ alignItems: 'center', justifyContent: 'center' }}>
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
      </div>
    </PageCanvas>
  );
}
