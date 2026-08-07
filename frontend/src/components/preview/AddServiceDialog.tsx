import { useState } from 'react'
import { C, R, FONT, MODAL_W } from '../../lib/design'
import { Modal, Button, TextField } from '../ui'
import { api } from '../../lib/api'
import type { LaunchConfigEntry } from '../../types'

// Свой запуск: дописывает конфигурацию в .claude/launch.json проекта. Раньше это была
// форма-вкладыш прямо в списке сервисов — из сырых input и кнопок мимо дизайн-системы.
interface Props {
  projectId: string
  onClose: () => void
  onSaved: () => void
}

export function AddServiceDialog({ projectId, onClose, onSaved }: Props) {
  const [name, setName] = useState('')
  const [command, setCommand] = useState('npm')
  const [args, setArgs] = useState('run dev')
  const [port, setPort] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const portInvalid = port.trim().length > 0 && !/^\d{1,5}$/.test(port.trim())
  const canSave = command.trim().length > 0 && !portInvalid

  const save = async () => {
    if (!canSave) return
    setSaving(true)
    setError(null)
    try {
      const cur = await api.projects.getLaunchConfig(projectId)
      const entry: LaunchConfigEntry = {
        name: name.trim() || command.trim(),
        runtimeExecutable: command.trim(),
        runtimeArgs: args.split(' ').filter(Boolean),
        port: port.trim() ? Number(port) : undefined,
      }
      await api.projects.putLaunchConfig(projectId, [...cur.configurations, entry])
      onSaved()
      onClose()
    } catch (e) {
      // Раньше ошибка сохранения глоталась молча, и форма просто «не срабатывала»
      setError(e instanceof Error ? e.message : 'Не удалось сохранить конфигурацию')
      setSaving(false)
    }
  }

  const footer = (
    <div style={{ display: 'flex', gap: 8, marginLeft: 'auto' }}>
      <Button variant="ghost" disabled={saving} onClick={onClose}>Отмена</Button>
      <Button variant="primary" loading={saving} disabled={saving || !canSave} onClick={() => void save()}>
        Сохранить
      </Button>
    </div>
  )

  return (
    <Modal
      width={MODAL_W.form}
      title="Свой запуск"
      subtitle="Сохранится в .claude/launch.json проекта"
      onClose={onClose}
      footer={footer}
    >
      {error && (
        <div style={{
          padding: '10px 12px', background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          borderRadius: R.lg, fontSize: 12.5, color: C.dangerText,
        }}>{error}</div>
      )}

      <label style={labelStyle}>Название</label>
      <TextField value={name} onChange={setName} autoFocus placeholder="Фронтенд (необязательно)" />

      <label style={labelStyle}>Команда</label>
      <TextField value={command} onChange={setCommand} mono placeholder="npm" />

      <label style={labelStyle}>Аргументы</label>
      <TextField value={args} onChange={setArgs} mono placeholder="run dev" />

      <label style={labelStyle}>Порт</label>
      <TextField value={port} onChange={setPort} mono placeholder="5173 (необязательно)" />
      <div style={{ fontSize: 11.5, color: portInvalid ? C.dangerText : C.textMuted, marginTop: -4 }}>
        {portInvalid
          ? 'Порт — это число'
          : 'Пусто — порт поймаем из вывода сервиса при старте'}
      </div>
    </Modal>
  )
}

const labelStyle: React.CSSProperties = {
  fontSize: 12, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans,
}
