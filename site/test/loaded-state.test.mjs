import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadedState } from '../loaded.mjs';

const row = (metric, value) => ({ metric, value: [0, String(value)] });
const ref = [row({ axis: 'x' }, 0), row({ axis: 'z' }, 0)];
const sim = [row({ part: 'near' }, 2), row({ part: 'far' }, 2)];
const classic = [row({}, 1)];
const active = [row({}, 1)];
const anchors = [row({ anchor: 'b', axis: 'z' }, -1024), row({ anchor: 'a', axis: 'x' }, 0),
  row({ anchor: 'b', axis: 'x' }, 2048), row({ anchor: 'a', axis: 'z' }, 0)];

test('each sign metric becomes a center and counts remain server totals', () => {
  const s = loadedState(ref, sim, classic, active, [row({}, 100)], [row({ kind: 'tamed' }, 12)], anchors);
  assert.deepEqual(s.anchors, [{ id: 'a', x: 0, z: 0 }, { id: 'b', x: 2048, z: -1024 }]);
  assert.equal(s.instances, 100);
  assert.equal(s.tamed, 12);
});

test('older plugins use the primary point; missing simulation metrics disable drawing', () => {
  assert.deepEqual(loadedState(ref, sim, classic, active, [], []).anchors, [{ id: null, x: 0, z: 0 }]);
  assert.equal(loadedState(ref, [], classic, active, [], [], anchors), null);
});

test('removed signs disappear and disabled loaders ignore stale series', () => {
  const s = loadedState(ref, sim, classic, active, [], [], anchors.filter((r) => r.metric.anchor !== 'a'));
  assert.deepEqual(s.anchors, [{ id: 'b', x: 2048, z: -1024 }]);
  const off = loadedState(ref, sim, classic, [row({}, 0)], [], [], anchors);
  assert.deepEqual(off.anchors, [{ id: null, x: 0, z: 0 }]);
});

test('incomplete or invalid coordinates do not create phantom areas', () => {
  const s = loadedState(ref, sim, classic, active, [], [], [...anchors,
    row({ anchor: 'c', axis: 'x' }, 100), row({ anchor: 'd', axis: 'x' }, NaN), row({ anchor: 'd', axis: 'z' }, 0)]);
  assert.equal(s.anchors.length, 2);
});
