# valheim-server

Stack Docker de servidor dedicado Valheim. Motor neutro: config e segredo ficam no host, fora do git.

## Subir

```sh
cp .env.example .env && chmod 600 .env   # editar
docker compose up -d
docker compose logs -f
```

1º boot baixa o servidor (app Steam 896660, ~3 GB) — leva alguns minutos até aceitar conexão.

## Limites

- Teto do jogo: **10 jogadores**.
- **UDP 2456-2457**. Trafego de jogo, direto no host — nao passa por reverse proxy.
- CPU: single-thread bound. 1 core rapido > muitos cores.
- RAM: ~3-4 GB vanilla c/ mundo maduro. `MEM_LIMIT` existe pra nao derrubar vizinho de host.
- `SERVER_PASS`: min 5 chars, nao pode estar contido no `SERVER_NAME`.
- Mods (BepInEx) passam de 8 GB. Nao cabem em host compartilhado.
- Tres crons diarios: backup (`BACKUPS_CRON`), update (`UPDATE_CRON`) e restart
  (`RESTART_CRON`, 05:10 por default da imagem). O restart e gateado por `valheim-is-idle`:
  nao derruba ninguem conectado, mas tambem nao roda se houver gente online no horario.

## World Modifiers

Ajustam a dificuldade sem recriar o mundo. Valem para mundo ja existente e preservam o
progresso: no load o jogo limpa so as chaves de modificador, e as de boss derrotado ficam
fora desse intervalo.

Por `SERVER_ARGS` (aplica no boot):

```sh
SERVER_ARGS=-resetmodifiers -modifier deathpenalty casual -setkey playerevents
```

Os args sao processados em ordem, entao comecar por `-resetmodifiers` torna a linha
declarativa em vez de acumulativa -- importante porque `-setkey` so adiciona.

- `-modifier <eixo> <valor>` -- eixos: `combat`, `deathpenalty`, `resources`, `raids`,
  `portals`.
- `-setkey <chave>` -- os toggles: `playerevents`, `teleportall`, `passivemobs`, `nomap`,
  `noportals`, `nobossportals`, `nobuildcost`, `fire`.
- `-preset <nome>` -- mexe em todos os eixos de uma vez.

Pelo console, com admin (nao precisa reiniciar):

- `setworldmodifier <eixo> <valor>`, `setworldpreset <nome>`, `resetworldkeys` -- valem na
  hora e **persistem** no mundo.
- `setkey` / `removekey` -- valem na hora mas **nao persistem**: some no proximo boot.

Admin sai de `ADMINLIST_IDS` (SteamID64) e do `adminlist.txt` em `/config`. O arquivo e
relido sozinho a cada 10s, entao promover alguem nao exige restart.

## Achievements

Nao ha risco em usar os modificadores acima. O jogo so marca o mundo como "cheated" quando
encontra uma global key que a GUI de World Modifiers nao conseguiria setar -- e ai desliga os
achievements da plataforma. As armadilhas sao duas, ambas via `SERVER_ARGS`, porque o
`-setkey` **nao valida nada**:

- chave fora da lista de toggles acima (ex.: `nocraftcost`, `worldlevel`, `noworkbench`);
- valor fora do slider do eixo (ex.: `deathpenalty less`).

O `setkey` do console, ao contrario, valida e recusa a chave. E comando de cheat nao existe
em servidor dedicado: exige `IsServer`, que nenhum cliente conectado e.

## Metricas (opt-in)

Plugin BepInEx em `plugin/`, so no servidor: expoe `/metrics` (Prometheus) na porta 9780 do
container, sem publicar no host. Nao muda nada pro jogador nem desliga achievement
(`Game.isModded` so e lido no proprio processo).

- por jogador: ping, qualidade, bytes/s contra o teto de 150 KiB/s, fila do Steam, ciclos de
  envio pulados (fila > limite - 2048), ZDOs enviados/recebidos, ZDOs e mobs (normal/raid) que o
  cliente dele simula;
- servidor: FPS (o jogo mira 30), tempo de frame, tempo por subsistema, save, desconexoes, GC;
- raid ativa, rodando ou pausada, e quem esta no raio;
- RPC por metodo (recebido, enviado, roteado).
- banda de ZDO por prefab (`valheim_zdo_traffic_bytes_total`, enviado e recebido) e, a cada 5 min no log
  (`Trafego de ZDO`), os 10 objetos e as 5 zonas de 64 m mais caros, com posicao e dono.
- posicao: jogadores, pins das mesas de cartografia, portais, camas, mortes, reclamacoes de lag e
  ZDOs/banda por zona de 64 m, com `x`/`z` em metros e `lat`/`lon` para o Geomap (ver Mapa).

```sh
# 1. BEPINEX=true no .env e subir: o boot baixa o BepInExPack pro volume
docker compose up -d
# 2. compilar contra o servidor do volume; grava em /config/bepinex/plugins
docker compose --profile plugin run --rm plugin-build
# 3. carregar
docker compose restart valheim
```

Coleta: ponha o container na rede do seu Prometheus/vmagent e raspe `valheim:9780/metrics`
(5s mostra a dinamica de uma raid). `valheim_exporter_patch_ok=0` = uma atualizacao do jogo mudou
um metodo: so aquela metrica some, o jogo segue. O BepInEx atualiza sozinho (`latest`) e e
reinstalado a cada update do jogo; o `PRE_BEPINEX_CONFIG_HOOK` do compose recoloca o plugin
nessa reinstalacao. Sem ele o servidor volta sem plugin (`0 plugins to load` no log) ate o
proximo `docker compose restart valheim`.

### Ajustes de rede (opt-in)

O servidor dedicado manda ZDOs (objetos, mobs, jogadores) a **um jogador por frame**, num pacote
de ate **10240 bytes**, e mira **30 FPS**. Com N jogadores, cada um recebe no maximo
`10240 * 30 / (N+1)` bytes/s: 34 KB/s com 8. Com o grupo espalhado ou muito mob por perto, o
pacote enche e a sincronizacao atrasa (mob teleportando, item demorando pra entrar no inventario),
com CPU sobrando. `valheim_zdo_send_cycles_total` e os bytes de saida por jogador mostram isso.

O plugin mexe nas tres alavancas, so com a variavel definida no `.env`:

| Variavel | Jogo | Faixa | Efeito |
| --- | --- | --- | --- |
| `VALHEIM_SERVER_FPS` | 30 | 30..360 | ciclo por jogador em `(N+1)/FPS` s (minimo de 50 ms por rodada); CPU do servidor sobe junto |
| `VALHEIM_ZDO_SEND_LIMIT_BYTES` | 10240 | 10240..65536 | bytes por ciclo; fila acima de `limite - 2048` pula o ciclo |
| `VALHEIM_STEAM_SEND_RATE_BYTES` | 153600 | 153600..1048576 | bytes/s por conexao no Steam (minimo e maximo, taxa fixa) |

FPS e limite multiplicam: 60 FPS e 20480 bytes dao 4x (136 KB/s por jogador com 8). Mas o
Steam corta cada conexao na propria taxa, e o que passa dela fica na fila: **subir o limite sem
subir a taxa do Steam so aumenta a fila e atrasa a sincronizacao**. Suba os tres juntos, olhando
qual corta primeiro. A taxa do Steam e fixa (o jogo poe o mesmo valor em minimo e maximo), entao
cada jogador precisa ter essa banda de download. O envio do **cliente** para o servidor tem os
mesmos limites no jogo dele, e nao muda por aqui.

Aplicar exige recriar o container (variavel nova):

```sh
docker compose up -d                      # derruba quem estiver jogando
```

Conferir em `/metrics`: `valheim_server_target_frame_rate`, `valheim_zdo_send_limit_bytes`,
`valheim_steam_send_rate_bytes_per_second` (lido do proprio Steam) e `valheim_exporter_patch_ok`
dos alvos `ZDOMan.SendZDOs#transpiler` e `ZSteamSocket.RegisterGlobalCallbacks#transpiler`. Rollback = apagar as variaveis e
repetir o comando.

### Travada horaria e troca de dono (opt-in)

Duas travadas que o jogo produz sozinho, cada uma com uma variavel no `.env` (recriar o container):

- `VALHEIM_DEFER_ASSET_UNLOAD=1`: de hora em hora o jogo chama `Resources.UnloadUnusedAssets`, que
  no dedicado trava a thread 0,7-0,9 s para liberar quase nada. Com jogador online a limpeza espera
  o servidor esvaziar e roda na hora em que ele fica vazio. Conferir em
  `valheim_asset_unload_deferred_total`, `valheim_asset_unload_idle_runs_total` e no tempo de frame.
- `VALHEIM_OWNER_HYSTERESIS=on|measure`: o servidor passa a posse de cada objeto para outro jogador
  assim que ele sai da area ativa do dono (1,5 zona de 64 m a partir do centro da zona dele), mas o
  cliente do dono segue carregando o objeto ate a distancia de simulacao dele (2 zonas no padrao).
  Base sobre a borda de zona vira ping-pong: milhares de pecas trocam de dono a cada passo e cada
  troca e reenviada a todos. Com `on`, pecas, baus, plantas e bichos domesticados so trocam de dono
  quando saem do que o dono tem carregado; criatura selvagem e jogador seguem a regra do jogo. Com
  `measure` o comportamento e o do jogo, e so conta o que seria segurado. Conferir em
  `valheim_zdo_owner_changes_total{action,kind}`, `valheim_zdo_owner_kept_total{kind}` e
  `valheim_player_simulation_distance_zones`. Se a replica falhar, o plugin volta sozinho ao metodo
  do jogo (`valheim_exporter_patch_errors_total` sobe e o log explica).

### Icones nas placas (opt-in)

A placa do jogo e um TextMeshPro com rich text, e o limite de 50 caracteres e so do campo de
digitacao do cliente: o texto gravado no mundo pode ter qualquer tamanho. Com o catalogo ligado,
quem escreve `:mel:` numa placa recebe de volta o icone do item, desenhado como pixel art de
texto (um bloco colorido por pixel). Quem ve e o jogo sem mod nenhum.

- vale o prefab do item (`:MushroomYellow:`) e o nome no jogo em ingles ou portugues
  (`:yellow mushroom:`, `:cogumelo amarelo:`); maiuscula, acento, espaco e `_` nao importam;
- codigo desconhecido vira o icone padrao, uma folha (`:weed:`);
- `Madeira :wood:` (ou `:wood: Madeira`) poe o rotulo na tabua, em cima do icone, que sai um
  pouco menor para caber. Rotulo de ate 10 caracteres sai grande, ate 22 sai pequeno, e maior
  que isso fica so no hover;
- quem mira a placa le o rotulo no hover; sem rotulo, le o que foi digitado dentro do codigo;
- o rotulo aceita cor, negrito e abreviacao (`{u}<#fc6>Madeira :wood:`); tag que muda o tamanho da
  linha e descartada, porque o desenho depende de tudo caber na tabua;
- uma porcentagem no fim do codigo e o brilho daquele icone, sobre o padrao do catalogo:
  `:wood 50%:` apaga pela metade, `:wood 140%:` chega na cor cheia (que estoura em bloom a noite).
  O brilho da letra e a cor dela: `<#fff>Madeira :wood:`;
- `<size=N>` colado no codigo e o lado do icone em unidades da tabua (ela tem 18,3 x 8,55; o
  padrao e 7,6): `<size=4>:wood:` encolhe, `<size=15>:wood:` vaza da tabua, ate 18, que e a
  largura que ainda nao quebra linha. Vale junto com rotulo: `Madeira <size=12>:wood:`;
- escrever qualquer outra coisa por cima devolve a placa ao jogador. Quem aperta E numa placa de
  icone ve o comeco do texto gerado; confirmar sem mexer nao estraga, o servidor redesenha;
- so o prefab `sign` e tocado, e so placa com um codigo na ponta do texto, ou que usa
  abreviacao ou continuacao (abaixo).

O hover do jogo mostra o texto da placa sem as tags, mas a regex dele nao aceita ponto: tag com
numero decimal sobra e vale no hover, que tambem e TextMeshPro. O gerador usa isso: as tags do
desenho tem sempre ponto (no hover o icone vira um cisco), as do rotulo nunca (no hover ele sai
legivel), e o texto termina devolvendo o tamanho, senao o `[E] Usar` some junto.

**Escrever mais que 50 caracteres.** O corte e do campo de digitacao de cada cliente e nao sai sem
mod nele; o servidor contorna aceitando texto curto e gravando texto longo:

- `{u}` e abreviacao do catalogo (linha `M`): vira `<material=Valheim_Fonts/Valheim-Norse>`, o
  material sem iluminacao, que deixa bloco e emoji visiveis no escuro. `{u}<size=20><#f60>🔥` cabe
  folgado;
- `>>resto` e continuacao: emenda no que a placa ja tinha. Escreva o comeco, espere a placa
  atualizar, abra de novo, apague e cole `>>` mais o proximo pedaco, quantas vezes precisar;
- o servidor guarda o que foi digitado na propria placa: confirmar o texto cortado sem mexer nao
  estraga, e escrever outra coisa por cima devolve a placa ao jogador.

Desenho com nome e abreviacao de casa vao em `custom.txt`, na mesma pasta do catalogo, no mesmo
formato (`I<TAB>nome<TAB>rich text com \n`, `A<TAB>apelido<TAB>nome`, `M<TAB>abreviacao<TAB>texto`).
Ele e lido depois do catalogo gerado e ganha dele, entao regerar os icones nao apaga nada.

O catalogo sai dos arquivos do **seu** jogo e carrega arte dele: nao entra no repositorio.

```sh
# 1. gerar, em qualquer maquina com o jogo instalado (~1 min, ~5 MB)
pip install -r tools/sign-icons/requirements.txt
python tools/sign-icons/build_catalog.py --game "<Steam>/steamapps/common/Valheim/valheim_Data" --out catalog.txt
# 2. por no volume de config
docker compose exec valheim mkdir -p /config/sign-icons
docker compose cp catalog.txt valheim:/config/sign-icons/catalog.txt
# 3. VALHEIM_SIGN_ICONS_CATALOG=/config/sign-icons/catalog.txt no .env, compilar o plugin e recriar
docker compose --profile plugin run --rm plugin-build
docker compose up -d                      # derruba quem estiver jogando
```

`--px` escolhe 16, 24 (padrao) ou 32 pixels de lado: mais pixels, mais texto por placa (em 24,
mediana de ~1,6 mil caracteres e maximo de ~3 mil). O material da placa e iluminado pela cena e o
icone some no escuro; por padrao o gerador usa o material sem iluminacao do jogo e o plugin mostra
as cores a 70% (`--brightness`, que vai no catalogo como `P brightness`; cor cheia estoura em bloom
a noite). `--material ""  --brightness 1` volta ao
material da placa. `--overlap` e quanto cada bloco invade o vizinho para fechar a costura entre os
pixels. O rotulo sai no preset `Valheim-Norse - Outline` do jogo, tambem sem iluminacao: letra clara
com contorno preto, que le de dia e de noite (`--label-material`, `--label-color`; material vazio =
texto normal da placa, que some no escuro).

Trocar `catalog.txt` ou `custom.txt` com o servidor no ar basta: o plugin confere os arquivos a
cada 5 s, recarrega e redesenha as placas, sem restart.
Conferir em `/metrics`: `valheim_sign_icons_catalog_entries`, `valheim_sign_icons_catalog_reloads_total`,
`valheim_sign_icons_signs`, `valheim_sign_icons_long_texts`, `valheim_sign_icons_changes_total` e
`valheim_exporter_patch_ok{target="ZDO.Deserialize"}`.
Rollback = apagar a variavel e recriar; as placas ja desenhadas ficam como estao.

### Pedidos de entrada na whitelist

Com `permittedlist.txt` preenchido, so os SteamIDs dele entram, e quem esta fora recebe "not in
whitelist" sem o admin ficar sabendo quem era. O plugin registra cada entrada barrada com nome e
SteamID (o jogo recebe os dois antes de recusar), numera como pedido, avisa os admins online no
canto da tela e no console F5, e deixa liberar de duas formas:

- **No jogo, sem mod no cliente**: `/unban <numero>` no chat ou no F5 (aceita tambem SteamID ou
  comeco do nome). `unban` e comando de rede que todo cliente manda ao servidor; argumento que nao
  e pedido aberto segue para o unban de verdade. `/banned` lista os pedidos junto dos banidos. Chat
  comum nao serve de comando: o cliente so envia a fala para os outros jogadores, entao com o admin
  sozinho ela nunca chega ao servidor.
- **Na pagina**, com `VALHEIM_ACCESS_PORT` no `.env` (ex.: `9781`): lista pedidos (Liberar /
  Ignorar) e a whitelist com o ultimo nome de cada SteamID. **Nao tem login.** A porta nao e
  publicada no host; exponha so atras de um proxy com autenticacao.

Liberar grava no `permittedlist.txt`, que o jogo rele sozinho a cada ~10 s. O registro fica em
`/config/access-requests.tsv` ao lado das listas. Whitelist vazia deixa todo mundo entrar, e ai
nada vira pedido. Conferir em `/metrics`: `valheim_access_pending`, `valheim_access_denied_total`
e `valheim_exporter_patch_ok` dos alvos `ZNet.IsAllowed`, `ZNet.RPC_Unban` e `ZNet.RPC_PrintBanned`.

### Slots de bau reservados (opt-in)

`VALHEIM_SLOT_MARKS=1` no `.env`. Um slot marcado guarda o lugar do item: quando a pilha sai inteira, fica
uma pilha de **zero** no slot, e o "Guardar pilhas" (place stacks) do jogo, sem mod, enche aquele slot,
porque so pede um item de mesmo nome com espaco.

- **Marcar** exige o mod de cliente Ghost Stacks (Alt + clique no slot). Quem nao marca joga vanilla.
- As marcas ficam no ZDO do bau, chave `valheim-server.slot_marks` (`1|x,y,prefab,qualidade,nivel|...`).
- O servidor recoloca a pilha de zero ~3 s depois que qualquer jogador fecha o bau (espera `InUse` = 0 e o
  ZDO parado), tira pilha de zero sem marca e desfaz a marca se outro item ocupou o slot. Com o plugin
  desligado as marcas ficam paradas; a varredura do boot pega o atraso.
- So empilhaveis: pilha zero de arma seria copia usavel. Lapide e saco de loot ficam de fora.
- Para cliente vanilla a pilha de zero e item comum: arrastar ou "Pegar tudo" leva um item `0` para o
  inventario, que nao cai no chao e some no proximo place stacks.
- Formato de item diferente de 108..109 (update do jogo) nao e mexido: conta em `valheim_slot_marks_skipped_total`.

### Combustivel de fogo (opt-in)

`VALHEIM_FIRE_FUEL_FACTOR=5` no `.env`: tochas, braseiros, fogueiras, lareiras e tudo que usa o
`Fireplace` do jogo gasta 5 vezes menos, sem mod no cliente. Vazio = jogo.

- Quem desconta o combustivel e o cliente dono do ZDO. O servidor ve cada `fuel`/`lastTime` que chega,
  anota o que o fogo mais lento nao teria gasto e soma no ZDO de 5 em 5 minutos por fogo. O contador
  sobe devagar e da um pulo quando a devolucao entra.
- Devolve no maximo o que o relogio (`lastTime`) justifica: fogo molhado, desligado ou reabastecido
  na janela nao ganha nada.
- Quem volta depois de horas longe encontra o fogo com o que restaria no ritmo lento; se ele tinha
  apagado por falta, o servidor devolve na hora e ele reacende.
- A escrita vai com a revisao do ZDO 1000 a frente, para nao perder a corrida com o dono (que grava a
  cada 2 s). O dono adota o ZDO inteiro do servidor e segue dali.
- Devolucao pendente vive na memoria: restart perde ate 5 minutos de devolucao por fogo.
- `valheim_fire_fuel_refunded_total` (unidades devolvidas), `_writes_total`, `_relit_total`, `_pending`.

### Mapa (opt-in)

O Geomap do Grafana desenha pontos sobre tiles XYZ em Web Mercator, e o plugin fala essa lingua: o
mundo vira um quadrado de 1,40625 grau em volta de (0, 0), com 1 grau = 17476,27 m (`lon = x / 17476,27`,
`lat = z / 17476,27`, norte = +z). O numero faz o mapa da mesa (2048 px de 12 m) cair exato em 8x8 tiles
no zoom 11; perto do Equador o Mercator e linear (0,3 m de erro na borda do mundo).

- **Metricas** (sempre): `valheim_player_position_meters{axis}`, `valheim_map_pin_info`,
  `valheim_portal_info`, `valheim_bed_info`, `valheim_map_table_info`, `valheim_player_deaths_total`
  (tumulo novo), `valheim_lag_reports_by_zone_total`, `valheim_zone_zdos` (300 zonas mais cheias) e
  `valheim_zone_traffic_bytes_per_second` (100 mais caras da ultima janela de 5 min), `valheim_player_biome`
  (valor = `Heightmap.Biome`). Tudo que tem lugar fixo leva `x`, `z`, `lat` e `lon`; a posicao do jogador divide
  por 17476,27 na consulta. Para o clique: pin com `kind` (tipo em portugues) e `checked`; portal com
  `connected`, `target` e `distance_m` (o par do TeleportWorld); zona com `top` (os 3 prefabs que mais pesam).
- **Sem spoiler**: pins e calor de ZDO so do que as mesas de cartografia mostram (uniao de todas as mesas,
  relida a cada minuto). Jogador, morte e lag aparecem onde acontecem.
- **Fundo** (`VALHEIM_MAP_DIR=/config/map`): o plugin desenha `tiles/{z}/{x}/{y}.png` (zoom 9 a 17, 0,19 m/px no
  17) com o gerador de mundo do proprio servidor, transparente fora do explorado, e as **construcoes por cima**:
  cada peca com criador (e planta crescida) vira o retangulo dos colliders do prefab, girado pelo ZDO, na cor do
  material (madeira, madeira nobre, madeira de Yggdrasil, pedra, marmore, grausten, ferro, antigo, gelo), com
  moveis/estacoes, plantacao e barco em cores proprias. O mais alto cobre o mais baixo (telhado por cima do
  piso). Peca so a partir do zoom 13. Terra mexida ainda nao aparece.
- **Desenho uma vez por dia**, pensado para nao pesar no jogo: a varredura das pecas roda na thread principal
  aos pedacos (`VALHEIM_MAP_SCAN_BUDGET_MS` por frame, padrao 1) e o desenho numa thread de prioridade minima que
  dorme na proporcao do trabalho (`VALHEIM_MAP_DRAW_DUTY`, padrao 0.5 = meio nucleo, o dobro do tempo). Horario em
  `VALHEIM_MAP_DRAW_AT` (`HH:MM`, hora do container, padrao 04:30); zoom maximo em `VALHEIM_MAP_MAX_ZOOM` (11 a 18,
  padrao 17; cada zoom a mais ~4x tiles e tempo). So redesenha tile cuja assinatura mudou (estilo, pixels da mesa
  que ele alcanca, pecas dentro dele), guardada em `tiles.book`. Pedir um desenho fora de hora: criar
  `draw.now` na pasta; `draw.pending` marca desenho interrompido e ele e retomado no boot seguinte.
  Custo em `valheim_map_draw_*` (tempo por fase, tiles por resultado, GC no periodo, pior tile) e
  `valheim_map_piece_scan_*` (tempo e pior frame na thread principal); `valheim_map_pieces{kind}` conta as pecas.
  `players.tsv` guarda SteamID -> nome para o autor dos pins.
- **Servir os tiles**: o navegador busca direto, entao eles precisam estar na mesma origem do Grafana, por
  exemplo montando `map/tiles` do volume em `public/img/valheim-map` do Grafana e usando
  `/public/img/valheim-map/{z}/{x}/{y}.png` como camada XYZ.

## Dados

Mundo e config em volumes nomeados (`valheim-server_config`, `valheim-server_server`), nunca no repo.
Backup diario em `/config/backups` dentro do volume — copiar pra fora do host.

Save em `/config/worlds_local/<WORLD_NAME>/` — desde a 1.0 o mundo e um diretorio de
geracoes fragmentadas (`_main.<n>.fwl2` + chunks), e o servidor escreve `_main.<n>.ok`
por ultimo: marcador presente = geracao inteira no disco. O formato antigo (`.db`/`.fwl`
soltos em `worlds_local/`) ainda e lido. Copiar mundo pela metade corrompe — use o zip
do backup, que fecha em cima do marcador.

## Nao versionar

`.env`, save do mundo (`worlds_local/`, `.db`/`.fwl`/`.fwl2`), `adminlist.txt`/`permittedlist.txt`/`access-requests.tsv` (SteamID64 e identificador de terceiro).

## Firewall

Se subiu e ninguem conecta, suspeitar da camada de firewall antes do jogo.

## Alternativa sem abrir porta

`-crossplay` usa relay PlayFab (entra por codigo, sem port forward), ao custo de latencia. Nao habilitado aqui.
