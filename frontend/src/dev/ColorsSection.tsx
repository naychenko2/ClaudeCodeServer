// Витрина дизайн-системы — секция «Цвета».
//
// Программный обход объекта C (см. lib/design.ts). Каждая запись — это
// `var(--c-…)`-токен; на витрине показываем:
//   • плашку с background = сам токен (var(--c-…), НЕ hex) — поэтому плашка
//     честно меняется при смене темы вместе со всем интерфейсом;
//   • имя ключа (bgMain, textPrimary, …);
//   • resolved-значение из getComputedStyle(document.documentElement) —
//     конкретный hex/rgba, который браузер подставил в текущей теме. Читается
//     в useEffect, чтобы не дёргать layout при рендере; обновляется при смене
//     темы, потому что подписываемся на useThemeMode() (он эмитит и при ручном
//     переключении, и при смене системной темы, если mode === 'system').
//
// Группировка — таблица «ключ → группа» (COLOR_GROUPS). Если в C появится новый
// ключ, которого нет в таблице, он автоматически попадёт в «Прочее» — витрина
// не требует правок при расширении палитры.
//
// Стили — только токены C/FS/SP/R/ISLAND и компоненты ui/; ни одного
// hex-литерала (lint:design зелёный).

import { useEffect, useState } from 'react';
import { Brush } from 'lucide-react';
import { C, FONT, FS, SP, R, ISLAND } from '../lib/design';
import { useThemeMode } from '../lib/themeMode';
import { Island, IslandHeader } from '../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';

// Таблица соответствия «ключ → группа». Порядок групп в массиве задаёт порядок
// вывода на витрине.
const COLOR_GROUPS: { name: string; keys: string[] }[] = [
  { name: 'Фоны',     keys: ['bgMain', 'bgPanel', 'bgCard', 'bgWhite', 'bgSelected', 'bgInset'] },
  { name: 'Текст',    keys: ['textHeading', 'textPrimary', 'textSecondary', 'textMuted', 'onAccent', 'onDark'] },
  { name: 'Акцент',   keys: ['accent', 'accentLight', 'accentMuted', 'accentSoft'] },
  { name: 'Границы',  keys: ['border', 'borderLight', 'divider', 'dashed', 'track'] },
  { name: 'Статусы',  keys: ['success', 'successBg', 'successText', 'warning', 'warningBg', 'warningText',
                             'danger', 'dangerBg', 'dangerText', 'dangerBorder', 'info', 'infoBg'] },
  { name: 'План',     keys: ['plan', 'planLight', 'planText', 'planBorder'] },
  { name: 'Diff',     keys: ['diffAddBg', 'diffAddText', 'diffRemBg', 'diffRemText'] },
  { name: 'Терминал', keys: ['termBg', 'termText', 'termError', 'outputBg', 'outputBorder'] },
  { name: 'Прочее',   keys: ['overlay', 'glass', 'msgBg', 'navInk', 'onNavInk'] },
];

// Мапа «ключ → имя группы» — для O(1) поиска при обходе Object.entries(C).
const COLOR_KEY_TO_GROUP: Map<string, string> = (() => {
  const m = new Map<string, string>();
  for (const g of COLOR_GROUPS) for (const k of g.keys) m.set(k, g.name);
  return m;
})();

// Регекс извлечения имени CSS-переменной из значения вида `var(--c-bg-main)`.
// Значения в C все такие; если встретится что-то иное — на плашку идёт сам
// токен как есть, в resolved пишем пустую строку (значение «—»).
const CSS_VAR_RE = /^var\((--c-[^)]+)\)$/;

// Записи из C — статичны, вычисляются один раз на уровне модуля (C не меняется
// в рантайме). Вынос за пределы компонента исключает их из deps useEffect
// и предотвращает бесконечный цикл ре-рендеров.
const COLOR_ENTRIES = Object.entries(C) as [string, string][];

// Распределить entries из C по группам. Возвращает массив {name, items} в
// порядке COLOR_GROUPS; пустые группы отбрасываются.
function buildColorGroups(
  entries: [string, string][],
): { name: string; items: { key: string; value: string }[] }[] {
  // Каждая группа из таблицы получает свой контейнер — сохраняем порядок.
  const byGroup = new Map<string, { key: string; value: string }[]>();
  for (const g of COLOR_GROUPS) byGroup.set(g.name, []);

  for (const [key, value] of entries) {
    const gName = COLOR_KEY_TO_GROUP.get(key) ?? 'Прочее';
    const bucket = byGroup.get(gName);
    if (bucket) {
      bucket.push({ key, value });
    } else {
      // На случай, если в COLOR_GROUPS нет «Прочего» — создадим (не должно
      // случиться, «Прочее» описано в таблице, но защищаемся).
      byGroup.set(gName, [{ key, value }]);
    }
  }

  // Возвращаем только непустые группы, в порядке COLOR_GROUPS.
  return COLOR_GROUPS
    .map(g => ({ name: g.name, items: byGroup.get(g.name) ?? [] }))
    .filter(g => g.items.length > 0);
}

export function ColorsSection() {
  // Подписка на режим темы: useThemeMode эмитит при смене mode (light/dark/system)
  // и при смене системной темы (если mode === 'system'). mode в deps useEffect
  // — триггер ре-чтения resolved-значений при любой смене темы.
  const mode = useThemeMode();

  // resolved[key] — вычисленное значение цвета в текущей теме (#F4F0E8 и т.п.).
  // Пустая строка = ещё не вычислено или getComputedStyle ничего не вернул.
  const [resolved, setResolved] = useState<Record<string, string>>({});

  // Группы статичны — C не меняется в рантайме.
  const groups = buildColorGroups(COLOR_ENTRIES);

  useEffect(() => {
    // getComputedStyle на :root читает значения, которые тема выставила через
    // data-theme. Запускаем после монтирования и при любой смене mode.
    const root = document.documentElement;
    const cs = getComputedStyle(root);
    const next: Record<string, string> = {};
    for (const [key, value] of COLOR_ENTRIES) {
      const m = CSS_VAR_RE.exec(value);
      if (m) {
        // getPropertyValue возвращает строку с пробелами по краям — тримим.
        next[key] = cs.getPropertyValue(m[1]).trim();
      } else {
        // На всякий случай: значение без var() — копируем как есть.
        next[key] = value;
      }
    }
    // eslint-disable-next-line react-hooks/set-state-in-effect -- резолв CSS-переменных при смене темы
    setResolved(next);
  }, [mode]);

  return (
    <Island>
      <IslandHeader
        icon={
          <Brush
            size={ICON_SIZE.md}
            strokeWidth={ICON_STROKE}
            style={{ color: C.accent, flexShrink: 0 }}
          />
        }
        title="Цвета"
        badge={`${COLOR_ENTRIES.length} токенов`}
      />
      <div style={{
        padding: ISLAND.pad,
        display: 'flex',
        flexDirection: 'column',
        gap: SP.lg,
      }}>
        {groups.map(group => (
          <section key={group.name} style={{
            display: 'flex',
            flexDirection: 'column',
            gap: SP.md,
          }}>
            {/* Заголовок группы + счётчик */}
            <div style={{
              display: 'flex',
              alignItems: 'baseline',
              gap: SP.sm,
            }}>
              <h3 style={{
                margin: 0,
                fontFamily: FONT.sans,
                fontSize: FS.sm,
                fontWeight: 600,
                color: C.textSecondary,
                textTransform: 'uppercase',
                letterSpacing: 0.5,
              }}>
                {group.name}
              </h3>
              <span style={{
                fontFamily: FONT.mono,
                fontSize: FS.xs,
                color: C.textMuted,
              }}>
                {group.items.length}
              </span>
            </div>
            {/* Сетка плашек — auto-fill с minmax: на мобиле 1 столбец,
                на десктопе несколько в ряд. */}
            <div style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))',
              gap: SP.md,
            }}>
              {group.items.map(({ key, value }) => (
                <ColorItem
                  key={key}
                  name={key}
                  token={value}
                  resolvedValue={resolved[key] ?? ''}
                />
              ))}
            </div>
          </section>
        ))}
      </div>
    </Island>
  );
}

// Одна плашка цвета: квадрат background=var(--c-…) + имя ключа + resolved hex.
// background намеренно = сам токен (var(--c-…)), а не resolved hex — так плашка
// остаётся живой и переключается темой через CSS, без re-render от state.
function ColorItem({ name, token, resolvedValue }: {
  name: string;
  token: string;
  resolvedValue: string;
}) {
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: SP.md,
      }}
      title={`${name} → ${token}${resolvedValue ? ` → ${resolvedValue}` : ''}`}
    >
      {/* Плашка: background = сам токен (var(--c-…)). Рамка — borderLight, чтобы
          светлые плашки (типа bgWhite) не сливались с фоном острова. */}
      <div
        style={{
          width: 40,
          height: 40,
          flexShrink: 0,
          borderRadius: R.md,
          background: token,
          border: `1px solid ${C.borderLight}`,
        }}
      />
      <div style={{
        display: 'flex',
        flexDirection: 'column',
        gap: SP.xxs,
        minWidth: 0,           // чтобы длинный токен не раздувал колонку
      }}>
        {/* Имя ключа — моноширинное, чтобы было видно структуру (bgMain, …). */}
        <span style={{
          fontFamily: FONT.mono,
          fontSize: FS.sm,
          color: C.textHeading,
        }}>
          {name}
        </span>
        {/* Resolved hex из getComputedStyle — это просто информационная подпись,
            не участвует в стилизации. Пустая строка → «—» (первый рендер). */}
        <span style={{
          fontFamily: FONT.mono,
          fontSize: FS.xs,
          color: C.textMuted,
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}>
          {resolvedValue || '—'}
        </span>
      </div>
    </div>
  );
}
