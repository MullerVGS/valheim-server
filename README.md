# valheim-server

Stack Docker de servidor dedicado Valheim. Motor neutro: config e segredo ficam no host, fora do git.

## Subir

```sh
cp .env.example .env && chmod 600 .env   # editar
docker compose up -d
docker compose logs -f
```

1º boot baixa o servidor (app Steam 896660, ~3 GB) — leva alguns minutos até aceitar conexão.

## Fatos

- Teto do jogo: **10 jogadores**.
- **UDP 2456-2457**. Trafego de jogo, direto no host — nao passa por reverse proxy.
- CPU: single-thread bound. 1 core rapido > muitos cores.
- RAM: ~3-4 GB vanilla c/ mundo maduro. `MEM_LIMIT` existe pra nao derrubar vizinho de host.
- `SERVER_PASS`: min 5 chars, nao pode estar contido no `SERVER_NAME`.
- Mods (BepInEx) passam de 8 GB. Nao cabem em host compartilhado.

## Dados

Mundo e config em volumes nomeados (`valheim-server_config`, `valheim-server_server`), nunca no repo.
Backup diario em `/config/backups` dentro do volume — copiar pra fora do host.

Save fica em `/config/worlds_local/<WORLD_NAME>.db|.fwl`.

## Nao versionar

`.env`, save do mundo (`.db`/`.fwl`), `adminlist.txt`/`permittedlist.txt` (SteamID64 e identificador de terceiro), IP/regra de firewall do host real.

## Firewall

Se subiu e ninguem conecta, suspeitar da camada de firewall antes do jogo: em host com regra em `DOCKER-USER`, porta publicada casa por `--ctorigdstport`, nao `--dport`.

## Alternativa sem abrir porta

`-crossplay` usa relay PlayFab (entra por codigo, sem port forward), ao custo de latencia. Nao habilitado aqui.
