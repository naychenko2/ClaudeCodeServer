import { useEffect, useState } from 'react';
import { Hourglass, History, Lock, Tag as TagIcon, ChevronDown } from 'lucide-react';
import type { Session, Project, ProjectTag } from '../../types';
import { api } from '../../lib/api';
import { expiryOptionLabel } from '../../lib/expiry';
import { updateChatFields, type ChatFieldsPatch } from '../../lib/chatUpdate';
import { ExpiryPicker } from './ExpiryPicker';
import { DossierOptOutRow } from './DossierOptOutRow';
import { TagPickerBody } from '../TagChip';
import { C, R, SP, FS, FONT, SHADOW, GROUP_COLORS } from '../../lib/design';

// Настройка будущего чата в пустом состоянии (до первого сообщения): время жизни, история
// решений и теги пилюлями с инлайн-раскрытием. Модели и усилия здесь нет — их выбирают в
// композере, второй путь к той же настройке только дублировал его. Значения сразу пишутся
// в сессию. Инлайн-карточка вместо плавающего поповера — надёжнее на мобильном.

type Panel = 'expiry' | 'dossiers' | 'tags' | null;

// Иконка «песочные часы» (время жизни временного чата)
const IconExpiry = <Hourglass size={15} strokeWidth={2} style={{ flexShrink: 0 }} />;
// Иконка «история» (opt-out «не сохранять решения из этого чата»)
const IconDossiers = <History size={15} strokeWidth={2} style={{ flexShrink: 0 }} />;
// Иконка «тег»
const IconTags = <TagIcon size={15} strokeWidth={2} style={{ flexShrink: 0 }} />;

function Chevron({ open }: { open: boolean }) {
  return (
    <ChevronDown size={11} color={C.textMuted} strokeWidth={2}
      style={{ flexShrink: 0, transition: 'transform 0.15s', transform: open ? 'rotate(180deg)' : 'none' }} />
  );
}

export function NewChatSetup({ session, project, onSessionUpdated, isMobile }: {
  session: Session;
  // Только для проектных чатов — реестр тегов проекта (per-project, у чатов вне проекта тегов нет)
  project?: Project;
  onSessionUpdated?: (s: Session) => void;
  isMobile?: boolean;
}) {
  const [panel, setPanel] = useState<Panel>(null);
  const [saving, setSaving] = useState(false);

  // Реестр тегов — optimistic state поверх project.tagRegistry (тот же паттерн, что в
  // SessionList): создание тега здесь видно сразу, не дожидаясь обновления project сверху.
  const [registryOverride, setRegistryOverride] = useState<ProjectTag[] | null>(null);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс оверрайда реестра тегов при смене проекта
  useEffect(() => { setRegistryOverride(null); }, [project?.id, project?.tagRegistry]);
  const registry = registryOverride ?? project?.tagRegistry ?? [];

  // Частичное обновление полей и выбор эндпоинта по projectId — в updateChatFields
  const persist = async (next: ChatFieldsPatch) => {
    setSaving(true);
    try {
      onSessionUpdated?.(await updateChatFields(session, next));
    } catch {
      // молча: не критично — значение просто не применится
    } finally {
      setSaving(false);
    }
  };

  const pickExpiry = (minutes: number | null) => {
    if (minutes !== (session.expiresAfterMinutes ?? null)) persist({ expiresAfterMinutes: minutes });
    setPanel(null);
  };

  // Теги — мультивыбор, панель после клика не закрывается (можно отметить несколько подряд)
  const toggleTag = (name: string) => {
    const tags = session.tags ?? [];
    const has = tags.some(t => t.toLowerCase() === name.toLowerCase());
    persist({ tags: has ? tags.filter(t => t.toLowerCase() !== name.toLowerCase()) : [...tags, name] });
  };

  // Новый тег: в реестр проекта (цвет — следующий из палитры по кругу) и сразу на чат
  const createTag = (name: string) => {
    if (!project) return;
    const color = GROUP_COLORS[registry.length % GROUP_COLORS.length];
    const nextRegistry = [...registry, { name, order: registry.length, color }];
    setRegistryOverride(nextRegistry);
    api.projects.updateTags(project.id, nextRegistry)
      .then(p => setRegistryOverride(p.tagRegistry ?? nextRegistry))
      .catch(() => setRegistryOverride(registry));
    const tags = session.tags ?? [];
    if (!tags.some(t => t.toLowerCase() === name.toLowerCase())) {
      persist({ tags: [...tags, name] });
    }
  };

  const toggle = (p: Exclude<Panel, null>) => setPanel(cur => (cur === p ? null : p));

  const pill = (p: Exclude<Panel, null>, icon: React.ReactNode, label: string, value: string) => {
    const active = panel === p;
    return (
      <button
        type="button"
        onClick={() => toggle(p)}
        disabled={saving}
        style={{
          display: 'flex', alignItems: 'center', gap: 9,
          padding: isMobile ? '8px 12px' : '7px 12px',
          borderRadius: R.lg, cursor: saving ? 'default' : 'pointer',
          border: `1px solid ${active ? C.accent : C.border}`,
          background: active ? C.accentLight : C.bgWhite,
          fontFamily: FONT.sans, opacity: saving ? 0.7 : 1,
        }}
      >
        <span style={{ color: C.accent, display: 'flex' }}>{icon}</span>
        <span style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-start', lineHeight: 1.2, minWidth: 0 }}>
          <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted }}>{label}</span>
          <span style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap',
            maxWidth: isMobile ? 130 : 190, overflow: 'hidden', textOverflow: 'ellipsis',
          }}>
            {value}
          </span>
        </span>
        <Chevron open={active} />
      </button>
    );
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm, marginTop: SP.md, width: '100%' }}>
      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
        {pill('expiry', IconExpiry, 'Время жизни', expiryOptionLabel(session.expiresAfterMinutes))}
        {/* История решений — только у проектных чатов (личные в неё не пишутся) */}
        {project && pill('dossiers', IconDossiers, 'История решений', session.excludeFromDossiers ? 'Не сохраняются' : 'Сохраняются')}
        {/* Теги — только у проектных чатов (реестр тегов per-project) */}
        {project && pill('tags', IconTags, 'Теги', session.tags?.length ? session.tags.join(', ') : 'Без тегов')}
      </div>

      {/* Заморозка модели: после первого хода правки цепочки и уровней чат не меняют */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 6, maxWidth: '100%',
        fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        <Lock size={12} strokeWidth={2} style={{ flexShrink: 0 }} />
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>Модель закрепится за чатом до конца разговора</span>
      </div>

      {panel && (
        <div style={{
          width: isMobile ? '100%' : 380, maxWidth: '100%',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.card, padding: 12, textAlign: 'left',
          maxHeight: 320, overflowY: 'auto',
        }}>
          {panel === 'tags' ? (
            <TagPickerBody
              registry={registry}
              selected={session.tags ?? []}
              onToggle={toggleTag}
              onCreate={createTag}
            />
          ) : panel === 'dossiers' ? (
            <DossierOptOutRow value={!!session.excludeFromDossiers} onChange={v => persist({ excludeFromDossiers: v })} />
          ) : (
            <ExpiryPicker value={session.expiresAfterMinutes} onChange={pickExpiry} />
          )}
        </div>
      )}
    </div>
  );
}
