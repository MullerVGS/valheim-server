// Dados do mapa servidos ao navegador. O terreno do mundo inteiro fica so no servidor e sai recortado
// pelo que as mesas de cartografia mostram. Fonte preferida: os arquivos que o plugin grava em
// MAP_DIR (terrain.bin, explored.bin, pieces.bin; formatos em plugin/src/Map/MapFiles.cs). Sem eles,
// terreno de DATA_DIR/terrain-full.bin e explorado lido do save (SAVE_DIR), sem construcoes.
import { createHash } from 'node:crypto';
import { readFile, readdir, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { gzipSync, inflateRawSync, constants } from 'node:zlib';

const HEADER = 16;
const GZIP = Buffer.from([0x1f, 0x8b, 0x08]);
// Folga ao redor do explorado para a borda da nevoa interpolar com terreno de verdade.
const MARGIN = 2;

async function statOrNull(path) {
  try {
    const s = await stat(path);
    return s.isFile() ? s : null;
  } catch {
    return null;
  }
}

const sig = (s) => (s ? `${s.size}:${Math.floor(s.mtimeMs)}` : '-');

function loadTerrain(buf, path) {
  if (buf.toString('latin1', 0, 4) !== 'VHM1') throw new Error(`${path}: formato desconhecido`);
  const size = buf.readUInt32LE(4);
  return { buf, size, n: size * size };
}

// Assinatura barata da pasta do mundo: muda quando o autosave regrava algum arquivo.
async function saveSignature(dir) {
  const parts = [];
  for (const name of (await readdir(dir)).sort()) parts.push(`${name}:${sig(await statOrNull(join(dir, name)))}`);
  return parts.join('|');
}

// Uniao do explorado de todas as mesas do save: o campo `data` da mesa e um ZPackage gzip de
// Minimap.GetSharedMapData (int versao 3, int n, um byte por pixel). So acumula em `into`: arquivo
// lido no meio de uma escrita pode perder uma mesa nesta volta, mas o explorado nunca encolhe.
async function readExploredFromSave(dir, into) {
  const n = into.length;
  for (const name of await readdir(dir)) {
    const path = join(dir, name);
    if (!(await statOrNull(path))) continue;
    const data = await readFile(path);
    for (let i = data.indexOf(GZIP); i !== -1; i = data.indexOf(GZIP, i + 1)) {
      if (data[i + 3] !== 0) continue; // sem campos opcionais: cabecalho de 10 bytes
      let raw;
      try {
        raw = inflateRawSync(data.subarray(i + 10), { finishFlush: constants.Z_SYNC_FLUSH });
      } catch {
        continue;
      }
      if (raw.length < 8 + n || raw.readInt32LE(0) !== 3 || raw.readInt32LE(4) !== n) continue;
      for (let k = 0; k < n; k++) if (raw[8 + k]) into[k] = 1;
    }
  }
}

// Copia do terreno com tudo zerado fora do explorado (+ folga) e a camada de explorado preenchida.
export function maskTerrain(full, explored) {
  const { buf, size, n } = full;
  const keep = new Uint8Array(explored);
  for (let pass = 0; pass < MARGIN; pass++) {
    const prev = keep.slice();
    for (let i = 0; i < size; i++) {
      for (let j = 0; j < size; j++) {
        const k = i * size + j;
        if (prev[k]) continue;
        if ((j > 0 && prev[k - 1]) || (j < size - 1 && prev[k + 1]) || (i > 0 && prev[k - size]) || (i < size - 1 && prev[k + size]))
          keep[k] = 1;
      }
    }
  }
  const out = Buffer.alloc(buf.length);
  buf.copy(out, 0, 0, 12); // o 4o campo do cabecalho e a seed: fica no servidor
  const bytes = HEADER + n * 2;
  for (let k = 0; k < n; k++) {
    if (!keep[k]) continue;
    out[HEADER + k * 2] = buf[HEADER + k * 2];
    out[HEADER + k * 2 + 1] = buf[HEADER + k * 2 + 1];
    out[bytes + k] = buf[bytes + k]; // bioma
    out[bytes + n + k] = buf[bytes + n + k]; // floresta
    out[bytes + 2 * n + k] = buf[bytes + 2 * n + k]; // bruma
  }
  let pixels = 0;
  for (let k = 0; k < n; k++) {
    out[bytes + 3 * n + k] = explored[k] ? 255 : 0;
    pixels += explored[k] ? 1 : 0;
  }
  return { ...packed(out), pixels };
}

function packed(raw) {
  const gz = gzipSync(raw, { level: 6 });
  return { gz, etag: `"${createHash('sha1').update(gz).digest('hex').slice(0, 16)}"` };
}

export class MapData {
  constructor({ mapDir, dataDir, saveDir }) {
    this.mapDir = mapDir;
    this.dataDir = dataDir;
    this.saveDir = saveDir;
    this.terrain = null; // { gz, etag, pixels } recortado
    this.pieces = null; // { gz, etag, count }
    this.full = null;
    this.fullSig = null;
    this.exploredSig = null;
    this.piecesSig = null;
    this.saveExplored = null;
  }

  // Refaz so o que mudou no disco. Chamado a cada poucos segundos: so stat, fora a reconstrucao.
  async refresh() {
    const t0 = performance.now();
    const pluginTerrain = this.mapDir && join(this.mapDir, 'terrain.bin');
    const terrainPath = (pluginTerrain && (await statOrNull(pluginTerrain))) ? pluginTerrain : join(this.dataDir, 'terrain-full.bin');
    const terrainStat = await statOrNull(terrainPath);
    if (!terrainStat) return;
    const fullSig = `${terrainPath}:${sig(terrainStat)}`;
    let rebuild = false;
    if (fullSig !== this.fullSig) {
      this.full = loadTerrain(await readFile(terrainPath), terrainPath);
      this.fullSig = fullSig;
      this.saveExplored = null;
      rebuild = true;
    }

    const exploredPath = this.mapDir && join(this.mapDir, 'explored.bin');
    const exploredStat = exploredPath && (await statOrNull(exploredPath));
    let explored = null;
    let exploredSig;
    if (exploredStat) {
      exploredSig = `plugin:${sig(exploredStat)}`;
      if (rebuild || exploredSig !== this.exploredSig) {
        explored = new Uint8Array(await readFile(exploredPath));
        if (explored.length !== this.full.n) throw new Error(`explored.bin com ${explored.length} bytes`);
      }
    } else if (this.saveDir) {
      exploredSig = `save:${await saveSignature(this.saveDir)}`;
      if (rebuild || exploredSig !== this.exploredSig) {
        this.saveExplored ??= new Uint8Array(this.full.n);
        await readExploredFromSave(this.saveDir, this.saveExplored);
        explored = this.saveExplored;
      }
    }
    if (explored) {
      this.terrain = maskTerrain(this.full, explored);
      this.exploredSig = exploredSig;
      console.log(`terreno: ${exploredSig.split(':')[0]}, ${this.terrain.pixels} px explorados, ` +
        `${(this.terrain.gz.length / 1024).toFixed(0)} KiB em ${(performance.now() - t0).toFixed(0)} ms`);
    }

    const piecesPath = this.mapDir && join(this.mapDir, 'pieces.bin');
    const piecesStat = piecesPath && (await statOrNull(piecesPath));
    if (piecesStat && sig(piecesStat) !== this.piecesSig) {
      const raw = await readFile(piecesPath);
      if (raw.toString('latin1', 0, 4) !== 'VPC1') throw new Error('pieces.bin: formato desconhecido');
      this.pieces = { ...packed(raw), count: raw.readUInt32LE(4) };
      this.piecesSig = sig(piecesStat);
      console.log(`construcoes: ${this.pieces.count} pecas, ${(this.pieces.gz.length / 1024).toFixed(0)} KiB`);
    }
  }
}
