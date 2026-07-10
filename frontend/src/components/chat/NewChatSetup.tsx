import { useState } from 'react';
import type { Session } from '../../types';
import { api } from '../../lib/api';
import { useModels, useModelCaps, modelCaps, modelProvider, useModelLabel } from '../../lib/models';
import { effortsForProvider, effortLabel } from '../../lib/effort';
import { EXPIRY_PRESETS, expiryOptionLabel } from '../../lib/expiry';
import { ModelPicker } from '../ModelPicker';
import { SegmentedControl } from '../ui';
import { C, R, FONT, SHADOW } from '../../lib/design';

// Настройка будущего чата в пустом состоянии (до первого сообщения): выбор модели и
// усилия рассуждения двумя пилюлями с инлайн-раскрытием. Значения сразу пишутся в
// сессию (провайдер ещё не «начат» — смена модели/провайдера разрешена). Инлайн-карточка
// вместо плавающего поповера — надёжнее на мобильном, а в пустом чате места по вертикали хватает.

type Panel = 'model' | 'effort' | 'expiry' | null;

// Иконка «чип» (модель)
const IconModel = (
  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
    <rect x="4" y="4" width="16" height="16" rx="2" />
    <rect x="9" y="9" width="6" height="6" />
    <path d="M9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3" />
  </svg>
);
// Иконка «молния» (усиление рассуждения)
const IconEffort = (
  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
    <path d="M13 2 3 14h9l-1 8 10-12h-9z" />
  </svg>
);
// Иконка «песочные часы» (время жизни временного чата)
const IconExpiry = (
  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
    <path d="M6 2h12M6 22h12M8 2v4l4 4 4-4V2M8 22v-4l4-4 4 4v4" />
  </svg>
);

function Chevron({ open }: { open: boolean }) {
  return (
    <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4"
      strokeLinecap="round" strokeLinejoin="round"
      style={{ flexShrink: 0, color: C.textMuted, transition: 'transform 0.15s', transform: open ? 'rotate(180deg)' : 'none' }}>
      <path d="m6 9 6 6 6-6" />
    </svg>
  );
}

export function NewChatSetup({ session, onSessionUpdated, isMobile }: {
  session: Session;
  onSessionUpdated?: (s: Session) => void;
  isMobile?: boolean;
}) {
  const models = useModels();
  const caps = useModelCaps(session.model);
  const modelName = useModelLabel(session.model);
  const [panel, setPanel] = useState<Panel>(null);
  const [saving, setSaving] = useState(false);

  // Update на бэке — полная замена (Name/Model/Effort перезаписываются целиком, отсутствующее → null),
  // поэтому шлём весь набор, подмешивая текущие значения сессии; иначе выбор усилия затёр бы модель/имя.
  const persist = async (next: { model?: string | null; effort?: string | null; expiresAfterMinutes?: number | null }) => {
    setSaving(true);
    try {
      const data = {
        name: session.name ?? null,
        model: next.model !== undefined ? next.model : (session.model ?? null),
        effort: next.effort !== undefined ? next.effort : (session.effort ?? null),
        // Время жизни — sentinel-семантика на бэке: отсутствие поля = не менять
        ...(next.expiresAfterMinutes !== undefined && { expiresAfterMinutes: next.expiresAfterMinutes }),
      };
      // Проектная сессия — через /projects/{id}/sessions, чат вне проекта — через /chats (как в EditSessionDialog)
      const updated = session.projectId
        ? await api.sessions.update(session.projectId, session.id, data)
        : await api.chats.update(session.id, data);
      onSessionUpdated?.(updated);
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
  const pickExpiry = (v: string) => {
    const minutes = v ? Number(v) : null;
    if (minutes !== (session.expiresAfterMinutes ?? null)) persist({ expiresAfterMinutes: minutes });
    setPanel(null);
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

  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, marginTop: 20, width: '100%' }}>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center' }}>
        {pill('model', IconModel, 'Модель', modelName)}
        {caps.supportsEffort && pill('effort', IconEffort, 'Усилие', effortLabel(session.effort))}
        {pill('expiry', IconExpiry, 'Время жизни', expiryOptionLabel(session.expiresAfterMinutes))}
      </div>

      {panel && (
        <div style={{
          width: isMobile ? '100%' : 380, maxWidth: '100%',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.card, padding: 12, textAlign: 'left',
          maxHeight: 320, overflowY: 'auto',
        }}>
          {panel === 'model' ? (
            <ModelPicker value={session.model ?? ''} options={models} onChange={pickModel} collapsible={false} />
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
          ) : (
            <>
              <div style={{ fontSize: 11.5, color: C.textMuted, marginBottom: 8, lineHeight: 1.4 }}>
                Временный чат удалится сам вместе с историей, если не будет активности выбранное время.
              </div>
              <SegmentedControl
                value={session.expiresAfterMinutes ? String(session.expiresAfterMinutes) : ''}
                options={[{ value: '', label: 'Бессрочно' }, ...EXPIRY_PRESETS.map(p => ({ value: String(p.minutes), label: p.label }))]}
                onChange={pickExpiry}
                columns={3}
              />
            </>
          )}
        </div>
      )}
    </div>
  );
}
