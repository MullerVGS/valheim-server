// Editor visual das placas: codigo -> pecas com estilo -> codigo. Rodar: node --test site/test/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { atomAt, commonStyle, parse, serialize, withStyle } from '../public/signrich.js';

const env = {
  isTag: (raw) => /^<\/?(rotate|sup|b)\b/i.test(raw),
  isIcon: (inner) => ['mel', 'mel 50%', 'wood'].includes(inner.toLowerCase()),
  isMacro: (name) => name === 'x',
};
const text = (atoms) => atoms.map((a) => (a.t === 'c' ? a.ch : a.t === 'n' ? '\n' : `[${a.raw}]`)).join('');
const round = (src) => serialize(parse(src, env).atoms).src;

test('cores, tamanho, sem luz e faixa viram estilo e voltam iguais', () => {
  const { atoms } = parse('<#fc6>Hidromel\n<size=4>{u}<#f80>da casa', env);
  assert.equal(text(atoms), 'Hidromel\nda casa');
  assert.deepEqual(atoms[0].st, { c: 'fc6' });
  assert.deepEqual(atoms.at(-1).st, { c: 'f80', z: '4', l: true });
  assert.equal(round('<#fc6>Hidromel\n<size=4>{u}<#f80>da casa'), '<#fc6>Hidromel\n<#f80><size=4>{u}da casa');
  for (const src of ['a<i>b</i>c', '<mark=#000000aa>x</mark>y', 'a<u>b<s>c', '<size=5>x</size>y']) assert.equal(round(src), src);
});

test('pilha de cor como no jogo: </color> volta a anterior', () => {
  const { atoms } = parse('<#f00>a<#0f0>b</color>c</color>d', env);
  assert.deepEqual(atoms.map((a) => a.st.c ?? null), ['f00', '0f0', 'f00', null]);
  assert.equal(round('<#f00>a<#0f0>b</color>c</color>d'), '<#f00>a<#0f0>b<#f00>c</color></color></color>d');
  assert.equal(round('<color=#FF8800>a<color=red>b'), '<#ff8800>a<#f00>b');
});

test('icone, abreviacao e tag sem botao viram chip; o resto e texto', () => {
  const { atoms } = parse('Mel :mel: <rotate=15>{x}{y}<oi>:nada:\\n', env);
  assert.deepEqual(atoms.filter((a) => a.t === 'x').map((a) => a.kind), ['icon', 'tag', 'macro']);
  assert.equal(text(atoms), 'Mel [:mel:] [<rotate=15>][{x}]{y}<oi>:nada:\n');
  assert.equal(round('<size=12>:mel 50%:'), '<size=12>:mel 50%:');
  assert.equal(round('Bau 2: lenha :wood:'), 'Bau 2: lenha :wood:');
});

test('quebra de linha e tag solta nao escrevem estilo; nada fecha no fim', () => {
  const a = parse('<#f80>ab', env).atoms;
  const nl = { t: 'n', st: {} };
  assert.equal(serialize([a[0], nl, a[1]]).src, '<#f80>a\nb');
  assert.equal(serialize([...a, { t: 'c', ch: 'c', st: {} }]).src, '<#f80>ab</color>c');
});

test('posicoes: cada peca sabe onde comeca no codigo e volta para o mesmo lugar', () => {
  const src = '<#f80>a{u}b\n:mel:c';
  const { atoms } = parse(src, env);
  const { src: out, pos } = serialize(atoms);
  assert.equal(out, src);
  atoms.forEach((x, n) => assert.equal(pos[n], x.at));
  const again = parse(out, env).atoms;
  assert.equal(atomAt(again, pos[2]), 2);
  assert.equal(atomAt(again, out.length), atoms.length);
});

test('estilo comum de um trecho', () => {
  const { atoms } = parse('<#f80>ab<i>c', env);
  assert.deepEqual(commonStyle(atoms, 0, 2), { c: 'f80', z: null, l: null, m: null, i: null, u: null, s: null });
  assert.equal(commonStyle(atoms, 0, 3).i, 'mixed');
  assert.equal(commonStyle(atoms, 1, 1), null);
  assert.deepEqual(withStyle({ c: 'f80' }, 'c', null), {});
});
