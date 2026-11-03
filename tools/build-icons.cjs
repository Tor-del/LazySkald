// Optional asset regeneration: node tools/build-icons.cjs [path-to-sharp]
const fs = require('node:fs');
const path = require('node:path');
const sharp = require(process.argv[2] || 'sharp');
const directory = path.resolve(__dirname, '../src/ScreenReader.App/Assets');
(async () => {
  const source = fs.readFileSync(path.join(directory, 'storyteller.svg'));
  const sizes = [16, 24, 32, 48, 64, 128, 256];
  const pngs = [];
  for (const size of sizes) pngs.push(await sharp(source).resize(size, size).png().toBuffer());
  const header = Buffer.alloc(6 + sizes.length * 16);
  header.writeUInt16LE(1, 2); header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  sizes.forEach((size, index) => {
    const at = 6 + index * 16;
    header[at] = size === 256 ? 0 : size; header[at + 1] = header[at];
    header.writeUInt16LE(1, at + 4); header.writeUInt16LE(32, at + 6);
    header.writeUInt32LE(pngs[index].length, at + 8); header.writeUInt32LE(offset, at + 12);
    offset += pngs[index].length;
  });
  fs.writeFileSync(path.join(directory, 'storyteller.ico'), Buffer.concat([header, ...pngs]));
  fs.writeFileSync(path.join(directory, 'storyteller.png'), pngs[pngs.length - 1]);
  process.stdout.write(`Built storyteller icon at ${sizes.join(', ')} px\n`);
})().catch(error => { process.stderr.write(String(error)); process.exitCode = 1; });
