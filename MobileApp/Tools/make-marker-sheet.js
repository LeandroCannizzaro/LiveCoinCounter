// Genera il foglio dei marker di scala da stampare: ArUco 4x4 (DICT_4X4_50 di OpenCV), quadrato nero di 20 mm di lato,
// con un margine bianco di 5 mm. Uso: node make-marker-sheet.js <uscita.pdf>
// I bit dei marker vengono da OpenCV 4.12 (modules/objdetect/src/aruco/predefined_dictionaries.hpp, DICT_4X4_1000_BYTES):
// per ogni marker i primi 2 byte sono la rotazione 0, letti riga per riga dal bit più alto (1 = bianco), dentro una cornice nera.
const fs = require('fs');


// I primi 48 marker di DICT_4X4_50 (rotazione 0): 2 byte ciascuno.
const BYTES = JSON.parse(fs.readFileSync(__dirname + '/aruco4x4_50_bytes.json', 'utf8'));

const MM = 72 / 25.4;
const PAGE_W = 210, PAGE_H = 297;
const MARKER = 20, TILE = 30, COLS = 6, ROWS = 8, COUNT = COLS * ROWS;
const GRID_X = (PAGE_W - COLS * TILE) / 2, GRID_Y = 45;

const f = n => (n * MM).toFixed(3);
const y = mm => ((PAGE_H - mm) * MM).toFixed(3);
const esc = s => s.replace(/\\/g, '\\\\').replace(/\(/g, '\\(').replace(/\)/g, '\\)');

const out = [];
const text = (font, size, xMm, yMm, s) => out.push(`BT /${font} ${size} Tf ${f(xMm)} ${y(yMm)} Td (${esc(s)}) Tj ET`);

// intestazione
text('F2', 16, 15, 16, 'Marker di scala per Conteggio Monete');
text('F1', 9, 15, 23, 'Stampa al 100% (senza «adatta alla pagina»): la barra qui sotto deve misurare esattamente 100 mm.');
text('F1', 9, 15, 28, 'Ogni marker ha il quadrato nero di 20 mm di lato (ArUco 4x4, ID da 0 a 47). Ritaglia lungo le linee grigie.');
text('F1', 9, 15, 33, 'Appoggia alcuni marker piatti tra le monete e negli spazi vuoti. Non coprire il margine bianco: serve al riconoscimento.');

// barra di verifica da 100 mm
out.push('0 g 1.2 w');
out.push(`${f(15)} ${y(40)} m ${f(115)} ${y(40)} l S`);
for (const x of [15, 65, 115]) out.push(`${f(x)} ${y(38.5)} m ${f(x)} ${y(41.5)} l S`);
text('F1', 8, 118, 41.3, '100 mm');

// marker
for (let i = 0; i < COUNT; i++) {
  const col = i % COLS, row = Math.floor(i / COLS);
  const tx = GRID_X + col * TILE, ty = GRID_Y + row * TILE;

  // guida di taglio
  out.push('0.65 G 0.3 w');
  out.push(`${f(tx)} ${y(ty + TILE)} ${f(TILE)} ${f(TILE)} re S`);

  // quadrato nero 20x20 e, sopra, le celle bianche (1 = bianco)
  const mx = tx + (TILE - MARKER) / 2, my = ty + (TILE - MARKER) / 2 - 1.5;
  out.push('0 g');
  out.push(`${f(mx)} ${y(my + MARKER)} ${f(MARKER)} ${f(MARKER)} re f`);
  const cell = MARKER / 6;
  const [b0, b1] = BYTES[i];
  const bits = [];
  for (const b of [b0, b1]) for (let k = 7; k >= 0; k--) bits.push((b >> k) & 1);
  out.push('1 g');
  for (let r = 0; r < 4; r++)
    for (let c = 0; c < 4; c++)
      if (bits[r * 4 + c]) out.push(`${f(mx + (c + 1) * cell)} ${y(my + (r + 2) * cell)} ${f(cell)} ${f(cell)} re f`);

  out.push('0 g');
  text('F1', 6, tx + 2, ty + TILE - 1.5, `ID ${i}`);
}

const content = Buffer.from(out.join('\n'), 'latin1');
const objects = [];
objects.push('<< /Type /Catalog /Pages 2 0 R >>');
objects.push('<< /Type /Pages /Kids [3 0 R] /Count 1 >>');
objects.push(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${(PAGE_W * MM).toFixed(2)} ${(PAGE_H * MM).toFixed(2)}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>`);
objects.push({ stream: content });
objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>');
objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>');

const parts = [Buffer.from('%PDF-1.4\n', 'latin1')];
const offsets = [];
let pos = parts[0].length;
objects.forEach((o, i) => {
  offsets.push(pos);
  const body = typeof o === 'string'
    ? Buffer.from(`${i + 1} 0 obj\n${o}\nendobj\n`, 'latin1')
    : Buffer.concat([Buffer.from(`${i + 1} 0 obj\n<< /Length ${o.stream.length} >>\nstream\n`, 'latin1'), o.stream, Buffer.from('\nendstream\nendobj\n', 'latin1')]);
  parts.push(body);
  pos += body.length;
});
let xref = `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
for (const o of offsets) xref += `${String(o).padStart(10, '0')} 00000 n \n`;
xref += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${pos}\n%%EOF\n`;
parts.push(Buffer.from(xref, 'latin1'));
fs.writeFileSync(process.argv[2], Buffer.concat(parts));
console.log('scritto', process.argv[2], 'marker:', COUNT);
