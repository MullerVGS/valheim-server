# site

Mapa do servidor no estilo do mapa do jogo + métricas básicas do VictoriaMetrics.

- `public/mapgl.js`: porte para WebGL2 do shader `Custom/mapshader` do jogo (material `minimap`),
  com as mesmas texturas de arte, as cores de bioma do prefab do `Minimap` e o pipeline de cor linear.
- `server.mjs`: Node sem dependências. Estáticos, terreno com ETag (sempre revalida) e
  `/api/state` / `/api/history` com consultas fixas ao Victoria (`VM_URL`, cache de 5 s / 60 s).
- `terrain.mjs`: dados do mapa, recortados pelo que as mesas de cartografia mostram (+2 px para a borda da
  névoa); o terreno do mundo inteiro nunca sai do servidor. Fonte: os arquivos que o plugin grava em
  `MAP_DIR` (`terrain.bin`, `explored.bin`, `pieces.bin`, formatos em `plugin/src/Map/MapFiles.cs`),
  conferidos a cada 15 s. Sem eles, terreno de `data/terrain-full.bin` e explorado lido do save
  (`SAVE_DIR`), sem construções.

## Dados para o navegador

- `GET /data/terrain.bin`: `VHM1` (ver `tools/build_terrain.py`), zerado fora do explorado; seed omitida.
- `GET /data/pieces.bin`: `VPC1` | u32 n | n × (f32 x, f32 z, f32 y, f32 cos, f32 sin, f32 meiaX,
  f32 meiaZ, u8 tipo). Retângulo de cada construção visto de cima (centro x/z em metros de mundo, giro
  pelo yaw, meia largura nos eixos locais); `y` é a altura (o mais alto cobre o de baixo). Tipo:
  0 madeira, 1 madeira nobre, 2 madeira de Yggdrasil, 3 pedra, 4 mármore, 5 grausten, 6 ferro,
  7 antigo, 8 gelo, 9 móvel/estação, 10 plantação, 11 barco. 404 enquanto o plugin não gravou.
- `GET /api/state`, `GET /api/history`: jogadores, marcações, portais, camas, mesas e métricas.

## Preparar

A arte é da Iron Gate e o terreno é spoiler: os dois ficam fora do git.

```sh
# arte do mapa, da instalação do jogo (UnityPy + Pillow >= 10)
python3 tools/extract_game_art.py --game "/caminho/para/Valheim" --out public/game
# só sem o plugin: terreno do mundo inteiro a partir de uma grade do gerador de mundo
# (12 m, centro de pixel: -12282..12282) com bioma, altura e fator de floresta
python3 tools/build_terrain.py world.bin data/terrain-full.bin
```

## Rodar

```sh
MAP_DIR=/caminho/map SAVE_DIR=/caminho/worlds_local/<mundo> VM_URL=http://127.0.0.1:8428 PORT=8787 node server.mjs
```

No compose do repositório é o serviço `site` (profile `site`):
`docker compose --profile site up -d --no-deps site`.
