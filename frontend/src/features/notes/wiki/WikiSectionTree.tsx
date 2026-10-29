// Дерево разделов вики (макет llm-wiki-v2.html, §1 «Дерево разделов и каталог»).
// Состав групп — модель разделов ADR-036 §1: «Проекты» (живые имена из DTO), «Сферы»
// (место зарезервировано, наполнение — следующей фичей, ADR-036 §7), «Общее», «Личное».
// Сирота остаётся в своей группе и несёт пометку «проект удалён»; удаляется она кнопкой в
// центре (WikiTab) — автоочистки нет (ADR-036 §1).
// Данных здесь своих нет: группы считает groupSectionsByKind (lib/wiki), имена — name из
// DTO. У сироты имени нет никогда (бэк не хранит имя удалённого проекта —
// WikiController.Tree → LiveNameOrNull = null), поэтому у неё свой подпись-заместитель.

import { Fragment, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { Folder, Globe, Lock, Monitor } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { Badge, type BadgeTone } from '../../../components/ui/Badge';
import { SidebarSection } from '../../../components/ui/SidebarSection';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { useIsMobile } from '../../../lib/breakpoints';
import { groupSectionsByKind, type WikiSectionDto } from '../../../lib/wiki';

// Строка дерева: 32 на десктопе, 44 на тач (макет .trow / .app.m .trow)
const ROW_H = 32;
const ROW_H_TOUCH = 44;
// Отступ второй уровня (проекты под группой) — макет .trow.l2
const ROW_INDENT = 26;

// Подпись сироты: имени удалённого проекта нигде нет, ключ — только в title строки
const ORPHAN_LABEL = 'Удалённый проект';

interface Props {
  sections: WikiSectionDto[];
  selectedKey: string | null;
  onSelect: (key: string) => void;
}

export function WikiSectionTree({ sections, selectedKey, onSelect }: Props) {
  const isMobile = useIsMobile();
  const [hoveredKey, setHoveredKey] = useState<string | null>(null);
  const groups = groupSectionsByKind(sections);

  const row = (s: WikiSectionDto, opts: {
    icon: ReactNode;
    label: string;
    muted?: boolean;
    badge?: { tone: BadgeTone; text: string; title?: string };
    /** Раздел сферы: место есть, выбор закрыт (ADR-036 §7) — строка не кликается */
    reserved?: boolean;
  }) => {
    const selected = selectedKey === s.key;
    const clickable = !opts.reserved;
    const body = (
      <>
        {opts.icon}
        <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {opts.label}
        </span>
        {opts.badge && <Badge tone={opts.badge.tone} size="xs" title={opts.badge.title}>{opts.badge.text}</Badge>}
        <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
          {s.pageCount || ''}
        </span>
      </>
    );
    const style: CSSProperties = {
      display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', textAlign: 'left',
      border: opts.reserved ? `1px dashed ${C.dashed}` : 'none',
      borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`, paddingLeft: ROW_INDENT,
      minHeight: isMobile ? ROW_H_TOUCH : ROW_H,
      fontSize: FS.base, fontWeight: selected ? 600 : 400,
      color: selected ? C.textHeading : opts.muted ? C.textMuted : C.textPrimary,
      background: selected ? C.bgSelected : !isMobile && hoveredKey === s.key ? C.bgCard : 'transparent',
      fontFamily: FONT.sans, cursor: clickable ? 'pointer' : 'default',
    };
    const handlers = clickable
      ? {
          onClick: () => onSelect(s.key),
          onMouseEnter: () => !isMobile && setHoveredKey(s.key),
          onMouseLeave: () => setHoveredKey(k => (k === s.key ? null : k)),
        }
      : {};
    return (
      <button type="button" data-testid="wiki-section-row" title={s.key} style={style} {...handlers}>
        {body}
      </button>
    );
  };

  const projectRow = (s: WikiSectionDto) => row(s, {
    icon: s.local
      ? <Monitor size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      : <Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />,
    label: s.name ?? ORPHAN_LABEL,
    muted: s.orphan || s.local,
    // Сирота — «проект удалён», локальный проект — «позже» (макет); бейдж сироты — её
    // пометка в дереве, кнопка удаления — в центре (WikiTab)
    badge: s.orphan
      ? { tone: 'neutral', text: 'проект удалён' }
      : s.local ? { tone: 'neutral', text: 'позже' } : undefined,
  });

  const sphereRow = (s: WikiSectionDto) => row(s, {
    icon: <span style={{ width: ICON_SIZE.xs, flexShrink: 0 }} />,
    label: s.name ?? 'Сфера',
    muted: true,
    reserved: true,
    badge: { tone: 'neutral', text: 'следующая фича', title: 'Раздел сферы — следующая фича: место есть, запись пока закрыта' },
  });

  // Постоянные разделы — в дереве всегда (WikiController.Tree отдаёт их даже пустыми)
  const fixedRow = (kind: 'common' | 'personal') => {
    const s = groups[kind][0];
    if (!s) return null;
    return row(s, {
      icon: kind === 'common'
        ? <Globe size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        : <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />,
      label: kind === 'common' ? 'Общее' : 'Личное',
      // Бейджи видимости — из макета; на 360 они не оставляют места имени раздела
      badge: isMobile ? undefined : {
        tone: 'neutral',
        text: kind === 'common' ? 'видно всем проектам' : 'только вне проектов',
      },
    });
  };

  return (
    <div data-testid="wiki-tree">
      <SidebarSection
        title="Разделы"
        actions={
          <span style={{ fontSize: FS.xs, color: C.textMuted, fontWeight: 400 }}>вся вики видна только вам</span>
        }
      >
        <SidebarSection title="Проекты" count={groups.project.length}>
          {groups.project.map(s => <Fragment key={s.key}>{projectRow(s)}</Fragment>)}
        </SidebarSection>

        <SidebarSection title="Сферы" count={groups.sphere.length}>
          {groups.sphere.length
            ? groups.sphere.map(s => <Fragment key={s.key}>{sphereRow(s)}</Fragment>)
            : (
              <div style={{ padding: `${SP.xs}px ${SP.sm}px`, fontSize: FS.sm, color: C.textMuted }}>
                раздел сферы — следующая фича
              </div>
            )}
        </SidebarSection>

        {fixedRow('common')}
        {fixedRow('personal')}
      </SidebarSection>
    </div>
  );
}
