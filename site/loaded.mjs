// Plugin metrics to layer state, including compatibility with the older single-center plugin.
export function loadedState(refPos, simZones, simClassic, loader, instances, characters, anchors = []) {
  const value = (r) => (r ? Number(r.value[1]) : null);
  const axis = (a) => refPos.find((r) => r.metric.axis === a);
  const part = (p) => simZones.find((r) => r.metric.part === p);
  const kind = (k) => characters.find((r) => r.metric.kind === k);
  if (!axis('x') || !axis('z') || !part('near') || !part('far') || !simClassic.length) return null;
  const x = value(axis('x')), z = value(axis('z'));
  const active = loader.length ? value(loader[0]) === 1 : null;
  const byId = new Map();
  if (active) {
    for (const r of anchors) {
      const { anchor, axis: a } = r.metric;
      if (!anchor || (a !== 'x' && a !== 'z')) continue;
      if (!byId.has(anchor)) byId.set(anchor, { id: anchor, x: null, z: null });
      byId.get(anchor)[a] = value(r);
    }
  }
  const centers = [...byId.values()].filter((p) => p.x !== null && p.z !== null && Number.isFinite(p.x) && Number.isFinite(p.z))
    .sort((a, b) => a.id.localeCompare(b.id));
  return {
    x, z,
    anchors: centers.length ? centers : [{ id: null, x, z }],
    near: value(part('near')),
    far: value(part('far')),
    classic: value(simClassic[0]) === 1,
    loader: active,
    instances: value(instances[0]),
    wild: value(kind('wild')),
    tamed: value(kind('tamed')),
  };
}
