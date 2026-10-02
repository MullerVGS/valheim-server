# valheim-server

Docker Compose for a Valheim dedicated server, plus an optional server-side BepInEx plugin and map site.
Players need no mods.

Built on [lloesche/valheim-server](https://github.com/lloesche/valheim-server-docker) (pinned tag).

## Quick start

```sh
cp .env.example .env && chmod 600 .env   # edit name, world, password
docker compose up -d
docker compose logs -f                   # 1st boot downloads ~3 GB
```

Open **UDP 2456-2457** on the host. Nothing else is published.

## What you get

| Layer | Needs | What |
| --- | --- | --- |
| Server | — | daily backup, update and idle-only restart (`*_CRON`), whitelist/admin by SteamID64, world modifiers via `SERVER_ARGS` |
| Plugin | `BEPINEX=true` | Prometheus `/metrics` on `valheim:9780` (not published) + opt-in tweaks below |
| Site | game art from your install; plugin + Prometheus-compatible DB for full data | game-style map, player/base pages, sign editor |

## Plugin

```sh
# BEPINEX=true in .env, then:
docker compose up -d
docker compose --profile plugin run --rm plugin-build
docker compose restart valheim
```

Check: `valheim_exporter_patch_ok` = 1. A game update that breaks a patch disables only that feature.

Opt-in, all off when empty (set in `.env`, then `docker compose up -d`):

| Variable | Effect |
| --- | --- |
| `VALHEIM_SERVER_FPS`, `VALHEIM_ZDO_SEND_LIMIT_BYTES`, `VALHEIM_STEAM_SEND_RATE_BYTES` | more sync bandwidth per player (less mob teleporting) |
| `VALHEIM_DEFER_ASSET_UNLOAD=1` | hourly 0.8 s freeze waits for empty server |
| `VALHEIM_OWNER_HYSTERESIS=on` | stops ownership ping-pong on bases at zone borders |
| `VALHEIM_SIGN_ICONS_CATALOG` | `:wood:` on a sign draws the item icon; long sign text |
| `VALHEIM_ACCESS_PORT` | whitelist join requests page (**no auth** — proxy it) |
| `VALHEIM_SLOT_MARKS=1` | reserved chest slots (marking needs the Ghost Stacks client mod) |
| `VALHEIM_FIRE_FUEL_FACTOR=N` | fires burn N× slower |
| `VALHEIM_SUMMON_NAMES=1` | summons come back with the name you gave them |
| `VALHEIM_MAP_DIR` | writes map data for the site |

Whitelist join requests work without the page: `/unban <n>` in game chat.

Details, metrics and limits: [docs/plugin.md](docs/plugin.md).

## Site

```sh
docker compose --profile site up -d --no-deps site   # serves :8080 in the container
```

Needs game art extracted from your install. Better with `VALHEIM_MAP_DIR` (buildings, live terrain) and
`SITE_VM_URL` (Prometheus-compatible DB scraping the plugin: players, trails). Not published: put it behind
your reverse proxy.
See [site/README.md](site/README.md).

## Data

- Volumes: `config` (worlds, lists, backups — keep it), `server` (game binaries — disposable).
- Backups: `/config/backups`, zipped daily. Copy them off the host.
- Never copy a world while running: use the backup zip.

## Gotchas

- Max 10 players. CPU-bound on one core.
- `SERVER_PASS`: ≥5 chars, not contained in `SERVER_NAME`.
- Recreating the container wipes BepInEx plugins: rerun `plugin-build` + `restart valheim`.
- `SERVER_ARGS -setkey` accepts anything; a key outside the in-game World Modifiers UI disables achievements.
  Start with `-resetmodifiers` so the line is declarative.
- Nobody connects? Check the firewall first.

## License

MIT
