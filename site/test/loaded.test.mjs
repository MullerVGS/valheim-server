// Area carregada pelo servidor. Rodar: node --test site/test/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bandAt, loadedArea, loadedAreas, loadedHitAt, outline, zoneOf } from '../public/loaded.js';

const CLASSIC = { near: 2, far: 2, classic: true };

test('zona do jogo: centro em id * 64, borda na meia zona', () => {
  assert.deepEqual(zoneOf(0, 0), [0, 0]);
  assert.deepEqual(zoneOf(31.9, -32.1), [0, -1]);
  assert.deepEqual(zoneOf(130, -850), [2, -13]);
  assert.deepEqual(zoneOf(-2000, 1400), [-31, 22]);
});

test('classico 2+2: 5x5 zonas carregadas, anel ate 9x9 de distantes', () => {
  const a = loadedArea(0, 0, CLASSIC);
  assert.equal(a.near.length, 25);
  assert.equal(a.distant.length, 81 - 25);
  assert.equal(a.active.half, 96);
  assert.equal(a.active.radius, undefined);
});

test('simulado = 96 m do centro da zona, nao do ponto', () => {
  const a = loadedArea(30, 30, CLASSIC);
  assert.equal(bandAt(a, 96, -96), 'active');
  assert.equal(bandAt(a, 97, 0), 'near');
  assert.equal(bandAt(a, 0, -159), 'near');
  assert.equal(bandAt(a, 0, 161), 'distant');
  assert.equal(bandAt(a, 289, 0), null);
});

test('fora do classico: zonas pelo raio e simulado recortado em 112 m', () => {
  const a = loadedArea(0, 0, { near: 2, far: 2, classic: false });
  assert.equal(a.near.length, 21);
  assert.ok(!a.near.some(([x, z]) => Math.abs(x) === 2 && Math.abs(z) === 2));
  assert.equal(a.active.radius, 112);
  assert.equal(bandAt(a, 90, 90), 'near');
  assert.equal(bandAt(a, 90, 0), 'active');
});

test('near 1: simulado so 64 m do centro', () => {
  const a = loadedArea(0, 0, { near: 1, far: 2, classic: true });
  assert.equal(a.near.length, 9);
  assert.equal(a.active.half, 64);
});

test('contorno da uniao nao desenha os lados internos', () => {
  assert.equal(outline([[0, 0]]).length, 4);
  assert.equal(outline([[0, 0], [1, 0]]).length, 6);
  assert.equal(outline(loadedArea(0, 0, CLASSIC).near).length, 20);
});

test('distant areas stay active together without loading the corridor', () => {
  const c = loadedAreas({ ...CLASSIC, anchors: [{ x: 0, z: 0 }, { x: 2048, z: -1024 }] });
  assert.equal(c.near.length, 50);
  assert.equal(loadedHitAt(c, 0, 0).band, 'active');
  assert.equal(loadedHitAt(c, 2048, -1024).band, 'active');
  assert.equal(loadedHitAt(c, 1024, -512), null);
});

test('overlaps deduplicate zones and simulation takes precedence over distant rings', () => {
  const c = loadedAreas({ ...CLASSIC, anchors: [{ x: 0, z: 0 }, { x: 192, z: 0 }] });
  assert.equal(c.near.length, 40);
  assert.ok(!c.distant.some(([x, z]) => c.near.some(([nx, nz]) => x === nx && z === nz)));
  assert.equal(loadedHitAt(c, 192, 0).band, 'active');
  assert.deepEqual(loadedHitAt(c, 192, 0).area.anchor, { x: 192, z: 0 });
});

test('signs in the same zone share coverage; removing an area preserves the other', () => {
  const first = { x: 0, z: 0 };
  const c = loadedAreas({ ...CLASSIC, anchors: [first, { x: 20, z: 20 }] });
  assert.equal(c.near.length, 25);
  assert.equal(outline(c.near).length, 20);
  const remaining = loadedAreas({ ...CLASSIC, anchors: [first] });
  assert.equal(loadedHitAt(remaining, 0, 0).band, 'active');
  assert.equal(loadedHitAt(remaining, 2048, 0), null);
});

test('older single-center state remains supported', () => {
  const c = loadedAreas({ ...CLASSIC, x: 130, z: -850 });
  assert.equal(c.areas.length, 1);
  assert.equal(loadedHitAt(c, 130, -850).band, 'active');
});
