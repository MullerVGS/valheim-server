// Area carregada pelo servidor. Rodar: node --test site/test/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bandAt, loadedArea, outline, zoneOf } from '../public/loaded.js';

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
