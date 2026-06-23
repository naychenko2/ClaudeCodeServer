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

  // maskable — padding ~9%, фон bgMain #F4F0E8
  const maskableSize = 512;
  const innerSize = Math.round(maskableSize * 0.82);
  const padding = Math.round((maskableSize - innerSize) / 2);

  const innerBuf = await sharp(svgBuffer)
    .resize(innerSize, innerSize)
    .png()
    .toBuffer();

  await sharp({
    create: {
      width: maskableSize,
      height: maskableSize,
      channels: 4,
      background: { r: 0xF4, g: 0xF0, b: 0xE8, alpha: 1 }
    }
  })
    .composite([{ input: innerBuf, top: padding, left: padding }])
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
