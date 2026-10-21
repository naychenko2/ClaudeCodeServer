// Кит панели плагина (plugin-kit) на витрине UI-кита (S11).
//
// Панель — sandbox-iframe (ADR-031): хост инжектит в её <head> шим — вшитый шрифт
// (font.css, S10) и девять классов кита (controls.css, S9) — и доставляет токены
// текущей темы --cc-* (S8: хост читает свои computed --c-* и мапит их по CC_TOKEN_MAP).
// Секция показывает ТЕ ЖЕ файлы (те же ?raw-импорты, что bridge.ts — без дубля в
// бандле) и те же токены: при переключении темы в шапке витрины кит перекрашивается
// без перезагрузки — ровно как живая панель.
//
// Тело панели прозрачное — подложку даёт остров хоста (C.bgCard) — а шрифт та же
// связка, что у тела хоста (FONT.sans: Hanken Grotesk; в панели латиница вшита,
// кириллица — системный фолбэк, паритет с хостом, S10a).
//
// Классы опт-ин: голый <button> без класса не стилизуется — демо показывает это
// явно («неправильный» способ). Собственные контролы приложения — React-компоненты
// из ui/: у них другие правила, и автор панели не должен их смешивать с китом.

import type { CSSProperties, ReactNode } from 'react';
import { Plug } from 'lucide-react';
import { Island, IslandHeader } from '../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { C, FONT, FS, ISLAND, R, SP } from '../lib/design';
import { useThemeMode } from '../lib/themeMode';
import { FLAGS, useFeature } from '../lib/featureFlags';
// Те же ?raw-импорты, что bridge.ts (те же модули — без дубля в бандле):
// font.css — вшитый шрифт (S10), controls.css — девять классов кита (S9).
import kitFont from '../../public/plugin-kit/font.css?raw';
import kitControls from '../../public/plugin-kit/controls.css?raw';

// Девять классов кита (S9) — полный набор, которым секция обязана быть покрыта.
export const KIT_CLASSES = [
  '.cc-btn', '.cc-input', '.cc-list', '.cc-list-item',
  '.cc-card', '.cc-badge', '.cc-empty', '.cc-notice', '.cc-skeleton',
] as const;

// --cc-* → --c-* хоста: та же карта, что CC_TOKEN_MAP (PluginFrame.tsx, S8).
// Копия, а не импорт: код панелей рельсы (S5/S7) задачей не трогаем, а карта
// мала и стабильна — палитра кита есть палитра хоста. Экспортирована, чтобы
// юнит проверял её состав (R-Design-2: новые токены поверхностей/состояний).
export const CC_TOKEN_MAP: Record<string, string> = {
  '--cc-bg':            '--c-bg-card',
  '--cc-fg':            '--c-text-primary',
  '--cc-fg-muted':      '--c-text-muted',
  '--cc-accent':        '--c-accent',
  '--cc-accent-fg':     '--c-on-accent',
  '--cc-accent-soft':   '--c-accent-soft',
  '--cc-border':        '--c-border',
  '--cc-bg-inset':      '--c-bg-inset',
  '--cc-bg-panel':      '--c-bg-panel',
  '--cc-bg-selected':   '--c-bg-selected',
  '--cc-surface':       '--c-bg-white',
  '--cc-success':       '--c-success',
  '--cc-warning':       '--c-warning',
  '--cc-danger':        '--c-danger',
  '--cc-info':          '--c-info',
  '--cc-success-text':  '--c-success-text',
  '--cc-warning-text':  '--c-warning-text',
  '--cc-danger-text':   '--c-danger-text',
  '--cc-danger-border': '--c-danger-border',
};

// Чтец токенов текущей темы: getComputedStyle(<html>) несёт активную тему
// (data-theme проставляет themeMode.ts) — как readHostTokens в PluginFrame.tsx.
// В node (без CSS) getComputedStyle заглушен — {} (токены не критичны для логики).
function readKitTokens(): Record<string, string> {
  if (typeof document === 'undefined' || typeof getComputedStyle !== 'function') return {};
  const cs = getComputedStyle(document.documentElement);
  const tokens: Record<string, string> = {};
  for (const cc of Object.keys(CC_TOKEN_MAP)) {
    const v = cs.getPropertyValue(CC_TOKEN_MAP[cc]).trim();
    if (v) tokens[cc] = v;
  }
  return tokens;
}

// Содержимое <style>: вшитый шрифт (S10) + девять классов (S9) + токены текущей
// темы. В живой панели те же три части лежат в srcdoc (bridge.ts: shimCss +
// <style id="cc-tokens">); здесь — один <style> в документе витрины, который
// React снимает при размонтировании секции.
export function buildKitStyle(tokens: Record<string, string>): string {
  const names = Object.keys(tokens);
  const tokensBlock = names.length
    ? ':root {\n' + names.map(n => '  ' + n + ': ' + tokens[n] + ';').join('\n') + '\n}'
    : '';
  return kitFont + '\n' + kitControls + '\n' + tokensBlock;
}

// Лейбл-подпись над группой: тот же визуальный ритм, что у SubBlock витрины.
function SubBlock({ label, children }: { label: ReactNode; children?: ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>{label}</div>
      {children}
    </div>
  );
}

// Тело панели: прозрачное в живой панели (подложку даёт остров хоста, C.bgCard),
// шрифт/размер/лидер — структурные правила theme.css (13px, 1.45, FONT.sans).
const PANEL_BODY: CSSProperties = {
  background: C.bgCard,
  border: `1px solid ${C.border}`,
  borderRadius: R.lg,
  padding: SP.lg,
  fontFamily: FONT.sans,
  fontSize: FS.base,
  lineHeight: 1.45,
  color: 'var(--cc-fg)',
  display: 'flex',
  flexDirection: 'column',
  gap: SP.md,
};

// Минимальный пример «как писать панель»: один HTML, китовые классы, высота
// по контенту. Полный справочник (API шима, правило высоты, рабочий пример) —
// docs/features/plugin-kit.md.
const PANEL_EXAMPLE =
  '<!-- панель — один HTML-файл; шим и CSP хост инжектит в <head> сам -->\n' +
  '<body>\n' +
  '  <h1>Заметки</h1>\n' +
  '  <input class="cc-input" placeholder="Поиск…">\n' +
  '  <ul class="cc-list">\n' +
  '    <li class="cc-list-item">Пункт</li>\n' +
  '  </ul>\n' +
  '  <button class="cc-btn cc-btn--primary">Сохранить</button>\n' +
  '</body>';

export function PluginKitSection() {
  // Флаг фичи: панели (и их кит) в приложении гейтятся plugins-mvp — витрина
  // показывает то же, что видит пользователь.
  const pluginsMvp = useFeature(FLAGS.pluginsMvp);
  // Перерендер при смене темы: токены перечитываются из computed --c-* хоста,
  // кит перекрашивается без перезагрузки — как живая панель (S8).
  useThemeMode();
  const tokens = readKitTokens();

  return (
    <Island>
      <IslandHeader
        icon={
          <Plug
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Кит панели плагина (plugin-kit)"
        badge="9 классов · обе темы"
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: ISLAND.gap,
      }}>
        {/* Те же файлы, что в srcdoc панели: шрифт, девять классов, токены темы */}
        <style>{buildKitStyle(tokens)}</style>

        <SubBlock label="Это кит ПАНЕЛИ, а не контролы приложения">
          <p style={{
            margin: 0,
            fontSize: FS.sm,
            color: C.textSecondary,
            lineHeight: 1.55,
          }}>
            Панель плагина — sandbox-iframe: хост инжектит в её <code style={{ color: C.accent, fontFamily: FONT.mono }}>&lt;head&gt;</code>
            шим (вшитый шрифт и девять классов) и доставляет токены текущей темы.
            Собственные контролы приложения — React-компоненты из <code style={{ color: C.accent, fontFamily: FONT.mono }}>ui/</code>:
            у них другие правила. Классы кита — опт-ин: голый <code style={{ color: C.accent, fontFamily: FONT.mono }}>&lt;button&gt;</code>
            без класса не стилизуется. Переключите тему в шапке витрины — кит
            перекрасится без перезагрузки, как живая панель.
          </p>
        </SubBlock>

        {pluginsMvp ? (
          <SubBlock label="Девять классов в текущей теме (сверху — «неправильный» голый button)">
            <div style={PANEL_BODY}>
              {/* Заголовок панели: вшитый шрифт (S10) — та же связка, что у хоста */}
              <h1 style={{ margin: 0, fontSize: FS.lg, fontWeight: 600, color: 'var(--cc-fg)' }}>
                Заметки
              </h1>

              {/* .cc-btn: базовая, primary, ghost, danger, sm, loading */}
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, alignItems: 'center' }}>
                <button className="cc-btn">Базовая</button>
                <button className="cc-btn cc-btn--primary">Основная</button>
                <button className="cc-btn cc-btn--ghost">Призрак</button>
                <button className="cc-btn cc-btn--danger">Опасная</button>
                <button className="cc-btn cc-btn--sm">Малая</button>
                <button className="cc-btn cc-btn--primary cc-btn--loading">Сохранение…</button>
              </div>

              {/* .cc-input */}
              <input className="cc-input" placeholder="Поиск…" />

              {/* .cc-input--invalid + .cc-field-error: поле с ошибкой (рамка dangerBorder, текст danger-text) */}
              <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
                <input className="cc-input cc-input--invalid" defaultValue="abc" />
                <span className="cc-field-error">Поле не прошло проверку</span>
              </div>

              {/* .cc-list / .cc-list-item (.is-active) */}
              <ul className="cc-list">
                <li className="cc-list-item is-active">Выбранный пункт</li>
                <li className="cc-list-item">Пункт списка</li>
              </ul>

              {/* .cc-card */}
              <div className="cc-card">
                <b>Карточка</b> — контейнер контента панели.
              </div>

              {/* .cc-badge: нейтральная + четыре тона */}
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
                <span className="cc-badge">Метка</span>
                <span className="cc-badge cc-badge--success">Успех</span>
                <span className="cc-badge cc-badge--warning">Внимание</span>
                <span className="cc-badge cc-badge--danger">Ошибка</span>
                <span className="cc-badge cc-badge--info">Инфо</span>
              </div>

              {/* .cc-empty */}
              <div className="cc-empty">
                <div className="cc-empty__title">Ничего не найдено</div>
                <div className="cc-empty__text">Попробуйте другой запрос</div>
              </div>

              {/* .cc-notice: нейтральное + четыре тона */}
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: SP.xs }}>
                <div className="cc-notice">Нейтральное уведомление панели.</div>
                <div className="cc-notice cc-notice--success">Успех</div>
                <div className="cc-notice cc-notice--warning">Внимание</div>
                <div className="cc-notice cc-notice--danger">Ошибка</div>
                <div className="cc-notice cc-notice--info">Инфо</div>
              </div>

              {/* .cc-skeleton */}
              <div className="cc-skeleton" />

              {/* «Неправильный» способ: голый button без класса — браузерные
                  дефолтные стили, кит к нему не применяется (опт-ин) */}
              <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.md }}>
                <button>Голый button</button>
                <span style={{ fontSize: FS.xs, color: 'var(--cc-fg-muted)' }}>
                  без класса .cc-btn — дефолтные стили браузера: кит — опт-ин
                </span>
              </div>
            </div>
          </SubBlock>
        ) : (
          <SubBlock label="Флаг plugins-mvp">
            <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted }}>
              Флаг выключен — панели плагинов (и их кит) в приложении скрыты.
            </p>
          </SubBlock>
        )}

        <SubBlock label="Как писать панель — минимальный пример (классы из кита, высота по контенту)">
          <pre style={{
            margin: 0,
            background: C.bgInset,
            border: `1px solid ${C.border}`,
            borderRadius: R.md,
            padding: SP.md,
            fontFamily: FONT.mono,
            fontSize: FS.xs,
            color: C.textSecondary,
            overflowX: 'auto',
            whiteSpace: 'pre',
            lineHeight: 1.5,
          }}>{PANEL_EXAMPLE}</pre>
          <p style={{
            margin: 0,
            fontSize: FS.xs,
            color: C.textMuted,
            fontFamily: FONT.mono,
            lineHeight: 1.5,
          }}>
            Полный справочник (API шима ccsPlugin, правило высоты, рабочий пример):
            docs/features/plugin-kit.md
          </p>
        </SubBlock>
      </div>
    </Island>
  );
}
