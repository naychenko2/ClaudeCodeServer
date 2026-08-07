import { useEffect, useRef } from 'react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { C } from '../../lib/design'
import { XTERM_BASE_OPTIONS } from '../../lib/xtermTheme'
import { onMessage, joinPreviewLog, leavePreviewLog } from '../../lib/signalr'

// Вывод дев-серверов. Read-only двойник TerminalView: тот же xterm (Vite и dotnet
// печатают ANSI-цвета — в <pre> они превратились бы в мусор вида [32m), но без
// stdin и без проброса resize на сервер — процессом мы не управляем, только смотрим.
//
// Сервисов может быть несколько (бэк + фронт): тогда строки идут вперемешку с цветным
// префиксом [Имя], как в docker compose. Буфер каждого живёт на сервере и приходит
// ответом на подписку, поэтому переключение вкладок и reconnect не теряют историю.

// ANSI-цвета префиксов по порядку сервисов. Сырой hex тут неуместен — это коды
// терминала, их раскрашивает тема xterm.
const PREFIX_COLORS = ['\x1b[36m', '\x1b[35m', '\x1b[32m', '\x1b[33m', '\x1b[34m', '\x1b[31m']
const RESET = '\x1b[0m'

export interface LogSource {
  id: string
  name: string
}

// Приписывает префикс каждой строке чанка. Чанк всегда заканчивается переводом
// строки (сервер шлёт строки целиком), поэтому хвостовой пустой элемент отбрасываем.
function withPrefix(chunk: string, prefix: string): string {
  const lines = chunk.split('\n')
  const tail = lines.pop()   // '' для завершённого чанка
  const out = lines.map(l => prefix + l).join('\n')
  return tail ? out + '\n' + prefix + tail : out + '\n'
}

export function PreviewLogView({ projectId, sources }: { projectId: string; sources: LogSource[] }) {
  const hostRef = useRef<HTMLDivElement>(null)
  // Стабильный ключ состава: пересоздавать терминал нужно при смене НАБОРА сервисов,
  // а не при каждом обновлении их статусов (объекты приходят новые каждый опрос)
  const key = sources.map(s => s.id).join('|')

  useEffect(() => {
    const host = hostRef.current
    if (!host || sources.length === 0) return
    let disposed = false

    const term = new Terminal({ ...XTERM_BASE_OPTIONS, disableStdin: true, cursorStyle: 'underline' })
    const fit = new FitAddon()
    term.loadAddon(fit)
    term.open(host)
    const initial = setTimeout(() => { if (!disposed) fit.fit() }, 50)

    const multi = sources.length > 1
    const prefixOf = new Map(sources.map((s, i) => [
      s.id,
      multi ? `${PREFIX_COLORS[i % PREFIX_COLORS.length]}[${s.name}]${RESET} ` : '',
    ]))
    const write = (serviceId: string, data: string) => {
      const prefix = prefixOf.get(serviceId)
      term.write(prefix ? withPrefix(data, prefix) : data)
    }

    // Живые строки могут прийти раньше ответа с накопленным буфером (в группу сервер
    // добавляет ДО того, как вернёт снимок) — тогда без очереди хвост лога оказался бы
    // выше его начала. Копим до буфера, потом сливаем в порядке появления.
    const ready = new Set<string>()
    const pending: { id: string; data: string }[] = []

    const unsub = onMessage(msg => {
      if (disposed || msg.type !== 'preview_log') return
      if (!prefixOf.has(msg.serviceId)) return
      if (ready.has(msg.serviceId)) write(msg.serviceId, msg.data)
      else pending.push({ id: msg.serviceId, data: msg.data })
    })

    sources.forEach(src => {
      joinPreviewLog(projectId, src.id)
        .then(buffered => {
          if (disposed) return
          if (buffered) write(src.id, buffered)
          ready.add(src.id)
          // Сливаем накопленное только по этому сервису — у остальных свой буфер в пути
          for (let i = pending.length - 1; i >= 0; i--) {
            if (pending[i].id !== src.id) continue
            write(src.id, pending[i].data)
            pending.splice(i, 1)
          }
        })
        .catch(() => {
          if (!disposed) term.writeln(`\x1b[90m[Не удалось подписаться на вывод «${src.name}»]${RESET}`)
        })
    })

    const observer = new ResizeObserver(() => { if (!disposed) fit.fit() })
    observer.observe(host)

    return () => {
      disposed = true
      clearTimeout(initial)
      observer.disconnect()
      unsub()
      sources.forEach(src => {
        leavePreviewLog(projectId, src.id).catch(() => { /* соединение уже закрыто */ })
      })
      term.dispose()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, key])

  return <div ref={hostRef} style={{ flex: 1, minHeight: 0, overflow: 'hidden', background: C.termBg, padding: 4 }} />
}
