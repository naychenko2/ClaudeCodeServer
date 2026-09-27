// Кадры трубы ретранслятора: [канал:1][длина:4 BE][данные]
import { EventEmitter } from 'node:events';

export const CH = { STDIN: 0, STDOUT: 1, STDERR: 2, EXIT: 3, STDIN_EOF: 4, INFO: 5, CONTROL: 9 };

export function frame(ch, data) {
  const buf = Buffer.isBuffer(data) ? data : Buffer.from(data ?? '');
  const head = Buffer.alloc(5);
  head[0] = ch;
  head.writeUInt32BE(buf.length, 1);
  return Buffer.concat([head, buf]);
}

export class FrameReader extends EventEmitter {
  #buf = Buffer.alloc(0);
  push(chunk) {
    this.#buf = Buffer.concat([this.#buf, chunk]);
    while (this.#buf.length >= 5) {
      const len = this.#buf.readUInt32BE(1);
      if (this.#buf.length < 5 + len) break;
      const ch = this.#buf[0];
      const data = this.#buf.subarray(5, 5 + len);
      this.#buf = this.#buf.subarray(5 + len);
      this.emit('frame', ch, data);
    }
  }
}
