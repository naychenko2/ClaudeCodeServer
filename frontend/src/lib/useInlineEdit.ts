import { useState } from 'react';

// Инлайн-редактирование текста записи по клику: открыть карточку → textarea →
// Enter/✓ сохранить, Esc/✕ отменить. Общий паттерн для карточек памяти
// (персоны и команды проекта) — держим его в одном месте, а не дублируем стейт-машину.
export function useInlineEdit(onSave: (id: string, text: string) => Promise<unknown>) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [saving, setSaving] = useState(false);

  const start = (id: string, initial: string) => { setEditingId(id); setText(initial); };
  const cancel = () => setEditingId(null);

  const save = async () => {
    const trimmed = text.trim();
    if (!editingId || !trimmed || saving) return;
    setSaving(true);
    try {
      await onSave(editingId, trimmed);
      setEditingId(null);
    } finally {
      setSaving(false);
    }
  };

  return { editingId, text, setText, saving, start, cancel, save };
}
