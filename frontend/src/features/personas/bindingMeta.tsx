import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { File, Layers, Pencil, Wrench, Zap } from 'lucide-react';
import type { BindingTarget, PersonaBinding, PersonaBindingMode, PersonaBindingType } from '../../types';
import { api } from '../../lib/api';
import { C, R } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';

// Общие метаданные привязок «Знания и правила» (фича persona-bindings):
// иконки/тона типов, подписи режимов, счётчики и резолв человекочитаемых
// подписей целей. Используется вкладкой «Знания» (PersonaBindingsPanel)
// и выжимкой на «Обзоре» (PersonaPreview).

// Иконки типов привязок (lucide-react). project — составная «папка с лицом»
// (нет точного lucide-аналога), остальные — канонические lucide-компоненты.
export const BINDING_ICONS: Record<PersonaBindingType, (size: number) => ReactNode> = {
  // Проект — открытая папка с «глазами» (составная, без lucide-аналога)
  project: size => (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={ICON_STROKE} strokeLinecap="round" strokeLinejoin="round">
      <path d="M4 20V6a2 2 0 0 1 2-2h3l2 2h7a2 2 0 0 1 2 2v12" />
      <path d="M2 20h20" />
      <circle cx="9" cy="13" r="1" />
      <circle cx="15" cy="13" r="1" />
    </svg>
  ),
  // Папка или файл — лист с загнутым углом
  projectPath: size => <File size={size} strokeWidth={ICON_STROKE} />,
  // База знаний — слои
  knowledge: size => <Layers size={size} strokeWidth={ICON_STROKE} />,
  // Заметки — карандаш
  notes: size => <Pencil size={size} strokeWidth={ICON_STROKE} />,
  // Инструмент — гаечный ключ
  tool: size => <Wrench size={size} strokeWidth={ICON_STROKE} />,
  // Навык — молния
  skill: size => <Zap size={size} strokeWidth={ICON_STROKE} />,
};

// Тона круглой иконки типа + название и подсказка для сетки выбора типа
export const BINDING_TYPE_META: Record<PersonaBindingType, { name: string; hint: string; bg: string; fg: string }> = {
  project:     { name: 'Проект',         hint: 'Персона видит все файлы проекта', bg: C.accentLight, fg: C.accent },
  projectPath: { name: 'Папка или файл', hint: 'Конкретный путь внутри проекта',  bg: C.bgSelected,  fg: C.textSecondary },
  knowledge:   { name: 'Знания проекта', hint: 'База знаний с документами',       bg: C.infoBg,      fg: C.info },
  notes:       { name: 'Заметки',        hint: 'Vault заметок или его папка',     bg: C.successBg,   fg: C.successText },
  tool:        { name: 'Инструмент',     hint: 'Задачи, веб, чаты, проекты…',     bg: C.planLight,   fg: C.plan },
  skill:       { name: 'Навык',          hint: 'Готовый приём работы',            bg: C.warningBg,   fg: C.warning },
};

export const BINDING_TYPE_ORDER: PersonaBindingType[] = ['project', 'projectPath', 'knowledge', 'notes', 'tool', 'skill'];

export const MODE_LABEL: Record<PersonaBindingMode, string> = { auto: 'авто', always: 'всегда', off: 'выкл' };

export const MODE_HINT: Record<PersonaBindingMode, string> = {
  auto:   'Модель сама решает по условию, когда заглянуть в источник',
  always: 'Краткая выжимка подкладывается персоне в каждый ход — дороже, но надёжнее',
  off:    'Привязка сохранена, но не используется',
};

// Тона бейджа режима: авто — акцент, всегда — info, выкл — приглушённый
export const MODE_BADGE: Record<PersonaBindingMode, { bg: string; fg: string }> = {
  auto:   { bg: C.accentLight, fg: C.accent },
  always: { bg: C.infoBg,      fg: C.info },
  off:    { bg: C.bgSelected,  fg: C.textMuted },
};

// Круглая иконка типа привязки (32px в карточках, 24px в компактных строках)
export function BindingTypeIcon({ type, size = 32, dim }: { type: PersonaBindingType; size?: number; dim?: boolean }) {
  const meta = BINDING_TYPE_META[type];
  return (
    <span style={{
      width: size, height: size, borderRadius: R.full, flexShrink: 0,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      background: dim ? C.bgSelected : meta.bg, color: dim ? C.textMuted : meta.fg,
    }}>
      {BINDING_ICONS[type](size >= 32 ? 16 : 13)}
    </span>
  );
}

// Бейдж режима привязки
export function BindingModeBadge({ mode }: { mode: PersonaBindingMode }) {
  const tone = MODE_BADGE[mode];
  return (
    <span style={{
      borderRadius: R.pill, padding: '2px 8px', fontSize: 10.5, fontWeight: 600,
      letterSpacing: '0.02em', background: tone.bg, color: tone.fg, flexShrink: 0, whiteSpace: 'nowrap',
    }}>
      {MODE_LABEL[mode]}
    </span>
  );
}

export function bindingPlural(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'привязка';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'привязки';
  return 'привязок';
}

// Счётчик «N привязок · M выкл» для заголовков секций
export function bindingsCounter(bindings: PersonaBinding[]): string {
  const offN = bindings.filter(b => b.mode === 'off').length;
  if (bindings.length === 0) return '0 привязок';
  return `${bindings.length} ${bindingPlural(bindings.length)}${offN ? ` · ${offN} выкл` : ''}`;
}

// === Резолв подписей целей ===
// Каталоги целей кэшируются на модуль: повторные заходы на вкладку/обзор
// не дёргают бэк заново (инвалидация — перезагрузкой страницы, целей мало).
const targetsCache = new Map<string, Promise<BindingTarget[]>>();

export function fetchBindingTargets(type: string, source?: string): Promise<BindingTarget[]> {
  const key = source ? `${type}|${source}` : type;
  let p = targetsCache.get(key);
  if (!p) {
    p = api.personas.bindingTargets(type, source).catch(err => {
      targetsCache.delete(key);   // не кэшируем ошибку
      throw err;
    });
    targetsCache.set(key, p);
  }
  return p;
}

// Какой каталог нужен типу привязки для подписи цели
function catalogTypeFor(t: PersonaBindingType): string {
  return t === 'projectPath' ? 'project' : t;
}

// Человекочитаемая подпись привязки по образцу прототипа:
// «Проект „X“ · файлы», «Знания проекта „X“», «Заметки · папка „Y“», «Навык „Z“»…
export function bindingLabel(b: PersonaBinding, targets: Map<string, BindingTarget>): string {
  const t = targets.get(`${catalogTypeFor(b.type)}:${b.target}`);
  const label = t?.label ?? b.target;
  switch (b.type) {
    case 'project':     return `Проект «${label}» · файлы`;
    case 'projectPath': return `Проект «${label}» · ${b.path ?? ''}`.trimEnd();
    case 'knowledge':   return t?.meta ? `Знания проекта «${label}»` : `Знания «${label}»`;
    case 'notes':       return b.path ? `Заметки · папка «${b.path}»` : `Заметки · ${label}`;
    case 'tool':        return label;
    case 'skill':       return `Навык «${label}»`;
  }
}

// Хук: подгружает каталоги целей под типы имеющихся привязок и отдаёт
// резолвер подписи. Пока каталог не загружен — подпись деградирует до raw id.
export function useBindingLabels(bindings: PersonaBinding[] | null): (b: PersonaBinding) => string {
  const [targets, setTargets] = useState<Map<string, BindingTarget>>(() => new Map());

  // Набор нужных каталогов — стабильный ключ, чтобы не перезапрашивать на каждый рендер
  const typesKey = useMemo(
    () => [...new Set((bindings ?? []).map(b => catalogTypeFor(b.type)))].sort().join(','),
    [bindings],
  );

  useEffect(() => {
    if (!typesKey) return;
    let alive = true;
    void Promise.all(typesKey.split(',').map(async type => {
      try {
        const list = await fetchBindingTargets(type);
        return list.map(t => [`${type}:${t.id}`, t] as const);
      } catch {
        return [];
      }
    })).then(chunks => {
      if (!alive) return;
      setTargets(new Map(chunks.flat()));
    });
    return () => { alive = false; };
  }, [typesKey]);

  return useMemo(() => (b: PersonaBinding) => bindingLabel(b, targets), [targets]);
}
