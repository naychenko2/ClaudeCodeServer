import { useEffect, useRef, useCallback } from 'react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { C } from '../../lib/design'
import { XTERM_BASE_OPTIONS } from '../../lib/xtermTheme'
import { sendTerminalInput, resizeTerminal, onTerminalMessage, connectTerminal } from '../../lib/terminalSignalr'

interface Props {
  terminalId: string
  onActivity?: (busy: boolean) => void
  // Терминал виден (активная вкладка). Скрытые остаются смонтированными и копят вывод;
  // при показе нужен refit — в display:none xterm меряет нулевой размер.
  visible?: boolean
}

export function TerminalView({ terminalId, onActivity, visible = true }: Props) {
  const termRef = useRef<HTMLDivElement>(null)
  const xtermRef = useRef<Terminal | null>(null)
  const fitAddonRef = useRef<FitAddon | null>(null)
  const disposedRef = useRef(false)
  const idleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  // onActivity — через ref: смена колбэка не должна пересоздавать xterm (иначе экран
  // чернеет и теряется ввод/вывод). xterm живёт ровно на один terminalId.
  const onActivityRef = useRef(onActivity)
  useEffect(() => { onActivityRef.current = onActivity })

  const handleResize = useCallback(() => {
    const fit = fitAddonRef.current
    if (!fit) return
    fit.fit()
    const dims = fit.proposeDimensions()
    if (dims) resizeTerminal(terminalId, dims.cols, dims.rows)
  }, [terminalId])

  useEffect(() => {
    if (!termRef.current) return
    disposedRef.current = false

    const term = new Terminal({
      ...XTERM_BASE_OPTIONS,
      cursorBlink: true,
      cursorStyle: 'bar',
    })

    const fitAddon = new FitAddon()
    term.loadAddon(fitAddon)
    term.open(termRef.current)
    fitAddonRef.current = fitAddon
    xtermRef.current = term

    // Busy detection: user sent a command (any data with newline)
    const markBusy = () => {
      onActivityRef.current?.(true)
      if (idleTimerRef.current) clearTimeout(idleTimerRef.current)
    }

    // Busy detection: output received → schedule idle after 400ms pause
    const markOutput = () => {
      if (idleTimerRef.current) clearTimeout(idleTimerRef.current)
      idleTimerRef.current = setTimeout(() => onActivityRef.current?.(false), 400)
    }

    setTimeout(() => { if (!disposedRef.current) handleResize() }, 50)

    term.onData((data) => {
      sendTerminalInput(terminalId, data)
      // Newline = command submitted → busy
      if (data.includes('\n') || data.includes('\r')) markBusy()
    })

    connectTerminal(terminalId).then(t => {
      if (t) { onActivityRef.current?.(false); markOutput() }
    })

    const unsub = onTerminalMessage((msg) => {
      if (disposedRef.current) return
      if (msg.type === 'terminal_output' && msg.data && msg.terminalId === terminalId) {
        term.write(msg.data)
        markOutput()
      } else if (msg.type === 'terminal_status' && msg.terminalId === terminalId) {
        if (msg.status === 'stopped') {
          onActivityRef.current?.(false)
          term.writeln(`\r\n\x1b[90m[Process exited with code ${msg.exitCode ?? '?'}]\x1b[0m`)
        }
      }
    })

    return () => {
      disposedRef.current = true
      if (idleTimerRef.current) clearTimeout(idleTimerRef.current)
      unsub()
      term.dispose()
      xtermRef.current = null
      fitAddonRef.current = null
    }
  }, [terminalId, handleResize])

  useEffect(() => {
    const el = termRef.current
    if (!el) return
    const observer = new ResizeObserver(() => handleResize())
    observer.observe(el)
    return () => observer.disconnect()
  }, [handleResize])

  // Стал видимым (переключились на эту вкладку терминала) — пересчитать размер и
  // перерисовать из буфера xterm (в скрытом состоянии fit меряет нулевой размер).
  useEffect(() => {
    if (!visible) return
    const id = setTimeout(() => {
      if (disposedRef.current) return
      handleResize()
      xtermRef.current?.refresh(0, (xtermRef.current.rows || 1) - 1)
    }, 0)
    return () => clearTimeout(id)
  }, [visible, handleResize])

  return (
    <div ref={termRef} style={{ flex: 1, minHeight: 0, overflow: 'hidden', background: C.termBg, padding: 4 }} />
  )
}
