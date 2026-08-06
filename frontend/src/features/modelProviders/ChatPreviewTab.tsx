import type { CSSProperties } from 'react';
import { C, FS, R } from '../../lib/design';

// Превью ленты чата (блок G): как подмена модели и ротация подписок видны в разговоре.
// Статичный пример: тексты пометок — те же, что рисует лента («Ответила … — … была
// недоступна», «Продолжено на подписке „…“»).

const bubbleStyle: CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
  padding: '10px 13px', maxWidth: 480, fontSize: FS.base, lineHeight: 1.55, color: C.textPrimary,
};

function DividerPill({ text, warn }: { text: string; warn?: boolean }) {
  return (
    <div style={{
      alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 8,
      width: '100%', maxWidth: 480,
    }}>
      <div style={{ flex: 1, height: 1, background: C.border }} />
      <div style={{
        fontSize: FS.sm, whiteSpace: 'nowrap', padding: '3px 10px', borderRadius: R.max,
        background: warn ? C.warningBg : C.bgSelected,
        border: `1px solid ${warn ? C.warning : C.border}`,
        color: warn ? C.warningText : C.textSecondary,
      }}>
        {text}
      </div>
      <div style={{ flex: 1, height: 1, background: C.border }} />
    </div>
  );
}

export function ChatPreviewTab() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px' }}>
        Если выбранная модель недоступна, ход продолжается на следующей — факт подмены
        виден прямо в ленте, а не только в счёте.
      </div>

      <div style={{
        background: C.bgInset, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: 16, display: 'flex', flexDirection: 'column', gap: 10,
      }}>
        <div style={bubbleStyle}>
          Смотрю миграцию 0042 — колонка добавляется без дефолта, на 50 млн строк это
          заблокирует таблицу на время бэкфилла.
        </div>
        <DividerPill warn text="⚡ Ответила GLM-4.7 — Opus была недоступна" />
        <div style={bubbleStyle}>
          Дальше два варианта: добавить колонку nullable и бэкфилить пачками, либо развести
          на две миграции. Первый безопаснее для прод-трафика.
        </div>
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
        Подмена происходит посреди уже начатого ответа — верхняя часть пузыря не
        переписывается, факт замены остаётся виден в ленте.
      </div>

      <div style={{
        background: C.bgInset, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: 16, display: 'flex', flexDirection: 'column', gap: 10, marginTop: 4,
      }}>
        <div style={bubbleStyle}>Есть контекст по инциденту — гружу лог за последний час.</div>
        <DividerPill text="Продолжено на подписке «Anthropic — резервная»" />
        <div style={bubbleStyle}>
          Нашла: три ретрая подряд на 502 от аплинка, дальше цепочка ушла на вторую подписку
          автоматически — модель при этом не меняется.
        </div>
      </div>
    </div>
  );
}
