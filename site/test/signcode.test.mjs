// As regras de placa do site tem de dar o mesmo que o plugin (plugin/tests/SignLabelTests.cs e
// SignTextTests.cs usam os mesmos catalogos de brinquedo). Rodar: node --test site/test/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { LIMIT, compose, parseCatalog, parseIcon, plan, slice } from '../public/signcode.js';

const Reset = '<size=100.0%><cspace=0.0><line-height=100.0%>';
const Plain = '<cspace=-0.0><size=1><line-height=0><#0000>{label}\n<cspace=-0.288><material=Valheim_Fonts/Valheim-Norse><line-height=0.32><size=0.368><#a63>███\n<#0000>█<#a63>██' + Reset;
const Titled = '<cspace=-0.0><size={ls}><line-height=1>{label}\n<cspace=-0.276><material=Valheim_Fonts/Valheim-Norse><line-height=0.24><size=0.276><#a63>███\n<#0000>█<#a63>██' + Reset;
const Unlit = '<material=Valheim_Fonts/Valheim-Norse>';

function catalogOf(lines, custom = []) {
  const cat = parseCatalog([{ lines, kind: 'catalog' }, { lines: custom, kind: 'custom' }]);
  return {
    cat,
    params: cat.params,
    macros: cat.macros,
    known: (k) => cat.texts.has(k) || k in cat.aliases,
    resolve: (k) => (cat.texts.has(k) ? k : cat.aliases[k] ?? cat.defaultIcon),
    entry: (id) => ({ i: cat.texts.get(id), t: cat.titled.get(id) ?? null }),
  };
}

const toy = () => catalogOf([
  'D\tweed',
  'I\twood\t' + Plain.replaceAll('\n', '\\n'),
  'T\twood\t' + Titled.replaceAll('\n', '\\n'),
  'I\tweed\t' + Plain.replaceAll('\n', '\\n').replaceAll('a63', '3a3'),
  'A\tmadeira\twood',
  'M\tu\t' + Unlit,
]);

const full = (brightness) => catalogOf([
  'P\tbrightness\t' + brightness,
  'I\twood\t<cspace=-0.0><size=1><line-height=0><#0000>{label}\\n<cspace=-0.288><line-height=0.32><size=0.368><#fa5>██<#0000>█<#84c8>█<#ffaa55>█' + Reset,
  'T\twood\t<cspace=-0.0><#bba><size={ls}><line-height=1>{label}\\n<cspace=-0.276><line-height=0.24><size=0.276><#fa5>██' + Reset,
]);

const draw = (c, text) => plan(text, c).final;

test('codigo na ponta vira rotulo e icone', () => {
  for (const [text, key, label] of [['Madeira :wood:', 'wood', 'Madeira'], ['  Madeira   :Wood:  ', 'wood', 'Madeira'],
    [':wood: Madeira', 'wood', 'Madeira'], ['Bau 2: lenha :fine wood:', 'finewood', 'Bau 2: lenha']]) {
    const p = parseIcon(text);
    assert.equal(p.key, key);
    assert.deepEqual(p.label, { text: label, shown: true });
  }
  assert.deepEqual(parseIcon(' :Yellow Mushroom: ').label, { text: 'Yellow Mushroom', shown: false });
  assert.equal(parseIcon('texto comum'), null);
});

test('tamanho pedido refaz o cabecalho do desenho', () => {
  const c = toy();
  assert.ok(draw(c, '<size=15.2>:wood:').includes('\n<cspace=-0.126><material=Valheim_Fonts/Valheim-Norse><line-height=0.64><size=0.736><#a63>███\n'));
  assert.ok(draw(c, '<size=3.8>:wood:').includes('<cspace=-0.264><material=Valheim_Fonts/Valheim-Norse><line-height=0.16><size=0.184>'));
  assert.ok(draw(c, 'Madeira <size=5>:wood:').includes('<size=2><line-height=1>Madeira\n<cspace=-0.272>'));
  assert.ok(draw(c, 'Madeira <size=7.6>:wood:').includes('<size=2><line-height=1>Madeira\n<cspace=-0.078>'));
  assert.ok(draw(c, '<size=500>:wood:').includes('<line-height=0.758>'));
});

test('brilho e sobre o padrao do catalogo e nao passa da cor cheia', () => {
  assert.ok(draw(full('0.6'), ':wood:').includes('<#963>██<#0000>█<#5278>█<#996633>█'));
  assert.ok(draw(full('0.6'), ':wood 50%:').includes('<#532>██'));
  assert.ok(draw(full('0.6'), ':wood 1000%:').includes('<#fa5>██'));
  assert.ok(draw(full('0.6'), 'Madeira :wood 50%:').startsWith('<cspace=-0.0><#bba><size=2><line-height=1>Madeira\n'));
  assert.ok(draw(toy(), ':wood 50%:').includes('<#532>███'));
});

test('codigo desconhecido cai no padrao', () => {
  const p = plan(':nada:', toy());
  assert.equal(p.id, 'weed');
  assert.equal(p.known, false);
});

test('abreviacao se expande e texto longo vira coladas de ate 50', () => {
  const c = toy();
  assert.equal(draw(c, '{u}Oi'), Unlit + 'Oi');
  const long = 'Bem-vindos à <#f80>casa do Ragnar\n<size=4>{u}<#fc6>hidromel, carne e lenha para todos os vikings que chegarem';
  const p = plan(long, c);
  assert.ok(p.parts.length > 1);
  assert.ok(p.parts.every((part) => part.length <= LIMIT));
  assert.ok(p.parts.slice(1).every((part) => part.startsWith('>>')));
  assert.ok(p.parts.every((part) => !parseIcon(part)));
  assert.equal(p.final, long.replace('\n', '\\n').replace('{u}', Unlit));
  // nenhuma tag partida entre duas coladas
  assert.ok(p.parts.every((part) => !/<[^>]*$/.test(part)));
});

test('troca por equivalente mais curto', () => {
  const p = plan('<#ff8800>Oi <color=#aabbcc>la</color> ' + Unlit + 'x', toy());
  assert.equal(p.typed, '<#f80>Oi <#abc>la</color> {u}x');
});

test('pedaco nunca parece pedido de icone', () => {
  const text = 'a'.repeat(47) + ':mel: texto que continua depois do codigo';
  for (const part of slice(text)) assert.equal(parseIcon(part), null);
});

test('artes: grupos, nomes e vitrine vem dos comentarios #@ do custom.txt', () => {
  const c = catalogOf(['I\twood\tx'], [
    '# gerador v1', '#@ grupo Fundos', '#@ dica :fundo LxA cor:', '#@ vitrine fundo1', '#@ nome fundo1 fundo 1',
    'I\tfundo1\ty', 'I\tfundo2\tz', '# outro', 'I\twood\tw',
  ]);
  assert.deepEqual(c.cat.items, []);
  assert.deepEqual(c.cat.groups.map((g) => [g.title, g.hint, g.ids, g.shown]), [
    ['Fundos', ':fundo LxA cor:', ['fundo1', 'fundo2'], ['fundo1']],
    ['outro', '', ['wood'], null],
  ]);
  assert.equal(c.cat.names.get('fundo1'), 'fundo 1');
});
