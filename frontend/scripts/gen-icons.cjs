// Скрипт генерации PWA-иконок из favicon.svg через sharp
// Запуск: node scripts/gen-icons.cjs (из папки frontend/)

const sharp = require('sharp');
const fs = require('fs');
const path = require('path');

const svgPath = path.resolve(__dirname, '../public/favicon.svg');
const outDir = path.resolve(__dirname, '../public');

const svgBuffer = fs.readFileSync(svgPath);

async function run() {
  const sizes = [
    { name: 'pwa-64x64.png', size: 64 },
    { name: 'pwa-192x192.png', size: 192 },
    { name: 'pwa-512x512.png', size: 512 },
    { name: 'apple-touch-icon-180x180.png', size: 180 },
  ];

  for (const { name, size } of sizes) {
    const out = path.join(outDir, name);
    await sharp(svgBuffer)
      .resize(size, size)
      .png()
      .toFile(out);
    console.log(`Generated: ${name} (${size}x${size})`);
  }

  // maskable — новый значок с собственным градиентным фоном, рисуем full-bleed
  const maskableSize = 512;
  await sharp(svgBuffer)
    .resize(maskableSize, maskableSize)
    .png()
    .toFile(path.join(outDir, 'maskable-icon-512x512.png'));
  console.log('Generated: maskable-icon-512x512.png');

  // favicon.ico — 32x32 PNG (браузеры принимают PNG-файл с расширением .ico)
  const ico32Buf = await sharp(svgBuffer)
    .resize(32, 32)
    .png()
    .toBuffer();
  fs.writeFileSync(path.join(outDir, 'favicon.ico'), ico32Buf);
  console.log('Generated: favicon.ico (32x32)');

  console.log('\nДонэ! Все иконки сгенерированы.');
}

run().catch(err => { console.error(err); process.exit(1); });
