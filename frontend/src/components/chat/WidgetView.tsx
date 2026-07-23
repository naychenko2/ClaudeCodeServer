import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { useSyncExternalStore } from 'react';
import { LayoutDashboard } from 'lucide-react';
import type { ChatItem } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { getEffectiveTheme, subscribeThemeMode } from '../../lib/themeMode';
import { useIsMobile } from '../../lib/breakpoints';
import {
  parseWidgetInput, buildWidgetSrcDoc, clampWidgetHeight,
  WIDGET_MAX_RENDER_BYTES, WIDGET_DEFAULT_HEIGHT, WIDGET_HEIGHT_MSG,
} from '../../lib/widgetHtml';

// Вызов widget_show (mcp__widgets__widget_show) — сравнение по суффиксу, без регистра:
// переживёт смену ключа сервера в MCP-конфиге
export function isWidgetShow(name: string): boolean {
  return name.toLowerCase().endsWith('__widget_show');
}

// Компактная плашка состояния (спиннер/ошибка/пустой виджет) внутри карточки
function StatusRow({ spinner, text, danger }: { spinner?: boolean; text: string; danger?: boolean }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 8, padding: '10px 12px',
      fontFamily: FONT.sans, fontSize: 12.5,
      color: danger ? C.dangerText : C.textMuted,
    }}>
      {spinner && <div className="tool-spinner" />}
      <span>{text}</span>
    </div>
  );
}

// Карточка HTML-виджета в ленте чата: html из input вызова рендерится в изолированном
// sandbox-iframe (без allow-same-origin и allow-popups — виджет живёт в opaque origin,
// CSP обёртки блокирует любые внешние запросы). Успешный item.result — инструкция для
// модели («не дублируй…»), в UI не выводится: видимы только iframe и error-плашки.
export const WidgetView = memo(function WidgetView({ item }: { item: Extract<ChatItem, { kind: 'tool_use' }> }) {
  const mobile = useIsMobile();
  // Эффективная тема с подпиской: subscribeThemeMode эмитит и при смене системной темы
  const theme = useSyncExternalStore(subscribeThemeMode, getEffectiveTheme, getEffectiveTheme);

  const input = useMemo(() => parseWidgetInput(item.input), [item.input]);
  const title = input.title || 'Виджет';

  // Пока input стримится частями (или финального input ещё нет) — html неполный
  const streaming = item.streamingArg !== undefined
    || (!input.html.trim() && item.result === undefined);
  const isError = !!item.isError;
  const tooBig = input.html.length > WIDGET_MAX_RENDER_BYTES;
  const emptyFinal = !streaming && !isError && !input.html.trim();

  // Высота: база из input (кламп) или дефолт; авто-подстройка приходит postMessage'ем
  const baseHeight = input.height != null ? clampWidgetHeight(input.height, mobile) : WIDGET_DEFAULT_HEIGHT;
  const [autoHeight, setAutoHeight] = useState<number | null>(null);
  const [collapsed, setCollapsed] = useState(false);
  const iframeRef = useRef<HTMLIFrameElement | null>(null);

  // Канал авто-высоты: origin у sandbox-iframe без allow-same-origin — 'null', по нему
  // фильтровать нельзя; сверяем источник с contentWindow нашего iframe (анти-спуфинг)
  useEffect(() => {
    const onMessage = (e: MessageEvent) => {
      if (e.source !== iframeRef.current?.contentWindow) return;
      const d = e.data as { type?: unknown; h?: unknown } | null;
      if (d?.type !== WIDGET_HEIGHT_MSG || typeof d.h !== 'number' || !Number.isFinite(d.h)) return;
      setAutoHeight(clampWidgetHeight(d.h, mobile));
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [mobile]);

  // Смена темы пересобирает srcDoc через key (полный ремаунт iframe) — состояние
  // виджета при переключении темы теряется, осознанный трейдофф v1
  const srcDoc = useMemo(
    () => (streaming || isError || tooBig || emptyFinal ? null : buildWidgetSrcDoc(input.html, theme)),
    [streaming, isError, tooBig, emptyFinal, input.html, theme],
  );

  const height = autoHeight ?? baseHeight;

  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderRadius: 12, background: C.bgWhite,
      overflow: 'hidden', boxShadow: SHADOW.card, maxWidth: '100%',
    }}>
      {/* Шапка: иконка + заголовок + сворачивание */}
      <div
        onClick={() => setCollapsed(v => !v)}
        style={{
          display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px',
          borderBottom: collapsed ? 'none' : `1px solid ${C.divider}`,
          cursor: 'pointer', userSelect: 'none',
        }}
      >
        <LayoutDashboard size={14} strokeWidth={2} style={{ color: C.accent, flexShrink: 0 }} />
        <span style={{
          fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.textPrimary,
          flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {title}
        </span>
        <span style={{
          color: C.textMuted, fontSize: 11, flexShrink: 0, display: 'inline-block',
          transform: collapsed ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s',
        }}>▾</span>
      </div>

      {!collapsed && (
        streaming ? <StatusRow spinner text="Готовлю виджет…" />
        : isError ? <StatusRow danger text={item.result?.trim() || 'Не удалось показать виджет'} />
        : tooBig ? <StatusRow danger text="Виджет слишком большой для отображения" />
        : emptyFinal ? <StatusRow text="Виджет без содержимого" />
        : (
          <iframe
            ref={iframeRef}
            key={theme}
            srcDoc={srcDoc!}
            sandbox="allow-scripts allow-forms allow-modals"
            title={title}
            style={{
              width: '100%', height, border: 'none', display: 'block',
              background: 'transparent', transition: 'height 0.15s ease',
            }}
          />
        )
      )}
    </div>
  );
});
