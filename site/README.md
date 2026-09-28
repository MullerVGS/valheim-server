# site

Mapa do servidor no estilo do mapa do jogo + métricas básicas do VictoriaMetrics.

- `public/mapgl.js`: porte para WebGL2 do shader `Custom/mapshader` do jogo (material `minimap`),
  com as mesmas texturas de arte, as cores de bioma do prefab do `Minimap` e o pipeline de cor linear.
- `public/pieces.js`: construções do `pieces.bin` rasterizadas numa textura do tamanho da tela, que o shader
  do mapa pinta com a mesma luz, o pergaminho, as nuvens e a névoa do terreno: contorno de tinta, sombra do
  sol, chão pisado sem árvores em volta e nuvens ralas sobre as bases. Precisa de `EXT_color_buffer_float`;
  sem ele o mapa abre sem construções.
- `mapview.js`: o mapa num canvas (renderer, camada 2D, arrastar/zoom/pinça/clique), usado pelo mapa principal e
  pelas páginas. Estático: água, névoa e nuvens paradas, e só desenha quando a vista ou os dados mudam;
  ícones e baús viram imagens com a sombra pronta (`sprite`), sem desfoque a cada quadro. `common.js`: nomes do jogo, itens, e as regras de base (agrupamento de peças que contém um ponto).
- Páginas: `/jogador/<nome>` (`player.html`/`player.js`: rastro com volta no tempo, horas por dia e por hora,
  bases que ergueu, lápides, camas, marcações) e `/base/<x>,<z>` (`base.html`/`base.js`: construtores, materiais,
  crescimento dia a dia, estoque somado e cada baú). No mapa, clicar num jogador, base ou baú abre um cartão com
  o básico e o link da página; o painel tem busca de item nos baús, rastros e a linha do tempo com play.
- `public/sidebar.js`: barra lateral de navegação de todas as páginas (mapa, base, jogador). Página ou ferramenta
  nova entra como item em `NAV`; `soon: true` mostra "em breve" sem link. Tela larga: trilho de ícones que abre
  com os nomes e lembra a escolha (`localStorage`); tela média: abre por cima; celular: gaveta pelo botão de menu (fecha no toque fora, Esc ou arrastando
  para a esquerda).
- `world.mjs`: lê o save ao vivo (`SAVE_DIR`) quando o autosave termina (`_main.<n>.ok` novo): baús com o que tem
  dentro (qualquer ZDO com inventário e criador, e lápides), placas a até 2,5 m como nome do baú, camas e quem
  construiu cada peça (id do personagem pelas camas/lápides; sem eles, `creatorIndex` → histórico de jogadores do
  `.fwl2` → `players.tsv`). Só o que está no explorado das mesas.
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
- `GET /api/world`: baús (`items` = `[hash do prefab, quantidade, qualidade]`), camas, a contagem de peças por
  construtor numa grade de `cell` m (`[cx, cz, construtor, peças]`) e as áreas de base (`baseAreas` =
  `[x, z, raio, prefab]`), do save ao vivo.

## Sem spawn (área de base)

Camada "Sem spawn": a união dos círculos das peças que abrem `EffectArea` do tipo `PlayerBase`, onde nenhum spawn
natural nasce (raide e spawner fixo, como ninho, ignoram). `base-areas.json` (prefab → raio) sai da instalação do
jogo com `tools/extract_base_areas.py`; refaça depois de update que traga peça nova.
- `GET /api/players`: todo jogador que o Victoria viu em 180 dias (primeira/última vez, minutos em 7 dias).
  `GET /api/player?name=`: minutos online por hora (30 dias) e total. `GET /api/trails?hours=1|6|24|72|168[&name=]`:
  posição no tempo (`[t, x, z]`), passo de 10 s a 5 min conforme a janela. Nome só se o Victoria já conhece.
- `GET /api/days/area?x0=&z0=&x1=&z1=`: peças dentro da caixa em cada dia guardado.
- `GET /api/days`: dias guardados (data, hora do save, km² explorados, construções, marcações);
  `GET /data/days/<AAAA-MM-DD>/terrain.bin`, `.../pieces.bin` e `GET /api/days/<AAAA-MM-DD>/pins`: o mapa daquele dia.

## Escondidos

Qualquer visitante pode esconder uma base, baú, portal, cama, marcação ou jogador (botão no pé do cartão, com
confirmação). `hidden.mjs` tira aquilo de toda resposta para todo mundo, menos para quem escondeu, que continua
vendo com a marca de escondido e é o único que pode mostrar de novo (botão "Escondidos" do mapa).

- Quem é quem: cookie `jahmaica_id` (aleatório, HttpOnly, 400 dias). Sem ele, `POST /api/me` com a impressão
  básica do navegador (`public/hidden.js`: UA, idioma, tela, fuso, canvas, WebGL) devolve o id antigo. Navegador
  ou aparelho diferente não é reconhecido; dois navegadores idênticos seriam.
- Base escondida = os agrupamentos de peças de agora que tocam a caixa guardada, +15 m: se a base cresce, a parte
  nova some junto. Some tudo dentro dela (peças, baús, camas, portais, marcações, mesas, rastros), e quem estiver
  lá aparece online sem posição. Portal de fora que aponta para dentro perde o par.
- Jogador escondido: posição, rastros e página sem mapa; continua na lista de online.
- Estado em `HIDDEN_FILE` (padrão `ARCHIVE_DIR/hidden.json`). Respostas por pessoa: `GET /api/pieces`,
  `/api/days/<dia>/pieces` (sem extensão, `private`, `Vary: Cookie`); `/data/pieces.bin` e `.../pieces.bin`
  (que a Cloudflare pode guardar) são a vista de quem não escondeu nada.
- `GET /api/hidden` (os meus), `POST /api/hide` `{hide: {kind, title, x, z, box?, name?}}`, `POST /api/unhide`
  `{id}`. Só JSON (`Content-Type`), cookie `SameSite=Lax`.
- Tirar um escondido à mão: editar o JSON no host e reiniciar o site.

## Histórico

O servidor apaga zip de backup com mais de `BACKUPS_MAX_AGE` dias. `archive.mjs` copia o último zip de cada dia
para `ARCHIVE_DIR/saves` (fica para sempre, ~23 MB por dia) e tira dele um retrato do mapa em `ARCHIVE_DIR/days`:
explorado, pins das mesas e construções. `saves.mjs` lê o save da 1.0 fora do jogo (índice `_main.<n>.chunks`
+ chunks, formato do `ZDO.Save`); a forma de cada peça vem do `pieces-catalog.bin` do plugin. Roda no boot e
de hora em hora, e refaz um dia quando aparece zip mais novo dele.

## Preparar

A arte é da Iron Gate e o terreno é spoiler: os dois ficam fora do git.

```sh
# arte do mapa, da instalação do jogo (UnityPy + Pillow >= 10)
python3 tools/extract_game_art.py --game "/caminho/para/Valheim" --out public/game
# nomes e ícones dos itens (busca e baús): public/game/items.json + items.webp
python3 tools/extract_items.py --game "/caminho/para/Valheim" --out public/game
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
