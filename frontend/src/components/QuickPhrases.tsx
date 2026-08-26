import { useEffect, useState } from 'react';
import { MessageSquareText, Plus, Settings2, Trash2 } from 'lucide-react';
import { C, FONT, FS, MODAL_W, R } from '../lib/design';
import { Button, Field, IconButton, Menu, MenuItem, MenuSep, Modal, ModalActions, TextField } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import {
  QUICK_PHRASE_MAX_COUNT, QUICK_PHRASE_MAX_LENGTH,
  ensureQuickPhrases, quickPhrasesFailed, quickPhrasesLoaded, saveQuickPhrases, useQuickPhrases,
} from '../lib/quickPhrases';

// Быстрые фразы композера: попап со списком готовых сообщений (клик — ход уходит
// в чат немедленно) и модалка правки набора. Набор личный и общий для всех чатов
// (см. lib/quickPhrases.ts). Сама кнопка живёт в Composer рядом с микрофоном —
// там она делит стиль и защиту от long-press с соседями по ряду.

export function QuickPhrasesMenu({ anchor, onClose, onPick, onEdit }: {
  // rect кнопки-триггера: попап открывается у нижней кромки экрана, и Menu сам
  // развернёт карточку вверх
  anchor: DOMRect;
  onClose: () => void;
  // Выбор фразы: отправляем как есть, поле ввода не трогаем
  onPick: (text: string) => void;
  onEdit: () => void;
}) {
  const phrases = useQuickPhrases();
  const loaded = quickPhrasesLoaded();
  const failed = quickPhrasesFailed();

  // Список тянем в момент открытия: раньше он не нужен, а на каждый чат лишний запрос
  useEffect(() => { void ensureQuickPhrases(); }, []);

  // Закрытие по Esc — поведение вызывающей стороны (см. комментарий в ui/Menu)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); onClose(); } };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <Menu anchor={anchor} onClose={onClose} minWidth={240} maxWidth={420} maxHeight={360}>
      {/* Список скроллится внутри карточки, а «Настроить» остаётся видимым внизу */}
      <div style={{ maxHeight: 260, overflowY: 'auto' }}>
        {phrases.map(p => (
          <MenuItem
            key={p}
            label={p}
            onClick={() => { onPick(p); onClose(); }}
          />
        ))}
        {phrases.length === 0 && (
          <div style={{
            padding: '10px 10px 12px', fontFamily: FONT.sans, fontSize: FS.sm,
            color: C.textMuted, lineHeight: 1.45,
          }}>
            {failed
              ? 'Не удалось загрузить фразы — нет связи с сервером.'
              : loaded
                ? 'Фраз пока нет. Заведите те, что шлёте чаще всего, — они будут уходить одним нажатием.'
                : 'Загружаем…'}
          </div>
        )}
      </div>
      <MenuSep />
      <MenuItem
        icon={<Settings2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        label="Настроить фразы"
        onClick={() => { onClose(); onEdit(); }}
      />
    </Menu>
  );
}

// Иконка кнопки — общая точка: попап и кнопка в композере должны читаться как одно
export function QuickPhrasesIcon() {
  return <MessageSquareText size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
}

export function QuickPhrasesDialog({ onClose }: { onClose: () => void }) {
  const saved = useQuickPhrases();
  // Черновик правится локально и уезжает на сервер целиком по «Сохранить»:
  // построчный PUT на каждый символ бил бы по users.json без нужды
  const [draft, setDraft] = useState<string[]>(() => (saved.length > 0 ? [...saved] : ['']));
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const setAt = (i: number, v: string) => setDraft(d => d.map((p, idx) => (idx === i ? v : p)));
  const removeAt = (i: number) => setDraft(d => (d.length > 1 ? d.filter((_, idx) => idx !== i) : ['']));
  const add = () => setDraft(d => [...d, '']);

  const filled = draft.map(p => p.trim()).filter(Boolean);
  const full = draft.length >= QUICK_PHRASE_MAX_COUNT;

  const handleSave = async () => {
    setLoading(true);
    setError(null);
    try {
      // Пустые строки, дубли и потолок вычищает сервер — его итог и станет набором
      await saveQuickPhrases(filled);
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить фразы');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Быстрые фразы"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={loading ? 'Сохраняем…' : 'Сохранить'}
          onConfirm={handleSave}
          loading={loading}
          onCancel={onClose}
        />
      }
    >
      <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 }}>
        Фраза из списка уходит в чат одним нажатием — без правки в поле ввода.
        Набор личный и работает во всех чатах.
      </div>

      <Field label={`Фразы (${filled.length} из ${QUICK_PHRASE_MAX_COUNT})`}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          {draft.map((p, i) => (
            <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <TextField
                  value={p}
                  onChange={v => setAt(i, v.slice(0, QUICK_PHRASE_MAX_LENGTH))}
                  placeholder="Например: продолжай"
                  onEnter={handleSave}
                />
              </div>
              <IconButton
                size="md"
                tone="danger"
                title="Удалить фразу"
                onClick={() => removeAt(i)}
              >
                <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              </IconButton>
            </div>
          ))}
        </div>
      </Field>

      <div>
        <Button variant="secondary" size="sm" onClick={add} disabled={full}>
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
            <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            Добавить фразу
          </span>
        </Button>
        {full && (
          <div style={{ marginTop: 6, fontSize: 11.5, color: C.textMuted, borderRadius: R.sm }}>
            Больше {QUICK_PHRASE_MAX_COUNT} фраз в списке не держим — попап перестаёт быть быстрым.
          </div>
        )}
      </div>

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}
