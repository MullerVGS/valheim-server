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

Dificuldade se ajusta por `SERVER_ARGS`, sem recriar o mundo:

```sh
SERVER_ARGS=-modifier deathpenalty casual
```

Chaves: `combat`, `deathpenalty`, `resources`, `raids`, `portals`. Existem tambem
`-preset <nome>`, que mexe em todos os eixos de uma vez, e `-resetmodifiers`.

Vale para mundo ja existente e preserva o progresso: no load o jogo limpa so as
chaves de modificador, e as de boss derrotado ficam fora desse intervalo. Tirar o
argumento depois **nao reverte** -- a chave fica gravada no mundo, reverter e
`-resetmodifiers`.

Nao desliga achievements da plataforma: o jogo marca o mundo como "cheated" apenas
quando acha uma global key que a GUI nao conseguiria setar. Um valor fora do slider
(ex.: `deathpenalty less`) e justamente o que fabrica essa chave -- use so as
combinacoes que o menu do jogo oferece.

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
