// Area que o proprio servidor mantem carregada em volta do ponto de referencia dele (a placa
// "chunkloader"; sem ela o dedicado fica estacionado fora do mundo e nao carrega nada).
// Porte das regras do jogo 1.0.14, sem DOM:
// - zona: ZoneSystem.GetZone / GetZonePos (64 m, centro em id * 64);
// - terreno e objetos: ZDOMan.FindSectorObjects (aneis ate near) e ZoneSystem.CreateLocalZones;
// - objetos distantes: o resto de FindSectorObjects (aneis ate near + far, so prefabs "distant");
// - simulado (posse do servidor): ZNetScene.PointInsideActiveArea dentro do que foi carregado.

export const ZONE = 64;

export const zoneOf = (x, z) => [Math.floor((x + 32) / ZONE), Math.floor((z + 32) / ZONE)];

// ZoneSystem.ZonesWithinRadius: distancia entre centros < raio em zonas + meia zona (0,8 se fantasma).
function within(a, b, radius, ghost) {
  const r = radius * ZONE + (ghost ? ZONE * 0.8 : ZONE * 0.5);
  const dx = (a[0] - b[0]) * ZONE;
  const dz = (a[1] - b[1]) * ZONE;
  return dx * dx + dz * dz < r * r;
}

function ring(center, k) {
  const out = [];
  const [cx, cz] = center;
  for (let x = cx - k; x <= cx + k; x++) out.push([x, cz - k], [x, cz + k]);
  for (let z = cz - k + 1; z <= cz + k - 1; z++) out.push([cx - k, z], [cx + k, z]);
  return out;
}

const key = ([x, z]) => `${x},${z}`;

// { center, near: [[zx, zz]], distant: [[zx, zz]], active: { half, radius } } para o ponto e a distancia
// de simulacao do servidor. `active.radius` so existe no recorte redondo (near 2 fora do classico).
export function loadedArea(x, z, sim) {
  const center = zoneOf(x, z);
  const { near, far, classic } = sim;
  const seen = new Set([key(center)]);
  const nearZones = [center];
  for (let i = 1; i <= near; i++) {
    for (const c of ring(center, i)) {
      if (!classic && !within(center, c, near, false)) continue;
      if (seen.has(key(c))) continue;
      seen.add(key(c));
      nearZones.push(c);
    }
  }
  const distant = [];
  const total = near + far;
  for (let k = classic ? 1 + near : 1; k <= total; k++) {
    for (const c of ring(center, k)) {
      if (!classic && !within(center, c, total, true)) continue;
      if (seen.has(key(c))) continue;
      seen.add(key(c));
      distant.push(c);
    }
  }
  const active = { half: (near === 1 ? 1 : 1.5) * ZONE };
  if (near === 2 && !classic) active.radius = ZONE * 1.75;
  return { center, near: nearZones, distant, active };
}

// O jogo dono do objeto nesse ponto, quando nenhum jogador esta por perto.
export function bandAt(area, x, z) {
  const zone = key(zoneOf(x, z));
  const inNear = area.near.some((c) => key(c) === zone);
  if (inNear && inActive(area, x, z)) return 'active';
  if (inNear) return 'near';
  if (area.distant.some((c) => key(c) === zone)) return 'distant';
  return null;
}

function inActive(area, x, z) {
  const cx = area.center[0] * ZONE;
  const cz = area.center[1] * ZONE;
  if (Math.max(Math.abs(x - cx), Math.abs(z - cz)) > area.active.half) return false;
  const r = area.active.radius;
  return r == null || (x - cx) ** 2 + (z - cz) ** 2 < r * r;
}

// Lados de zona que separam o conjunto do resto: contorno exato da uniao, em metros de mundo.
export function outline(zones) {
  const set = new Set(zones.map(key));
  const edges = [];
  for (const [zx, zz] of zones) {
    const x0 = zx * ZONE - ZONE / 2;
    const z0 = zz * ZONE - ZONE / 2;
    const x1 = x0 + ZONE;
    const z1 = z0 + ZONE;
    if (!set.has(key([zx, zz - 1]))) edges.push([x0, z0, x1, z0]);
    if (!set.has(key([zx, zz + 1]))) edges.push([x0, z1, x1, z1]);
    if (!set.has(key([zx - 1, zz]))) edges.push([x0, z0, x0, z1]);
    if (!set.has(key([zx + 1, zz]))) edges.push([x1, z0, x1, z1]);
  }
  return edges;
}

// All areas, deduplicating zones with near taking precedence over distant in overlaps.
export function loadedAreas(state) {
  const anchors = state.anchors ?? [{ x: state.x, z: state.z }];
  const areas = anchors.map((anchor) => ({ ...loadedArea(anchor.x, anchor.z, state), anchor }));
  const near = new Map(), distant = new Map();
  for (const area of areas) {
    for (const zone of area.near) near.set(key(zone), zone);
    for (const zone of area.distant) distant.set(key(zone), zone);
  }
  for (const k of near.keys()) distant.delete(k);
  return { areas, near: [...near.values()], distant: [...distant.values()] };
}

// The strongest band wins; tooltips identify the anchor covering the selected location.
export function loadedHitAt(coverage, x, z) {
  let best = null;
  const rank = { distant: 1, near: 2, active: 3 };
  for (const area of coverage.areas) {
    const band = bandAt(area, x, z);
    if (band && (!best || rank[band] > rank[best.band])) best = { area, band };
  }
  return best;
}
