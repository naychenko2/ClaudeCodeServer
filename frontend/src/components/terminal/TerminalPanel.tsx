import { useEffect, useRef, useCallback } from 'react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { C, FONT } from '../../lib/design'
import { startTerminal, stopTerminal, sendTerminalInput, resizeTerminal, onTerminalMessage } from '../../lib/terminalSignalr'

interface Props {
  projectId: string
}

export function TerminalPanel({ projectId }: Props) {
  const termRef = useRef<HTMLDivElement>(null)
  const xtermRef = useRef<Terminal | null>(null)
  const fitAddonRef = useRef<FitAddon | null>(null)
  const runningRef = useRef(false)
  const disposedRef = useRef(false)

  const handleResize = useCallback(() => {
    const fit = fitAddonRef.current
    if (!fit) return
    fit.fit()
    const { cols, rows } = fit.proposeDimensions() ?? { cols: 80, rows: 24 }
    resizeTerminal(projectId, cols, rows).catch(() => {})
  }, [projectId])

  useEffect(() => {
    if (!termRef.current) return
    disposedRef.current = false

    const term = new Terminal({
      cursorBlink: true,
      cursorStyle: 'bar',
      fontSize: 13,
      fontFamily: FONT.mono,
      theme: {
        background: C.termBg as string,
        foreground: C.termText as string,
        cursor: C.accent as string,
        selectionBackground: C.accentMuted as string,
        black: '#2e2e2e',
        red: '#cc6666',
        green: '#93c97d',
        yellow: '#e0c080',
        blue: '#7fa6d6',
        magenta: '#c397d8',
        cyan: '#70c0b1',
        white: '#d0d0d0',
        brightBlack: '#555555',
        brightRed: '#d97757',
        brightGreen: '#b8d7a3',
        brightYellow: '#f0dfaf',
        brightBlue: '#a0b9d8',
        brightMagenta: '#d4a8d9',
        brightCyan: '#8ed0c4',
        brightWhite: '#e8e8e8',
      },
      allowTransparency: false,
      cols: 80,
      rows: 24,
      scrollback: 5000,
    })

    const fitAddon = new FitAddon()
    term.loadAddon(fitAddon)
    term.open(termRef.current)

    // Fit после открытия (нужен DOM-размер)
    setTimeout(() => {
      if (!disposedRef.current) handleResize()
    }, 50)

    xtermRef.current = term
    fitAddonRef.current = fitAddon

    // Ввод пользователя → сервер
    term.onData((data) => {
      if (runningRef.current) {
        sendTerminalInput(projectId, data).catch(() => {})
      }
    })

    // Запуск терминала
    const dims = fitAddon.proposeDimensions() ?? { cols: 80, rows: 24 }
    startTerminal(projectId, dims.cols, dims.rows)
      .then(() => { runningRef.current = true })
      .catch(() => {
        term.writeln('\r\n\x1b[31mОшибка подключения терминала\x1b[0m')
      })

    // Подписка на сообщения сервера
    const unsub = onTerminalMessage((msg: { type: string; data?: string; isError?: boolean; status?: string; exitCode?: number }) => {
      if (disposedRef.current) return
      if (msg.type === 'terminal_output' && msg.data) {
        term.write(msg.data)
      } else if (msg.type === 'terminal_status') {
        if (msg.status === 'stopped') {
          runningRef.current = false
          term.writeln(`\r\n\x1b[90m[Process exited with code ${msg.exitCode ?? '?'}]\x1b[0m`)
        }
      }
    })

    return () => {
      disposedRef.current = true
      unsub()
      stopTerminal(projectId).catch(() => {})
      term.dispose()
      xtermRef.current = null
      fitAddonRef.current = null
    }
  }, [projectId, handleResize])

  // ResizeObserver
  useEffect(() => {
    const el = termRef.current
    if (!el) return
    const observer = new ResizeObserver(() => {
      handleResize()
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [handleResize])

  return (
    <div
      ref={termRef}
      style={{
        flex: 1,
        minHeight: 0,
        overflow: 'hidden',
        background: C.termBg,
        padding: 4,
      }}
    />
  )
}
