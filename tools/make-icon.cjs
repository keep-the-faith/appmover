// Renders the AppMover icon SVGs into a multi-size Windows .ico (see build-icon.sh).
// Usage: node make-icon.cjs <large.svg> <small.svg> <out.ico> [png preview dir]
const fs = require("fs");
const path = require("path");
const { Resvg } = require("@resvg/resvg-js");

const [large, small, output, previewDir] = process.argv.slice(2);
// Up to 24 px the simplified drawing is used; 256 px is stored as PNG, the others as 32-bit bitmaps.
const sizes = [16, 20, 24, 32, 40, 48, 64, 256];
const SMALL_MAX = 24;

function render(file, size) {
  return new Resvg(fs.readFileSync(file, "utf8"), { fitTo: { mode: "width", value: size } }).render();
}

/** BITMAPINFOHEADER + bottom-up BGRA pixels (straight alpha) + empty AND mask, as an .ico entry expects. */
function bitmap(image) {
  const { width, height } = image;
  const rgba = image.pixels;
  const maskStride = Math.ceil(width / 32) * 4;
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0);
  header.writeInt32LE(width, 4);
  header.writeInt32LE(height * 2, 8); // colour + mask
  header.writeUInt16LE(1, 12);
  header.writeUInt16LE(32, 14);
  header.writeUInt32LE(width * height * 4 + maskStride * height, 20);
  const pixels = Buffer.alloc(width * height * 4);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const src = (y * width + x) * 4;
      const dst = ((height - 1 - y) * width + x) * 4;
      const a = rgba[src + 3];
      const straight = (c) => (a === 0 ? 0 : Math.min(255, Math.round((c * 255) / a)));
      pixels[dst] = straight(rgba[src + 2]);
      pixels[dst + 1] = straight(rgba[src + 1]);
      pixels[dst + 2] = straight(rgba[src]);
      pixels[dst + 3] = a;
    }
  }
  return Buffer.concat([header, pixels, Buffer.alloc(maskStride * height)]);
}

const entries = sizes.map((size) => {
  const image = render(size <= SMALL_MAX ? small : large, size);
  if (previewDir) {
    fs.mkdirSync(previewDir, { recursive: true });
    fs.writeFileSync(path.join(previewDir, `appmover-${size}.png`), image.asPng());
  }
  return { size, data: size >= 256 ? image.asPng() : bitmap(image) };
});

const directory = Buffer.alloc(6 + 16 * entries.length);
directory.writeUInt16LE(0, 0);
directory.writeUInt16LE(1, 2); // icon
directory.writeUInt16LE(entries.length, 4);
let offset = directory.length;
entries.forEach(({ size, data }, i) => {
  const at = 6 + 16 * i;
  directory.writeUInt8(size >= 256 ? 0 : size, at); // 0 means 256
  directory.writeUInt8(size >= 256 ? 0 : size, at + 1);
  directory.writeUInt16LE(1, at + 4); // planes
  directory.writeUInt16LE(32, at + 6); // bits per pixel
  directory.writeUInt32LE(data.length, at + 8);
  directory.writeUInt32LE(offset, at + 12);
  offset += data.length;
});
fs.writeFileSync(output, Buffer.concat([directory, ...entries.map((e) => e.data)]));
console.log(`${output}: ${sizes.join(", ")} px`);
