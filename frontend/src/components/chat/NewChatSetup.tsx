import { useEffect, useState } from 'react';
import { Cpu, Zap, Hourglass, History, Lock, Tag as TagIcon, ChevronDown } from 'lucide-react';
import type { Session, Project, ProjectTag } from '../../types';
import { api } from '../../lib/api';
import { useModels, useModelCaps, modelCaps, modelProvider, useModelLabel, modelLabel, USAGE } from '../../lib/models';
import { effortsForProvider, effortLabel } from '../../lib/effort';
import { expiryOptionLabel } from '../../lib/expiry';
import { updateChatFields, type ChatFieldsPatch } from '../../lib/chatUpdate';
import { ExpiryPicker } from './ExpiryPicker';
import { DossierOptOutRow } from './DossierOptOutRow';
import { ModelPicker } from '../ModelPicker';
import { SegmentedControl } from '../ui';
import { TagPickerBody } from '../TagChip';
import { useEffectiveLine } from '../../lib/presets';
import { C, R, FONT, SHADOW, GROUP_COLORS } from '../../lib/design';

// Настройка будущего чата в пустом состоянии (до первого сообщения): выбор модели,
// усилия рассуждения, времени жизни и тегов пилюлями с инлайн-раскрытием. Значения сразу
// пишутся в сессию (провайдер ещё не «начат» — смена модели/провайдера разрешена). Инлайн-карточка
// вместо плавающего поповера — надёжнее на мобильном, а в пустом чате места по вертикали хватает.

type Panel = 'model' | 'effort' | 'expiry' | 'dossiers' | 'tags' | null;

// Иконка «чип» (модель)
const IconModel = <Cpu size={15} strokeWidth={2} style={{ flexShrink: 0 }} />;
// Иконка «молния» (усиление рассуждения)
const IconEffort = <Zap size={15} strokeWidth={2} style={{ flexShrink: 0 }} />;
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
  const models = useModels();
  const caps = useModelCaps(session.model);
  const modelName = useModelLabel(session.model);
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

  const pickModel = (v: string) => {
    if (v !== (session.model ?? '')) {
      // Новый провайдер может не поддерживать усилие — тогда сбрасываем его вместе с моделью
      const nextCaps = modelCaps(v);
      persist({ model: v || null, ...(nextCaps.supportsEffort ? {} : { effort: null }) });
    }
    setPanel(null);
  };
  const pickEffort = (v: string) => {
    if (v !== (session.effort ?? '')) persist({ effort: v || null });
    setPanel(null);
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
          <span style={{ fontSize: 9.5, fontWeight: 700, textTransform: 'uppercase', letterSpacing: 0.3, color: C.textMuted }}>{label}</span>
          <span style={{
            fontSize: 13, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap',
            maxWidth: isMobile ? 130 : 190, overflow: 'hidden', textOverflow: 'ellipsis',
          }}>
            {value}
          </span>
        </span>
        <Chevron open={active} />
      </button>
    );
  };

  // «Сейчас пойдёт» для превью под пилюлями: на явной модели показываем её саму,
  // на дефолте — резолв места по матрице персоны/слота/назначения
  const explicitModel = (session.model ?? '').trim();
  // Хук зовём БЕЗУСЛОВНО, а результат применяем по условию: выбор модели прямо в этом
  // диалоге переключает explicitModel между пустым и заполненным, то есть при условном
  // вызове число хуков менялось бы между рендерами и React падал бы на «Rendered fewer
  // hooks than expected». Лишним запрос не будет — превью кэшируется в presets.ts.
  const effectiveLine = useEffectiveLine({
    kind: 'action',
    actionKey: session.personaId ? USAGE.chatPersona : USAGE.chatNew,
  });
  const previewLine = explicitModel
    ? `Сейчас пойдёт: ${modelLabel(explicitModel)}`
    : (effectiveLine ?? 'Сейчас пойдёт: выбираем…');

  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, marginTop: 20, width: '100%' }}>
      {/* Плашка-сделка о заморозке модели: пользователь выбирает модель ДО первого хода,
          дальше правки цепочки и уровней действуют на новые чаты, этот — не изменят.
          Видна всегда, не под панелью — её главное прочитать до клика по пилюле. */}
      <div style={{
        display: 'flex', alignItems: 'flex-start', gap: 7,
        width: isMobile ? '100%' : 380, maxWidth: '100%',
        padding: '8px 12px', borderRadius: R.lg,
        background: C.bgPanel, border: `1px solid ${C.border}`,
        fontFamily: FONT.sans, fontSize: 11.5, color: C.textSecondary, lineHeight: 1.4,
        textAlign: 'left',
      }}>
        <Lock size={12} strokeWidth={2} style={{ flexShrink: 0, marginTop: 2, color: C.textMuted }} />
        <span>Разговор держит выбранную модель до конца: правки цепочки и уровней действуют на новые чаты, этот — не изменят.</span>
      </div>

      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center' }}>
        {pill('model', IconModel, 'Модель', modelName)}
        {caps.supportsEffort && pill('effort', IconEffort, 'Усилие', effortLabel(session.effort))}
        {pill('expiry', IconExpiry, 'Время жизни', expiryOptionLabel(session.expiresAfterMinutes))}
        {/* История решений — только у проектных чатов (личные в неё не пишутся) */}
        {project && pill('dossiers', IconDossiers, 'История решений', session.excludeFromDossiers ? 'Не сохраняются' : 'Сохраняются')}
        {/* Теги — только у проектных чатов (реестр тегов per-project) */}
        {project && pill('tags', IconTags, 'Теги', session.tags?.length ? session.tags.join(', ') : 'Без тегов')}
      </div>

      {panel && (
        <div style={{
          width: isMobile ? '100%' : 380, maxWidth: '100%',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.card, padding: 12, textAlign: 'left',
          maxHeight: 320, overflowY: 'auto',
        }}>
          {panel === 'model' ? (
            <ModelPicker value={session.model ?? ''} options={models} onChange={pickModel} collapsible={false}
              usage={session.personaId ? USAGE.chatPersona : USAGE.chatNew} />
          ) : panel === 'effort' ? (
            <>
              <div style={{ fontSize: 11.5, color: C.textMuted, marginBottom: 8, lineHeight: 1.4 }}>
                Выше — глубже размышляет, но дольше и дороже.
              </div>
              <SegmentedControl
                value={session.effort ?? ''}
                options={effortsForProvider(modelProvider(session.model))}
                onChange={pickEffort}
                columns={3}
              />
            </>
          ) : panel === 'tags' ? (
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

      {/* «Сейчас пойдёт» под пилюлями — виден всегда, кроме случая, когда модель
          сейчас правят в раскрытой панели: внутри неё уже видно своё «Сейчас пойдёт».
          Дубль здесь не нужен. */}
      {!panel && (
        <div style={{
          fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, lineHeight: 1.4,
          textAlign: 'center',
        }}>
          {previewLine}
        </div>
      )}
    </div>
  );
}
