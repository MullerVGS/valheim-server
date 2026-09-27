// Terreno servido ao navegador: o mundo inteiro fica so no servidor (terrain-full.bin, gerado uma
// vez: a seed nao muda) e sai recortado pelo que as mesas de cartografia mostram, lido do save.
// Nada disso toca o processo do jogo: o save e o arquivo que o proprio autosave grava.
import { createHash } from 'node:crypto';
import { readFile, readdir, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { gzipSync, inflateRawSync, constants } from 'node:zlib';

const HEADER = 16;
const GZIP = Buffer.from([0x1f, 0x8b, 0x08]);
// Folga ao redor do explorado para a borda da nevoa interpolar com terreno de verdade.
const MARGIN = 2;

// Formato em tools/build_terrain.py.
export async function loadFullTerrain(path) {
  const buf = await readFile(path);
  if (buf.toString('latin1', 0, 4) !== 'VHM1') throw new Error(`${path}: formato desconhecido`);
  const size = buf.readUInt32LE(4);
  return { buf, size, n: size * size };
}

// Assinatura barata da pasta do mundo: muda quando o autosave regrava algum arquivo.
export async function saveSignature(dir) {
  const names = (await readdir(dir)).sort();
  const parts = [];
  for (const name of names) {
    const s = await stat(join(dir, name));
    if (s.isFile()) parts.push(`${name}:${s.size}:${Math.floor(s.mtimeMs)}`);
  }
  return parts.join('|');
}

// Uniao do explorado de todas as mesas do save. O campo `data` da mesa e um ZPackage gzip de
// Minimap.GetSharedMapData: int versao (3), int n e um byte por pixel. Procura todo gzip com esse
// cabecalho, sem depender do layout do ZDO. So acumula em `into`: arquivo lido no meio de uma
// escrita pode perder uma mesa nesta volta, mas o explorado nunca encolhe.
export async function readExplored(dir, into) {
  const n = into.length;
  let tables = 0;
  for (const name of await readdir(dir)) {
    const path = join(dir, name);
    if (!(await stat(path)).isFile()) continue;
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
      tables++;
      for (let k = 0; k < n; k++) if (raw[8 + k]) into[k] = 1;
    }
  }
  return tables;
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
  buf.copy(out, 0, 0, HEADER);
  const height = HEADER;
  const bytes = height + n * 2;
  for (let k = 0; k < n; k++) {
    if (!keep[k]) continue;
    out[height + k * 2] = buf[height + k * 2];
    out[height + k * 2 + 1] = buf[height + k * 2 + 1];
    out[bytes + k] = buf[bytes + k]; // bioma
    out[bytes + n + k] = buf[bytes + n + k]; // floresta
    out[bytes + 2 * n + k] = buf[bytes + 2 * n + k]; // bruma
  }
  for (let k = 0; k < n; k++) out[bytes + 3 * n + k] = explored[k] ? 255 : 0;
  const gz = gzipSync(out, { level: 6 });
  let pixels = 0;
  for (let k = 0; k < n; k++) pixels += explored[k];
  return { gz, etag: `"${createHash('sha1').update(gz).digest('hex').slice(0, 16)}"`, pixels };
}
