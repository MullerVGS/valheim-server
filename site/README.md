# site

Mapa do servidor no estilo do mapa do jogo + métricas básicas do VictoriaMetrics.

- `public/mapgl.js`: porte para WebGL2 do shader `Custom/mapshader` do jogo (material `minimap`),
  com as mesmas texturas de arte, as cores de bioma do prefab do `Minimap` e o pipeline de cor linear.
- `server.mjs`: Node sem dependências. Estáticos, terreno com ETag (sempre revalida) e
  `/api/state` / `/api/history` com consultas fixas ao Victoria (`VM_URL`, cache de 5 s / 60 s).
- `terrain.mjs`: o terreno do mundo inteiro fica só no servidor; o navegador recebe uma cópia
  zerada fora do que as mesas de cartografia mostram (+2 px para a borda da névoa). O explorado sai
  do save (`SAVE_DIR`), relido quando o autosave regrava a pasta do mundo. Não toca o processo do jogo.

## Preparar

A arte é da Iron Gate e o terreno é spoiler: os dois ficam fora do git.

```sh
# arte do mapa, da instalação do jogo (UnityPy + Pillow >= 10)
python3 tools/extract_game_art.py --game "/caminho/para/Valheim" --out public/game
# terreno do mundo inteiro, uma vez por seed, a partir de uma grade do gerador de mundo
# (12 m, centro de pixel: -12282..12282) com bioma, altura e fator de floresta
python3 tools/build_terrain.py world.bin data/terrain-full.bin
```

## Rodar

```sh
SAVE_DIR=/caminho/worlds_local/<mundo> VM_URL=http://127.0.0.1:8428 PORT=8787 node server.mjs
```

No compose do repositório é o serviço `site` (profile `site`):
`docker compose --profile site up -d --no-deps site`.
