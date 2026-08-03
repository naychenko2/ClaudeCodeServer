import { useState, type ReactNode } from 'react';
import { Check, Copy } from 'lucide-react';
import { C, R, SHADOW } from '../../lib/design';

// Кнопка копирования кода поверх блока: hover на десктопе (пойнтер мыши), всегда
// видна на тач-устройствах — .cc-code-block/.cc-code-copy в index.css, тот же
// паттерн, что у .cc-msg/.cc-actions (панель действий поста).
function CodeCopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  const copy = (e: React.MouseEvent) => {
    e.stopPropagation();
    navigator.clipboard?.writeText(text)
      .then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); })
      .catch(() => {});
  };
  return (
    <button onClick={copy} className="cc-code-copy"
      title={copied ? 'Скопировано' : 'Скопировать код'} aria-label="Скопировать код"
      style={{
        position: 'absolute', top: 6, right: 6, zIndex: 1,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        width: 26, height: 26, borderRadius: R.md, border: `1px solid ${C.border}`,
        background: C.bgPanel, boxShadow: SHADOW.card,
        color: copied ? C.success : C.textMuted, cursor: 'pointer', padding: 0,
      }}>
      {copied
        ? <Check size={13} strokeWidth={3} style={{ flexShrink: 0 }} />
        : <Copy size={12} strokeWidth={2} style={{ flexShrink: 0 }} />}
    </button>
  );
}

// Оборачивает блок кода в позиционирующий контейнер + класс для hover/тач-CSS.
// text — ровно то, что уйдёт в буфер: без подписи языка, без номеров строк, без обрезки.
export function CodeBlockFrame({ text, children }: { text: string; children: ReactNode }) {
  return (
    <div className="cc-code-block" style={{ position: 'relative' }}>
      {children}
      <CodeCopyButton text={text} />
    </div>
  );
}
