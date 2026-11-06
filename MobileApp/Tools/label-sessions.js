// Etichetta le sessioni di prova con un set di monete noto e ne ricava dati di addestramento.
//
// Uso: node label-sessions.js <cartella-uscita> <cartella-sessione> [<cartella-sessione> ...]
//
// Per ogni sessione:
//  1. toglie i doppioni del rilevatore (due riquadri con i centri a meno di mezzo diametro nello stesso fotogramma);
//  2. se le monete restanti sono quante quelle del set, assegna a ognuna un valore del set con un'assegnazione ottimale
//     (algoritmo ungherese) che combina punteggi del classificatore e del rilevatore, colore (rame/oro) e diametro relativo;
//  3. scrive, per ogni fotogramma chiave di cui c'è l'immagine di debug, l'immagine e il JSON delle etichette
//     (MachineLearning/schema/coin_label.schema.json) e, per ogni moneta, il ritaglio in crops/<valore>/.
// Le sessioni con un numero di monete diverso dal set si saltano: non si può sapere quale moneta è in più.
const fs = require('fs');
const path = require('path');

const SET = { 1: 19, 2: 6, 5: 2, 10: 1, 20: 1, 50: 1, 200: 1 };       // il set di prova (31 monete)
const DIAMETER = { 1: 16.25, 2: 18.75, 5: 21.25, 10: 19.75, 20: 22.25, 50: 24.25, 100: 23.25, 200: 25.75 };
const COPPER = new Set([1, 2, 5]);
const SAME_COIN = 0.5;

const [out, ...sessions] = process.argv.slice(2);
if (!out || sessions.length === 0) { console.error('uso: node label-sessions.js <uscita> <sessione> ...'); process.exit(1); }

function readJsonl(file) { return fs.readFileSync(file, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l)); }
const centre = b => [b[0] + b[2] / 2, b[1] + b[3] / 2];
const diam = b => (b[2] + b[3]) / 2;

// algoritmo ungherese su matrice quadrata di costi
function hungarian(cost) {
  const n = cost.length, u = Array(n + 1).fill(0), v = Array(n + 1).fill(0), p = Array(n + 1).fill(0), way = Array(n + 1).fill(0);
  for (let i = 1; i <= n; i++) {
    p[0] = i; let j0 = 0; const minv = Array(n + 1).fill(Infinity), used = Array(n + 1).fill(false);
    do {
      used[j0] = true; const i0 = p[j0]; let delta = Infinity, j1 = 0;
      for (let j = 1; j <= n; j++) if (!used[j]) {
        const cur = cost[i0 - 1][j - 1] - u[i0] - v[j];
        if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
        if (minv[j] < delta) { delta = minv[j]; j1 = j; }
      }
      for (let j = 0; j <= n; j++) if (used[j]) { u[p[j]] += delta; v[j] -= delta; } else minv[j] -= delta;
      j0 = j1;
    } while (p[j0] !== 0);
    do { const j1 = way[j0]; p[j0] = p[j1]; j0 = j1; } while (j0);
  }
  const result = Array(n); for (let j = 1; j <= n; j++) result[p[j] - 1] = j - 1; return result;
}

const summary = [];
for (const dir of sessions) {
  const name = path.basename(dir);
  const s = JSON.parse(fs.readFileSync(path.join(dir, 'session.json'), 'utf8').replace(/^﻿/, ''));
  const ev = readJsonl(path.join(dir, 'diagnostics.jsonl'));

  // 1) doppioni: tracce viste solo tramite riquadri soppressi
  const keptBoxes = [], keptTracks = new Set(), allTracks = new Set();
  for (const e of ev.filter(e => e.e === 'detect')) {
    const ok = e.d.boxes.filter(b => b.use === 'ok' || b.use === 'same_coin');
    const order = ok.map((b, i) => i).sort((a, b) => ok[b].conf - ok[a].conf), kept = [];
    for (const i of order) {
      const b = ok[i], c = centre(b.box);
      if (kept.some(k => { const o = ok[k], oc = centre(o.box); return Math.hypot(oc[0] - c[0], oc[1] - c[1]) < SAME_COIN * (diam(o.box) + diam(b.box)) / 2; })) continue;
      kept.push(i);
    }
    for (const b of ok) if (b.track != null) allTracks.add(b.track);
    for (const i of kept) { const b = ok[i]; if (b.track != null) keptTracks.add(b.track); keptBoxes.push({ keyframe: e.d.keyframe, ...b }); }
  }
  const coins = s.coins.filter(c => keptTracks.has(c.number));
  const total = Object.values(SET).reduce((a, b) => a + b, 0);
  if (coins.length !== total) { summary.push(`${name}: ${coins.length} monete dopo i doppioni (il set ne ha ${total}): saltata`); continue; }

  // 2) assegnazione dei valori del set
  const slots = Object.entries(SET).flatMap(([v, n]) => Array(n).fill(+v));
  const scoreOf = c => {
    const scores = {};
    for (const h of c.history || []) for (const part of (h.topK || '').split(';')) { const [v, sc] = part.split(':'); if (v) scores[+v] = Math.max(scores[+v] || 0, +sc); }
    if (c.detector?.cents != null) scores[c.detector.cents] = Math.max(scores[c.detector.cents] || 0, c.detector.confidence || 0);
    return scores;
  };
  const MARGIN = 1.5;                       // differenza minima di costo con il secondo valore possibile per dirsi sicuri
  let scale = null, assignment = null, costOf = null;
  for (let round = 0; round < 3; round++) {
    costOf = (c, v) => {
      const sc = scoreOf(c), tint = c.color?.tint;
      {
        let k = -Math.log(Math.max(sc[v] || 0, 0.01));
        if (tint === 'copper' && !COPPER.has(v)) k += 3;
        if (tint === 'gold' && COPPER.has(v)) k += 3;
        if (scale) { const err = (c.diameterPx / scale - DIAMETER[v]) / (0.07 * DIAMETER[v]); k += err * err / 2; }
        return k;
      }
    };
    const cost = coins.map(c => slots.map(v => costOf(c, v)));
    const a = hungarian(cost);
    assignment = a.map(j => slots[j]);
    const ratios = coins.map((c, i) => c.diameterPx / DIAMETER[assignment[i]]).sort((x, y) => x - y);
    scale = ratios[Math.floor(ratios.length / 2)];
  }
  // le monete confermate a mano nell'app valgono più di ogni stima
  coins.forEach((c, i) => { if (c.truth?.verified && c.effectiveCents != null) assignment[i] = c.effectiveCents; });
  const label = new Map(coins.map((c, i) => [c.number, assignment[i]]));
  // sicura se ogni altro valore costa almeno MARGIN in più (con la scala finale)
  const sure = new Set(coins.filter((c, i) => c.truth?.verified || Object.keys(SET).map(Number).filter(v => v !== assignment[i]).every(v => costOf(c, v) - costOf(c, assignment[i]) >= MARGIN)).map(c => c.number));
  const changed = coins.filter((c, i) => c.effectiveCents !== assignment[i]).map(c => `#${c.number} ${c.effectiveCents}→${label.get(c.number)}`);

  // 3) scrittura: ritagli e fotogrammi chiave
  for (const c of coins) {
    if (!c.crop) continue;
    const dst = path.join(out, sure.has(c.number) ? 'crops' : path.join('crops', '_incerte'), String(label.get(c.number)));
    fs.mkdirSync(dst, { recursive: true });
    fs.copyFileSync(path.join(dir, c.crop), path.join(dst, `${name}_${String(c.number).padStart(3, '0')}.jpg`));
  }
  const frames = fs.existsSync(path.join(dir, 'frames', 'frames.jsonl')) ? readJsonl(path.join(dir, 'frames', 'frames.jsonl')) : [];
  const byTs = new Map(frames.map(f => [Math.floor(f.TimestampNs / 1e6), f]));   // nel registro ts = ns / 1 000 000 (troncato)
  const keyframeTs = {}; for (const e of ev) if (e.e === 'frame' && e.d.decision === 'keyframe') keyframeTs[Object.keys(keyframeTs).length] = e.d;
  let images = 0;
  for (const e of ev.filter(e => e.e === 'detect')) {
    const kf = keyframeTs[e.d.keyframe], f = kf ? byTs.get(kf.ts) : null;
    if (!f) continue;
    const boxes = keptBoxes.filter(b => b.keyframe === e.d.keyframe);
    const border = e.d.boxes.filter(b => b.use === 'border');
    const id = `${name}_kf${e.d.keyframe}`;
    const dstDir = path.join(out, 'images'); fs.mkdirSync(dstDir, { recursive: true });
    fs.copyFileSync(path.join(dir, 'frames', f.File), path.join(dstDir, id + '.jpg'));
    fs.writeFileSync(path.join(dstDir, id + '.json'), JSON.stringify({
      image: id + '.jpg', width: kf.w, height: kf.h, source: 'own-phone',
      // auto: tutte le monete dell'immagine hanno un'etichetta sicura; review: alcune vanno controllate a mano
      status: border.length === 0 && boxes.every(b => sure.has(b.track)) ? 'auto' : 'review',
      labeler: { boxes: 'detector:offline-model', classifiers: ['set-assignment'], reviewed: false },
      coins: [
        ...boxes.map(b => ({ label: sure.has(b.track) ? label.get(b.track) : null, bbox: b.box.map(x => Math.round(x * 10) / 10), confidence: b.conf })),
        ...border.map(b => ({ label: null, bbox: b.box.map(x => Math.round(x * 10) / 10), confidence: b.conf }))
      ]
    }, null, 1));
    images++;
  }
  summary.push(`${name}: etichettata, ${coins.length} monete (${sure.size} sicure), ${images} fotogrammi; valori diversi da quelli dell'app: ${changed.length} (${changed.join(', ')})`);
  fs.writeFileSync(path.join(out, `${name}.labels.json`), JSON.stringify(Object.fromEntries([...label].map(([k, v]) => [k, { label: v, sure: sure.has(k) }])), null, 1));
}
console.log(summary.join('\n'));
