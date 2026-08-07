import { useState } from 'react';
import { Modal, ModalActions, TextField } from './ui';
import { api, setStoredToken } from '../lib/api';
import { C, MODAL_W } from '../lib/design';

interface Props {
  onClose: () => void;
}

export function ChangePasswordDialog({ onClose }: Props) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const validate = (): string | null => {
    if (!current.trim()) return 'Введите текущий пароль';
    if (next.length < 8) return 'Новый пароль — не менее 8 символов';
    if (next !== confirm) return 'Пароли не совпадают';
    return null;
  };

  const handleSave = async () => {
    const validationError = validate();
    if (validationError) { setError(validationError); return; }
    setError('');
    setLoading(true);
    try {
      // Прежний токен сервер только что отозвал — без подмены следующий же запрос
      // вернул бы 401 и выкинул на логин то самое устройство, с которого меняли пароль
      const { token } = await api.auth.changePassword(current, next);
      setStoredToken(token);
      onClose();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка смены пароля');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Сменить пароль"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Сохранить"
          onConfirm={handleSave}
          onCancel={onClose}
          loading={loading}
        />
      }
    >
      {error && (
        <div style={{ color: C.danger, fontSize: 13, marginBottom: -4 }}>{error}</div>
      )}
      <TextField
        type="password"
        value={current}
        onChange={setCurrent}
        placeholder="Текущий пароль"
        autoFocus
      />
      <TextField
        type="password"
        value={next}
        onChange={setNext}
        placeholder="Новый пароль (не менее 8 символов)"
      />
      <TextField
        type="password"
        value={confirm}
        onChange={setConfirm}
        placeholder="Подтвердите новый пароль"
        onEnter={handleSave}
      />
    </Modal>
  );
}
