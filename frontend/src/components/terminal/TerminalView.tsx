import { useEffect, useRef, useCallback } from 'react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { C, FONT } from '../../lib/design'
import { sendTerminalInput, resizeTerminal, onTerminalMessage, connectTerminal } from '../../lib/terminalSignalr'
import { WaitingIndicator } from '../ui'

interface Props {
  terminalId: string
}

export function TerminalView({ terminalId }: Props) {
  const termRef = useRef<HTMLDivElement>(null)
  const xtermRef = useRef<Terminal | null>(null)
  const fitAddonRef = useRef<FitAddon | null>(null)
  const runningRef = useRef(false)
  const disposedRef = useRef(false)

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
      cursorBlink: true,
      cursorStyle: 'bar',
      fontSize: 13,
      fontFamily: FONT.mono,
      theme: {
        background: C.termBg as string,
        foreground: C.termText as string,
        cursor: C.accent as string,
        selectionBackground: C.accentMuted as string,
        black: '#2e2e2e', red: '#cc6666', green: '#93c97d', yellow: '#e0c080',
        blue: '#7fa6d6', magenta: '#c397d8', cyan: '#70c0b1', white: '#d0d0d0',
        brightBlack: '#555555', brightRed: '#d97757', brightGreen: '#b8d7a3',
        brightYellow: '#f0dfaf', brightBlue: '#a0b9d8', brightMagenta: '#d4a8d9',
        brightCyan: '#8ed0c4', brightWhite: '#e8e8e8',
      },
      allowTransparency: false,
      cols: 80, rows: 24, scrollback: 5000,
    })

    const fitAddon = new FitAddon()
    term.loadAddon(fitAddon)
    term.open(termRef.current)
    fitAddonRef.current = fitAddon
    xtermRef.current = term

    setTimeout(() => { if (!disposedRef.current) handleResize() }, 50)

    term.onData((data) => {
      sendTerminalInput(terminalId, data)
    })

    connectTerminal(terminalId).then(t => {
      if (t) runningRef.current = true
    })

    const unsub = onTerminalMessage((msg) => {
      if (disposedRef.current) return
      if (msg.type === 'terminal_output' && msg.data && msg.terminalId === terminalId) {
        term.write(msg.data)
      } else if (msg.type === 'terminal_status' && msg.terminalId === terminalId) {
        if (msg.status === 'stopped') {
          runningRef.current = false
          term.writeln(`\r\n\x1b[90m[Process exited with code ${msg.exitCode ?? '?'}]\x1b[0m`)
        }
      }
    })

    return () => {
      disposedRef.current = true
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

  return (
    <div ref={termRef} style={{ flex: 1, minHeight: 0, overflow: 'hidden', background: C.termBg, padding: 4 }} />
  )
}
