import { useState } from 'react';

// Инлайн-редактирование текста записи по клику: открыть карточку → textarea →
// Enter/✓ сохранить, Esc/✕ отменить. Общий паттерн для карточек памяти
// (персоны и команды проекта) — держим его в одном месте, а не дублируем стейт-машину.
export function useInlineEdit(onSave: (id: string, text: string) => Promise<unknown>) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const start = (id: string, initial: string) => { setEditingId(id); setText(initial); setError(null); };
  const cancel = () => { setEditingId(null); setError(null); };

  const save = async () => {
    const trimmed = text.trim();
    if (!editingId || !trimmed || saving) return;
    setSaving(true);
    try {
      await onSave(editingId, trimmed);
      setEditingId(null);
      setError(null);
    } catch (e) {
      // Ошибку (напр. 400 «длиннее 1000 символов») показываем прямо в карточке —
      // редактирование остаётся открытым, набранный текст не пропадает.
      setError(e instanceof Error ? e.message : 'Не удалось сохранить изменения');
    } finally {
      setSaving(false);
    }
  };

  return { editingId, text, setText, saving, error, start, cancel, save };
}
