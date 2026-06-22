// base64 → байты для Blob/ArrayBuffer (документы, изображения, скачивание).
// Явный ArrayBuffer (не ArrayBufferLike) — иначе Uint8Array не присваивается BlobPart в TS 6
export function base64ToBytes(base64: string): Uint8Array<ArrayBuffer> {
  const bin = atob(base64);
  const bytes = new Uint8Array(new ArrayBuffer(bin.length));
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
}
