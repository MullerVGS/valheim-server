// Historico do mapa, dia a dia. O servidor so guarda BACKUPS_MAX_AGE dias de zip; aqui o ultimo zip de
// cada dia e copiado para ARCHIVE_DIR/saves (fica para sempre) e vira um retrato do mapa naquele dia:
// explorado, pins das mesas e construcoes. Roda no container do site, lendo copias: nada toca o jogo.
import { copyFile, mkdir, readFile, readdir, rename, stat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { gunzipSync, gzipSync } from 'node:zlib';
import { buildPieces, parseCatalog, readWorld, unzip } from './saves.mjs';

const ZIP = /^worlds-(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})\.zip$/;
// Muda quando o retrato muda de formato: dias ja arquivados sao refeitos do zip guardado.
const SNAPSHOT_VERSION = 1;

async function exists(path) {
  try {
    await stat(path);
    return true;
  } catch {
    return false;
  }
}

async function writeAtomic(path, data) {
  await writeFile(path + '.tmp', data);
  await rename(path + '.tmp', path);
}

export class Archive {
  constructor({ backupsDir, archiveDir, mapDir, world }) {
    this.backupsDir = backupsDir;
    this.archiveDir = archiveDir;
    this.mapDir = mapDir;
    this.world = world;
    this.days = [];
  }

  get savesDir() { return join(this.archiveDir, 'saves'); }
  get daysDir() { return join(this.archiveDir, 'days'); }

  // Ultimo zip de cada dia, entre os backups do servidor e os ja guardados aqui.
  async sources() {
    const best = new Map();
    const consider = (dir, name) => {
      const m = name.match(ZIP);
      if (!m) return;
      const date = `${m[1]}-${m[2]}-${m[3]}`;
      const prev = best.get(date);
      if (!prev || name > prev.name) best.set(date, { date, name, path: join(dir, name), time: `${m[4]}:${m[5]}` });
    };
    for (const name of await readdir(this.backupsDir).catch(() => [])) consider(this.backupsDir, name);
    for (const name of await readdir(this.savesDir).catch(() => [])) consider(this.savesDir, name);
    return [...best.values()].sort((a, b) => a.date.localeCompare(b.date));
  }

  async catalog() {
    try {
      return parseCatalog(await readFile(join(this.mapDir, 'pieces-catalog.bin')));
    } catch {
      return null;
    }
  }

  async run() {
    await mkdir(this.savesDir, { recursive: true });
    await mkdir(this.daysDir, { recursive: true });
    const catalog = await this.catalog();
    for (const src of await this.sources()) {
      const kept = join(this.savesDir, src.name);
      if (src.path !== kept && !(await exists(kept))) {
        await copyFile(src.path, kept + '.tmp');
        await rename(kept + '.tmp', kept);
      }
      const metaPath = join(this.daysDir, `${src.date}.json`);
      const meta = JSON.parse(await readFile(metaPath, 'utf8').catch(() => 'null'));
      const upToDate = meta && meta.source === src.name && meta.version === SNAPSHOT_VERSION && (meta.pieces !== null || !catalog);
      if (upToDate) continue;
      const t0 = performance.now();
      try {
        const prefix = `worlds_local/${this.world}/`;
        const files = unzip(await readFile(kept), (n) => n.includes(prefix));
        const world = readWorld(files, catalog);
        const pieces = catalog ? buildPieces(world.candidates, catalog, world.explored) : null;
        let pixels = 0;
        for (const b of world.explored) pixels += b;
        await writeAtomic(join(this.daysDir, `${src.date}.explored.gz`), gzipSync(world.explored));
        if (pieces) await writeAtomic(join(this.daysDir, `${src.date}.pieces.gz`), gzipSync(pieces));
        const next = {
          version: SNAPSHOT_VERSION,
          date: src.date,
          time: src.time,
          source: src.name,
          tables: world.tables,
          exploredKm2: (pixels * 144) / 1e6,
          pieces: pieces ? pieces.readUInt32LE(4) : null,
          pins: world.pins,
        };
        await writeAtomic(metaPath, JSON.stringify(next));
        console.log(`historico: ${src.date} de ${src.name}: ${next.exploredKm2.toFixed(1)} km2, ${next.pins.length} pins, ` +
          `${next.pieces ?? 'sem'} construcoes (${Math.round(performance.now() - t0)} ms)`);
      } catch (err) {
        console.error(`historico: ${src.name}:`, err.message);
      }
      // Um zip por volta do event loop: o site segue respondendo durante o preenchimento.
      await new Promise((r) => setImmediate(r));
    }
    this.days = await this.list();
  }

  async list() {
    const days = [];
    for (const name of await readdir(this.daysDir).catch(() => [])) {
      if (!name.endsWith('.json')) continue;
      const meta = JSON.parse(await readFile(join(this.daysDir, name), 'utf8'));
      days.push({ date: meta.date, time: meta.time, exploredKm2: meta.exploredKm2, pieces: meta.pieces, pins: meta.pins.length });
    }
    return days.sort((a, b) => b.date.localeCompare(a.date));
  }

  has(date) {
    return this.days.some((d) => d.date === date);
  }

  async explored(date) {
    return new Uint8Array(gunzipSync(await readFile(join(this.daysDir, `${date}.explored.gz`))));
  }

  async piecesGz(date) {
    return readFile(join(this.daysDir, `${date}.pieces.gz`)).catch(() => null);
  }

  async pins(date) {
    return JSON.parse(await readFile(join(this.daysDir, `${date}.json`), 'utf8')).pins;
  }
}
