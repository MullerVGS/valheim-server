#!/usr/bin/env python3
"""Extrai da instalacao do jogo os itens que podem estar num bau: nome (pt-BR e ingles) e icone.

Sai em public/game/items.json (hash estavel do prefab -> [prefab, nome pt-BR, nome em ingles, indice
do icone, tipo]) e public/game/items.webp (atlas de icones de SIZE px, COLS por linha). O save guarda
o item so pelo hash do prefab; e por ele que o site acha nome e icone. Arte da Iron Gate: fora do git.
Reusa o leitor de bundles do gerador de icones de placa (tools/sign-icons).

    python3 tools/extract_items.py --game "/mnt/d/SteamLibrary/steamapps/common/Valheim" --out public/game
"""
import argparse
import json
import os
import sys

import UnityPy
from PIL import Image

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools", "sign-icons"))
import build_catalog as catalog  # noqa: E402

SIZE = 32
COLS = 32


def stable_hash(text):
    """String.GetStableHashCode do jogo (djb2 duplo, em int32)."""
    a = b = 5381
    for i in range(0, len(text), 2):
        a = ((a * 33) ^ ord(text[i])) & 0xFFFFFFFF
        if i == len(text) - 1:
            break
        b = ((b * 33) ^ ord(text[i + 1])) & 0xFFFFFFFF
    h = (a + b * 1566083941) & 0xFFFFFFFF
    return h - (1 << 32) if h >= 1 << 31 else h


def load_items(game, manifest):
    """prefab -> (token do nome, PathID do icone, tipo), como o load_items do catalogo mais o tipo."""
    prefabs = {p: b for p, b in manifest.items()
               if p.startswith("Assets/GameElements/Items/") and p.endswith(".prefab")}
    items = {}
    for bundle in sorted(set(prefabs.values())):
        env = UnityPy.load(catalog.bundle_path(game, bundle))
        for path, obj in env.container.items():
            if path not in prefabs or obj.type.name != "GameObject":
                continue
            game_object = obj.read()
            for component in game_object.m_Components:
                reader = (component.read() if hasattr(component, "read") else component.component.read()).object_reader
                if reader.type.name != "MonoBehaviour":
                    continue
                try:
                    tree = reader.read_typetree()
                except Exception:
                    continue
                shared = (tree.get("m_itemData") or {}).get("m_shared")
                if not shared or not shared.get("m_icons"):
                    continue
                items[game_object.m_Name] = (shared.get("m_name", ""), shared["m_icons"][0].get("m_PathID"),
                                             shared.get("m_itemType", 0))
                break
    return items


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", required=True, help="pasta do jogo (a que tem valheim_Data)")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    data = os.path.join(args.game, "valheim_Data")
    manifest = catalog.read_manifest(data)
    icons, sprite_names = catalog.load_icons(data, manifest)
    items = load_items(data, manifest)
    pt = catalog.load_localization(data, ["Portuguese_Brazilian"])
    en = catalog.load_localization(data, ["English"])

    table, tiles, tile_of = {}, [], {}
    for prefab, (token, path_id, item_type) in sorted(items.items()):
        sprite = sprite_names.get(path_id)
        if sprite is None:
            continue
        if sprite not in tile_of:
            tile_of[sprite] = len(tiles)
            tiles.append(icons[sprite])
        key = token.lstrip("$")
        table[str(stable_hash(prefab))] = [prefab, (pt.get(key) or [""])[0], (en.get(key) or [prefab])[0],
                                           tile_of[sprite], item_type]

    rows = (len(tiles) + COLS - 1) // COLS
    atlas = Image.new("RGBA", (COLS * SIZE, rows * SIZE))
    for i, image in enumerate(tiles):
        icon = image.convert("RGBA")
        icon.thumbnail((SIZE, SIZE), Image.LANCZOS)
        atlas.paste(icon, ((i % COLS) * SIZE + (SIZE - icon.width) // 2, (i // COLS) * SIZE + (SIZE - icon.height) // 2))
    os.makedirs(args.out, exist_ok=True)
    atlas.save(os.path.join(args.out, "items.webp"), quality=90, method=6)
    with open(os.path.join(args.out, "items.json"), "w", encoding="utf-8") as handle:
        json.dump({"size": SIZE, "cols": COLS, "items": table}, handle, ensure_ascii=False, separators=(",", ":"))
    print(f"{len(table)} itens, {len(tiles)} icones")


if __name__ == "__main__":
    main()
