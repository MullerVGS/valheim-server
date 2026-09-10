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
