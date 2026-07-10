import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Persona, PersonaMemoryEntry, PersonaMemoryType, ServerMessage } from '../../types';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { api } from '../../lib/api';
import { onMessage } from '../../lib/signalr';
import { PersonaAvatar } from './PersonaAvatar';

// Панель долгой памяти персоны (этап 3). Показывает записи, сгруппированные по
// типу, позволяет вручную добавить факт и удалить запись. Реагирует на realtime
// personas_changed(action='memory') для текущей персоны — перечитывает записи.

// Метаданные категорий памяти: заголовок группы + цвет-подсветка из палитры.
const TYPE_META: Record<PersonaMemoryType, { title: string; hint: string; color: string; softBg: string }> = {
  semantic:   { title: 'Факты',    hint: 'устойчивые сведения', color: C.accent,  softBg: C.accentSoft },
  episodic:   { title: 'Эпизоды',  hint: 'что было в разговорах', color: C.info,    softBg: C.infoBg },
  procedural: { title: 'Приёмы',   hint: 'привычные способы',    color: C.success, softBg: C.successBg },
};

// Порядок вывода групп
const TYPE_ORDER: PersonaMemoryType[] = ['semantic', 'episodic', 'procedural'];

// Короткая относительная дата: «только что», «5 мин», «3 ч», «2 дн», иначе — число/месяц
function shortAgo(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '';
  const diffMs = Date.now() - t;
  const min = Math.floor(diffMs / 60000);
  if (min < 1) return 'только что';
  if (min < 60) return `${min} мин`;
  const h = Math.floor(min / 60);
  if (h < 24) return `${h} ч`;
  const d = Math.floor(h / 24);
  if (d < 7) return `${d} дн`;
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' }).replace('.', '');
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

  // Форма ручного добавления
  const [addType, setAddType] = useState<PersonaMemoryType>('semantic');
  const [addText, setAddText] = useState('');
  const [saving, setSaving] = useState(false);
  const [removingId, setRemovingId] = useState<string | null>(null);

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

  // Realtime: память текущей персоны изменилась (агент запомнил/забыл) — перечитать.
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

  const groups = useMemo(() => {
    const by: Record<PersonaMemoryType, PersonaMemoryEntry[]> = { semantic: [], episodic: [], procedural: [] };
    for (const e of entries) (by[e.type] ??= []).push(e);
    // Внутри группы — свежие сверху
    for (const k of TYPE_ORDER) by[k].sort((a, b) => (b.createdAt || '').localeCompare(a.createdAt || ''));
    return by;
  }, [entries]);

  const submit = async () => {
    const text = addText.trim();
    if (!text || saving) return;
    setSaving(true);
    try {
      const created = await api.personas.remember(persona.id, { type: addType, text });
      // Оптимистично добавляем (realtime продублирует перезагрузкой)
      setEntries(prev => [created, ...prev.filter(e => e.id !== created.id)]);
      setAddText('');
    } catch {
      alert('Не удалось сохранить запись.');
    } finally {
      setSaving(false);
    }
  };

  const remove = async (entryId: string) => {
    setRemovingId(entryId);
    try {
      await api.personas.forget(persona.id, entryId);
      setEntries(prev => prev.filter(e => e.id !== entryId));
    } catch {
      alert('Не удалось удалить запись.');
    } finally {
      setRemovingId(null);
    }
  };

  const isEmpty = !loading && !error && entries.length === 0;

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
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M15 18l-6-6 6-6" />
              </svg>
            </button>
          )}
          <PersonaAvatar persona={persona} size={30} />
          <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
            <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              Память · {persona.name}
            </div>
            <div style={{ fontSize: 11.5, color: C.textMuted }}>
              {loading ? 'загрузка…' : `${entries.length} ${plural(entries.length)}`}
            </div>
          </div>
        </div>
      )}

      {/* Список записей */}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: isMobile ? '12px 12px 4px' : '16px 20px 8px' }}>
        {loading && (
          <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontSize: 13 }}>Загрузка…</div>
        )}
        {error && !loading && (
          <div style={{ padding: 40, textAlign: 'center', color: C.dangerText, fontSize: 13 }}>
            Не удалось загрузить память.{' '}
            <button onClick={() => { setLoading(true); void load(); }} style={linkBtn}>Повторить</button>
          </div>
        )}
        {isEmpty && (
          <div style={{ padding: '48px 20px', textAlign: 'center', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 8 }}>
            <div style={{ fontSize: 30, opacity: 0.5 }}>🧠</div>
            <div style={{ fontFamily: FONT.serif, fontSize: 17, color: C.textHeading }}>Память пуста</div>
            <div style={{ fontSize: 13, color: C.textSecondary, maxWidth: 320, lineHeight: 1.5 }}>
              Агент пока ничего не запомнил. Память пополняется во время разговоров.
            </div>
          </div>
        )}
        {!loading && !error && entries.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 18, maxWidth: 720, margin: '0 auto' }}>
            {TYPE_ORDER.filter(t => groups[t].length > 0).map(t => {
              const meta = TYPE_META[t];
              return (
                <div key={t}>
                  <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 8, padding: '0 2px' }}>
                    <span style={{ width: 8, height: 8, borderRadius: R.full, background: meta.color, flex: 'none' }} />
                    <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 700, color: C.textHeading, letterSpacing: '0.01em' }}>
                      {meta.title}
                    </span>
                    <span style={{ fontSize: 11.5, color: C.textMuted }}>{groups[t].length}</span>
                    <span style={{ fontSize: 11.5, color: C.textMuted, fontStyle: 'italic', marginLeft: 'auto' }}>{meta.hint}</span>
                  </div>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                    {groups[t].map(e => (
                      <MemoryCard key={e.id} entry={e} color={meta.color} onRemove={() => remove(e.id)} removing={removingId === e.id} />
                    ))}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* Форма ручного добавления */}
      <div style={{
        flex: 'none', borderTop: `1px solid ${C.border}`, background: C.bgPanel,
        padding: isMobile ? '10px 12px' : '12px 20px',
      }}>
        <div style={{ display: 'flex', gap: 8, alignItems: 'stretch', maxWidth: 720, margin: '0 auto', flexWrap: isMobile ? 'wrap' : 'nowrap' }}>
          <select
            value={addType}
            onChange={e => setAddType(e.target.value as PersonaMemoryType)}
            style={selectStyle}
            aria-label="Тип записи"
          >
            {TYPE_ORDER.map(t => <option key={t} value={t}>{TYPE_META[t].title}</option>)}
          </select>
          <input
            value={addText}
            onChange={e => setAddText(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void submit(); } }}
            placeholder="Добавить факт, который агент должен помнить…"
            style={{ ...inputStyle, flex: 1, minWidth: isMobile ? '100%' : 180 }}
          />
          <button
            onClick={() => void submit()}
            disabled={saving || !addText.trim()}
            style={{ ...primaryBtn, opacity: saving || !addText.trim() ? 0.55 : 1, cursor: saving || !addText.trim() ? 'default' : 'pointer' }}
          >
            {saving ? 'Сохраняю…' : 'Запомнить'}
          </button>
        </div>
      </div>
    </div>
  );
}

// Карточка одной записи памяти
function MemoryCard({ entry, color, onRemove, removing }: {
  entry: PersonaMemoryEntry;
  color: string;
  onRemove: () => void;
  removing: boolean;
}) {
  return (
    <div style={{
      position: 'relative', background: C.bgWhite, border: `1px solid ${C.border}`,
      borderLeft: `3px solid ${color}`, borderRadius: R.xl, padding: '10px 12px',
      opacity: removing ? 0.5 : 1,
    }}>
      <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: 13.5, color: C.textPrimary, lineHeight: 1.5, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
            {entry.text}
          </div>
          {(entry.tags && entry.tags.length > 0) && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5, marginTop: 7 }}>
              {entry.tags.map(tag => (
                <span key={tag} style={{
                  fontSize: 11, color: C.textSecondary, background: C.bgInset,
                  border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '1px 7px',
                }}>#{tag}</span>
              ))}
            </div>
          )}
          <div style={{ fontSize: 11, color: C.textMuted, marginTop: 6 }}>{shortAgo(entry.createdAt)}</div>
        </div>
        <button
          onClick={onRemove}
          disabled={removing}
          aria-label="Забыть"
          title="Забыть"
          style={forgetBtn}
        >
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M18 6L6 18M6 6l12 12" />
          </svg>
        </button>
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
const forgetBtn: React.CSSProperties = {
  width: 26, height: 26, borderRadius: R.md, border: 'none', background: 'transparent',
  color: C.textMuted, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
};
const selectStyle: React.CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: '0 10px', fontSize: 13, fontFamily: FONT.sans, color: C.textHeading, cursor: 'pointer', height: 38, flex: 'none',
};
const inputStyle: React.CSSProperties = {
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: '0 12px', fontSize: 14, fontFamily: FONT.sans, color: C.textHeading, height: 38, outline: 'none',
};
const primaryBtn: React.CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.xl,
  padding: '0 16px', height: 38, fontSize: 13, fontWeight: 600, fontFamily: FONT.sans, flex: 'none',
  boxShadow: SHADOW.button, whiteSpace: 'nowrap',
};
const linkBtn: React.CSSProperties = {
  background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
  fontSize: 13, fontFamily: FONT.sans, textDecoration: 'underline', padding: 0,
};
