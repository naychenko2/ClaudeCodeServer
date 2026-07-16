import type { TeamMemoryEntry, TeamMemoryType } from '../../types';
import { C, FONT } from '../../lib/design';
import { MemoryPanel } from '../memory/MemoryPanel';
import type { MemoryEntryView, MemoryTypeMeta } from '../memory/memoryTypes';

// Вкладка «Память команды» (Командный центр проекта) — тонкий адаптер над общей
// MemoryPanel (features/memory): те же фильтры/сортировка/карточка, что у личной
// памяти персоны (PersonaMemoryPanel), только 4 типа и без pending-гейта (авто-факты
// команды складываются сразу, без подтверждения — см. TeamMemoryAutolearnService).

const TYPE_META: Record<TeamMemoryType, MemoryTypeMeta> = {
  decision:   { title: 'Решение',    hint: 'принятый выбор',        color: C.accent,  softBg: C.accentSoft },
  convention: { title: 'Договорённость', hint: 'правило проекта',   color: C.info,    softBg: C.infoBg },
  fact:       { title: 'Факт',       hint: 'устойчивые сведения',   color: C.success, softBg: C.successBg },
  glossary:   { title: 'Термин',     hint: 'значение в проекте',    color: C.plan,    softBg: C.planLight },
};
const TYPE_ORDER: TeamMemoryType[] = ['decision', 'convention', 'fact', 'glossary'];

// Явное поле Source с бэка (в отличие от памяти персоны — там источник выводится из
// sourceSessionId): manual → ручное, autoTurn/autoMeeting → авто с уточнением.
function toView(e: TeamMemoryEntry): MemoryEntryView<TeamMemoryType> {
  return {
    id: e.id, type: e.type, text: e.text, tags: e.tags, salience: e.salience,
    createdAt: e.createdAt,
    origin: e.source === 'manual' ? 'manual' : 'auto',
    originDetail: e.source === 'autoMeeting' ? 'из совещания' : e.source === 'autoTurn' ? 'из хода' : undefined,
  };
}

export function TeamMemoryPanel({ mem, onAdd, onUpdate, onRemove, stripe }: {
  mem: TeamMemoryEntry[] | null;
  onAdd: (text: string, type: TeamMemoryType) => Promise<unknown>;
  onUpdate: (id: string, text: string) => Promise<unknown>;
  onRemove: (id: string) => Promise<unknown>;
  stripe: string;
}) {
  const add = (type: TeamMemoryType, text: string) => onAdd(text, type);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span style={{ width: 6, height: 6, borderRadius: '50%', background: stripe, flexShrink: 0 }} />
        <span style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1.5 }}>
          Общие факты и договорённости проекта — их recall'ят все персоны команды в каждом ходе.
        </span>
      </div>
      <MemoryPanel<TeamMemoryType>
        entries={mem ? mem.map(toView) : null}
        typeMeta={TYPE_META}
        typeOrder={TYPE_ORDER}
        addHint="Запись увидят все персоны команды проекта наравне с личной памятью."
        addPlaceholder="Напр.: ревью через PR, не напрямую в main; релизы по пятницам"
        onAdd={add}
        onEdit={onUpdate}
        onRemove={onRemove}
        emptyIcon="👥"
        emptyTitle="Пока пусто"
        emptyHint="Запишите общий факт выше — его запомнят все персоны команды проекта."
      />
    </div>
  );
}
