// Спайк ADR-016: серверная сторона ретранслятора — то, что RemoteProcessRunner запускал бы
// вместо claude. Его stdin/stdout/stderr/exit-код — это stdio удалённого CLI, поэтому
// ClaudeSession держал бы его обычным System.Diagnostics.Process (как docker exec -i).
//
//   node relay-client.mjs spawn <spec.json>   — запуск хода; spec: { turnId, args, turnToken, mcpConfig? }
//   node relay-client.mjs kill <turnId>       — убийство хода по TurnId отдельным подключением
import net from 'node:net';
import fs from 'node:fs';
import { FrameReader, frame, CH } from './frames.mjs';

const PORT = +(process.env.RELAY_PORT ?? 18711);
const [op, arg] = process.argv.slice(2);
const sock = net.connect(PORT, '127.0.0.1');
const reader = new FrameReader();
sock.on('data', d => reader.push(d));

if (op === 'kill') {
  sock.write(frame(CH.CONTROL, JSON.stringify({ op: 'kill', turnId: arg })));
  reader.on('frame', (ch, data) => { if (ch === CH.INFO) { process.stdout.write(data + '\n'); process.exit(0); } });
} else {
  const spec = JSON.parse(fs.readFileSync(arg, 'utf8'));
  sock.write(frame(CH.CONTROL, JSON.stringify({ op: 'spawn', ...spec })));
  process.stdin.on('data', d => sock.write(frame(CH.STDIN, d)));
  process.stdin.on('end', () => sock.write(frame(CH.STDIN_EOF)));
  let exitCode = 1;
  reader.on('frame', (ch, data) => {
    if (ch === CH.STDOUT) process.stdout.write(data);
    else if (ch === CH.STDERR) process.stderr.write(data);
    else if (ch === CH.INFO && process.env.RELAY_INFO_FILE) fs.writeFileSync(process.env.RELAY_INFO_FILE, data);
    else if (ch === CH.EXIT) {
      const { code, signal } = JSON.parse(data);
      exitCode = code ?? (signal ? 137 : 1);
    }
  });
  sock.on('close', () => process.stdout.end(() => process.exit(exitCode)));
}
