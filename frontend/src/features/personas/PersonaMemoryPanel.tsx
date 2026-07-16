import { useCallback, useEffect, useRef, useState } from 'react';
import { ChevronLeft } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona, PersonaMemoryEntry, PersonaMemoryType, ServerMessage } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { onMessage } from '../../lib/signalr';
import { showToast } from '../../lib/toast';
import { personaLabel } from '../../lib/personas';
import { PersonaAvatar } from './PersonaAvatar';
import { MemoryPanel } from '../memory/MemoryPanel';
import type { MemoryEntryView, MemoryTypeMeta } from '../memory/memoryTypes';

// Панель долгой памяти персоны — тонкий адаптер над общей MemoryPanel (features/memory):
// фетч/realtime/мутации остаются здесь (персональные REST-эндпоинты), рендер списка/
// фильтров/добавления — в общем компоненте, единый вид с памятью команды проекта.

// Метаданные типов памяти персоны: заголовок + цвет-подсветка из палитры.
const TYPE_META: Record<PersonaMemoryType, MemoryTypeMeta> = {
  semantic:   { title: 'Факты',    hint: 'устойчивые сведения', color: C.accent,  softBg: C.accentSoft },
  episodic:   { title: 'Эпизоды',  hint: 'что было в разговорах', color: C.info,    softBg: C.infoBg },
  procedural: { title: 'Приёмы',   hint: 'привычные способы',    color: C.success, softBg: C.successBg },
};
const TYPE_ORDER: PersonaMemoryType[] = ['semantic', 'episodic', 'procedural'];

// Ручной ввод из UI никогда не передаёт sourceSessionId — значит его наличие надёжно
// означает «пришло из autolearn» (даже после подтверждения pending гасится в false, но
// sourceSessionId не теряется).
function toView(e: PersonaMemoryEntry): MemoryEntryView<PersonaMemoryType> {
  return {
    id: e.id, type: e.type, text: e.text, tags: e.tags, salience: e.salience,
    createdAt: e.createdAt,
    origin: e.sourceSessionId ? 'auto' : 'manual',
    originDetail: e.sourceSessionId ? 'из хода' : undefined,
    pending: e.pending,
  };
}

export function PersonaMemoryPanel({ persona, onBack, isMobile, embedded }: {
  persona: Persona;
  onBack?: () => void;
  isMobile: boolean;
  // embedded — панель отрисована под стрипом персоны (в PersonaChat), свой
  // заголовок не нужен: идентичность уже показана выше.
  embedded?: boolean;
}) {
  const [entries, setEntries] = useState<PersonaMemoryEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  const load = useCallback(async () => {
    try {
      const list = await api.personas.memory(persona.id);
      setEntries(list);
      setError(false);
    } catch {
      setError(true);
    } finally {
      setLoading(false);
    }
  }, [persona.id]);

  // Первичная загрузка при смене персоны
  useEffect(() => {
    setLoading(true);
    void load();
  }, [load]);

  // Realtime: память текущей персоны изменилась (персона запомнил/забыл) — перечитать.
  // joinUser уже сделан стором personas; здесь только слушаем сообщения.
  const loadRef = useRef(load);
  loadRef.current = load;
  useEffect(() => {
    const off = onMessage((msg: ServerMessage) => {
      if (msg.type === 'personas_changed' && msg.action === 'memory' && msg.personaId === persona.id) {
        void loadRef.current();
      }
    });
    return off;
  }, [persona.id]);

  const add = async (type: PersonaMemoryType, text: string) => {
    try {
      const created = await api.personas.remember(persona.id, { type, text });
      // Оптимистично добавляем (realtime продублирует перезагрузкой)
      setEntries(prev => [created, ...prev.filter(e => e.id !== created.id)]);
    } catch {
      showToast('Память', 'Не удалось сохранить запись.');
    }
  };

  const edit = async (entryId: string, text: string) => {
    try {
      const updated = await api.personas.updateMemory(persona.id, entryId, text);
      setEntries(prev => prev.map(e => e.id === entryId ? updated : e));
    } catch {
      showToast('Память', 'Не удалось сохранить изменения.');
    }
  };

  const remove = async (entryId: string) => {
    try {
      await api.personas.forget(persona.id, entryId);
      setEntries(prev => prev.filter(e => e.id !== entryId));
    } catch {
      showToast('Память', 'Не удалось удалить запись.');
    }
  };

  // Подтвердить предложенную autolearn запись (③-3.2): снимает pending → попадает в recall
  const confirm = async (entryId: string) => {
    try {
      await api.personas.confirmMemory(persona.id, entryId);
      setEntries(prev => prev.map(e => e.id === entryId ? { ...e, pending: false } : e));
    } catch {
      showToast('Память', 'Не удалось подтвердить запись.');
    }
  };

  // Превратить запись памяти в заметку (③-3.3): инсайт выходит в общий vault
  const toNote = async (entryId: string) => {
    try {
      const res = await api.personas.memoryToNote(persona.id, entryId);
      showToast('Память', `Создана заметка «${res.noteTitle}».`, 'info');
    } catch {
      showToast('Память', 'Не удалось создать заметку.');
    }
  };

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgMain, overflow: 'hidden' }}>
      {/* Шапка панели — скрыта во встроенном режиме (идентичность в стрипе) */}
      {!embedded && (
        <div style={{
          flex: 'none', display: 'flex', alignItems: 'center', gap: 10, padding: '10px 14px',
          borderBottom: `1px solid ${C.border}`, background: C.bgPanel,
        }}>
          {onBack && (
            <button onClick={onBack} aria-label="Назад" style={iconBtn}>
              <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            </button>
          )}
          <PersonaAvatar persona={persona} size={30} />
          <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
            <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              Память · {personaLabel(persona)}
            </div>
            <div style={{ fontSize: 11.5, color: C.textMuted }}>
              {loading ? 'загрузка…' : `${entries.length} ${plural(entries.length)}`}
            </div>
          </div>
        </div>
      )}

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: isMobile ? '12px 12px 24px' : '16px 20px 32px' }}>
        <MemoryPanel<PersonaMemoryType>
          entries={loading ? null : entries.map(toView)}
          error={error}
          onRetry={() => { setLoading(true); void load(); }}
          typeMeta={TYPE_META}
          typeOrder={TYPE_ORDER}
          addHint="Персона будет считать это устойчивым фактом и опираться на него в разговоре."
          addPlaceholder="Добавить факт, который персона должна помнить…"
          onAdd={add}
          onEdit={edit}
          onRemove={remove}
          onConfirm={confirm}
          onToNote={toNote}
          emptyIcon="🧠"
          emptyTitle="Память пуста"
          emptyHint="Персона пока ничего не запомнил. Память пополняется во время разговоров."
        />
      </div>
    </div>
  );
}

function plural(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'запись';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'записи';
  return 'записей';
}

const iconBtn: React.CSSProperties = {
  width: 32, height: 32, borderRadius: R.md, border: 'none', background: 'transparent',
  color: C.textSecondary, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
};
