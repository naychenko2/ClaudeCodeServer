// Ретранслятор stdio удалённого хода (ADR-016, задача 2.3): его запускает RemoteProcessRunner
// вместо claude, и для ClaudeSession он — обычный System.Diagnostics.Process (как docker exec -i
// у песочницы). stdin/stdout/stderr и код выхода — это stdio CLI на устройстве; сам процесс
// ходит только на loopback-порт раннера в процессе бэкенда, а тот — в канал устройства.
//
//   node exec-relay.mjs <порт>        ключ подключения — в env CCS_EXEC_RELAY_KEY
//
// Кадры — как у канала исполнения: [канал:1][номер:8 BE][длина:4 BE][данные], номер здесь 0.
import net from 'node:net';

const HEADER = 13;
const CH = { STDIN: 0, STDOUT: 1, STDERR: 2, EXIT: 3, STDIN_EOF: 4, CONTROL: 9 };

function frame(ch, data = Buffer.alloc(0)) {
  const b = Buffer.alloc(HEADER + data.length);
  b[0] = ch;
  b.writeBigUInt64BE(0n, 1);
  b.writeUInt32BE(data.length, 9);
  data.copy(b, HEADER);
  return b;
}

const port = Number(process.argv[2]);
const key = process.env.CCS_EXEC_RELAY_KEY ?? '';
let exitCode = 1;
let pending = Buffer.alloc(0);
let finished = false;

const sock = net.connect(port, '127.0.0.1', () => {
  sock.write(frame(CH.CONTROL, Buffer.from(key, 'utf8')));
  process.stdin.on('data', d => { if (sock.writable) sock.write(frame(CH.STDIN, d)); });
  process.stdin.on('end', () => { if (sock.writable) sock.write(frame(CH.STDIN_EOF)); });
});

sock.on('data', d => {
  pending = pending.length ? Buffer.concat([pending, d]) : d;
  while (pending.length >= HEADER) {
    const len = pending.readUInt32BE(9);
    if (pending.length < HEADER + len) break;
    const ch = pending[0];
    const data = pending.subarray(HEADER, HEADER + len);
    pending = pending.subarray(HEADER + len);
    if (ch === CH.STDOUT) process.stdout.write(data);
    else if (ch === CH.STDERR) process.stderr.write(data);
    else if (ch === CH.EXIT) {
      try {
        const e = JSON.parse(data.toString('utf8'));
        exitCode = e.code ?? (e.signal ? 137 : 1);
      } catch { exitCode = 1; }
    }
  }
});

function finish() {
  if (finished) return;
  finished = true;
  process.stdout.write('', () => process.stderr.write('', () => process.exit(exitCode)));
}
sock.on('close', finish);
sock.on('error', finish);
