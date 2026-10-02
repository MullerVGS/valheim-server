# Plugin

Server-only BepInEx plugin (`plugin/`). Clients stay vanilla; achievements unaffected
(`Game.isModded` is local to the server process). Every feature is off unless its variable is set.
Changing a variable = `docker compose up -d` (recreates; kicks players) + `plugin-build` + `restart valheim`.

Each feature exposes `valheim_exporter_patch_ok{target}`; 0 = a game update changed that method, feature off,
game unaffected.

## Metrics

`/metrics` on `valheim:9780`, not published. Attach the container to your Prometheus/vmagent network and
scrape it (5 s shows raids well).

- per player: ping, connection quality, bytes/s vs Steam cap, Steam queue, skipped send cycles, ZDOs sent/received, mobs simulated
- server: FPS (game targets 30), frame time, per-subsystem time, saves, disconnects, GC
- raids, RPCs per method, ZDO bandwidth per prefab and per 64 m zone (top 10 objects / 5 zones logged every 5 min)
- positions with `x`/`z` and `lat`/`lon` (Grafana Geomap): players, cartography-table pins, portals, beds, deaths, lag reports

## Network tuning

The server sends ZDOs to **one player per frame**, ≤10240 bytes, at 30 FPS: each player gets at most
`10240 × 30 / (N+1)` B/s (34 KB/s with 8). Full packets = mobs teleporting, slow pickups, with CPU idle.

| Variable | Game | Range |
| --- | --- | --- |
| `VALHEIM_SERVER_FPS` | 30 | 30–360 |
| `VALHEIM_ZDO_SEND_LIMIT_BYTES` | 10240 | 10240–65536 |
| `VALHEIM_STEAM_SEND_RATE_BYTES` | 153600 | 153600–1048576 |

FPS × limit multiply. Raise all three together: Steam caps each connection, so raising the limit alone just
grows the queue. Every player needs that download bandwidth. Client→server is unchanged.

## Stalls

- `VALHEIM_DEFER_ASSET_UNLOAD=1`: the hourly `Resources.UnloadUnusedAssets` (0.7–0.9 s freeze) waits until the server is empty.
- `VALHEIM_OWNER_HYSTERESIS=on|measure`: pieces, chests, plants and tames change owner only when they leave
  what the owner has loaded, instead of at 1.5 zones. Fixes ping-pong on bases at zone borders.
  `measure` = vanilla, only counts. Falls back to vanilla on error.

## Sign icons

Signs are TextMeshPro rich text; the 50-char limit is client input only.

```sh
# on a machine with the game (~1 min, ~5 MB; game art, keep out of git)
pip install -r tools/sign-icons/requirements.txt
python tools/sign-icons/build_catalog.py --game "<Steam>/steamapps/common/Valheim/valheim_Data" --out catalog.txt
docker compose exec valheim mkdir -p /config/sign-icons
docker compose cp catalog.txt valheim:/config/sign-icons/catalog.txt
# VALHEIM_SIGN_ICONS_CATALOG=/config/sign-icons/catalog.txt
```

Syntax on a sign:

- `:wood:` / `:MushroomYellow:` / `:yellow mushroom:` — prefab or EN/PT name; case, accents, spaces ignored. Unknown → `:weed:`.
- `Wood :wood:` — label on the sign (≤10 chars big, ≤22 small, longer only on hover).
- `:wood 50%:` brightness; `<size=4>:wood:` icon size (default 7.6, max 18); `<#fc6>` label color.
- `{u}` — abbreviation for the unlit material (visible at night).
- `>>more` — appends to the existing text, to go past 50 chars.
- Writing anything else returns the sign to the player.

`custom.txt` next to the catalog adds your own drawings/abbreviations (same format, wins over catalog).
Both files hot-reload every 5 s. Generator flags: `--px 16|24|32`, `--brightness`, `--material`, `--overlap`.

## Whitelist requests

With `permittedlist.txt` filled, denied joins are logged with name + SteamID, numbered, and announced to
online admins. Approve with `/unban <n>` (chat or F5; also SteamID or name prefix). `/banned` lists them.
`VALHEIM_ACCESS_PORT=9781` adds a web page — **no login**, keep it behind an authenticating proxy.
Log: `/config/access-requests.tsv`.

## Reserved chest slots

`VALHEIM_SLOT_MARKS=1`. A marked slot keeps a zero-size stack when emptied, so vanilla "Place stacks"
refills it. Marking needs the Ghost Stacks client mod (Alt+click); everyone else plays vanilla.
Stackables only. Vanilla clients may drag a `0` item out; it vanishes on next place stacks.

## Fire fuel

`VALHEIM_FIRE_FUEL_FACTOR=N` (2–100): anything using `Fireplace` burns N× slower. The server refunds fuel
every 5 min per fire, bounded by the fire's own clock (wet/off/refilled fires get nothing). Restart loses
≤5 min of pending refund.

## Summon names

`VALHEIM_SUMMON_NAMES=1`: summons (Dead Raiser skeletons, any tame born with a random name) reuse names the
player gave earlier — up to 30 per character in `summon-names.tsv` next to the world.

## Map

Positions use Web Mercator so Grafana Geomap works: `lon = x / 17476.27`, `lat = z / 17476.27`.
Pins and zone heat only cover what cartography tables reveal (no spoilers); players, deaths and lag show everywhere.

`VALHEIM_MAP_DIR=/config/map` writes raw data for the site (formats in `plugin/src/Map/MapFiles.cs`):

- `terrain.bin` — whole world, 2048² px of 12 m, once per seed, low-priority thread (≤ half a core). Spoiler: the site only sends revealed parts.
- `explored.bin` — union of all tables, rewritten on change.
- `pieces.bin` — buildings in revealed areas, rescanned every `VALHEIM_MAP_PIECES_MINUTES` (60) with
  `VALHEIM_MAP_SCAN_BUDGET_MS` (1) per frame; touch `pieces.now` to force.

## World data

Since 1.0 a world is a directory `worlds_local/<WORLD_NAME>/` of `_main.<n>.fwl2` + chunks;
`_main.<n>.ok` is written last. Copying mid-save corrupts — use the backup zip.
