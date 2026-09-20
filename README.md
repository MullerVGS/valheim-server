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
- Tres crons diarios, nao dois: alem do backup e do update declarados no compose, a
  imagem cria um **restart as 05:10** por default proprio, sem variavel no ambiente.
  E gateado por `valheim-is-idle`, entao nao derruba ninguem conectado.

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
icone some no escuro; por padrao o gerador usa o material sem iluminacao do jogo com as cores a
70% (`--brightness`; cor cheia estoura em bloom a noite). `--material ""  --brightness 1` volta ao
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

## Dados

Mundo e config em volumes nomeados (`valheim-server_config`, `valheim-server_server`), nunca no repo.
Backup diario em `/config/backups` dentro do volume — copiar pra fora do host.

Save em `/config/worlds_local/<WORLD_NAME>/` — desde a 1.0 o mundo e um diretorio de
geracoes fragmentadas (`_main.<n>.fwl2` + chunks), e o servidor escreve `_main.<n>.ok`
por ultimo: marcador presente = geracao inteira no disco. O formato antigo (`.db`/`.fwl`
soltos em `worlds_local/`) ainda e lido. Copiar mundo pela metade corrompe — use o zip
do backup, que fecha em cima do marcador.

## Nao versionar

`.env`, save do mundo (`worlds_local/`, `.db`/`.fwl`/`.fwl2`), `adminlist.txt`/`permittedlist.txt` (SteamID64 e identificador de terceiro).

## Firewall

Se subiu e ninguem conecta, suspeitar da camada de firewall antes do jogo.

## Alternativa sem abrir porta

`-crossplay` usa relay PlayFab (entra por codigo, sem port forward), ao custo de latencia. Nao habilitado aqui.
