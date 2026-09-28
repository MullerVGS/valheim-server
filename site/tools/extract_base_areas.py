#!/usr/bin/env python3
"""Extrai da instalacao do jogo as pecas que abrem area de base (EffectArea com PlayerBase) e o raio dela.

Nenhum spawn natural nasce dentro dessa area (SpawnSystem.IsSpawnPointGood). O raio e o do collider
de gatilho no mesmo GameObject do EffectArea (esfera ou capsula de pe), na escala do prefab.
Sai em base-areas.json (prefab -> raio em metros), que o site le para desenhar onde nao nasce monstro.

    python3 tools/extract_base_areas.py --game "/mnt/d/SteamLibrary/steamapps/common/Valheim" --out base-areas.json
"""
import argparse
import json
import os
import struct
import sys

import UnityPy

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools", "sign-icons"))
import build_catalog as catalog  # noqa: E402

PLAYER_BASE = 4


def effect_area_type(raw):
    """Tipo do EffectArea no MonoBehaviour cru: GameObject(12) Enabled(4) Script(12) Name(string) m_type(u8)."""
    if len(raw) < 33:
        return None
    size = struct.unpack_from("<i", raw, 28)[0]
    at = 32 + ((size + 3) & ~3)
    return raw[at] if 0 <= size < 256 and at < len(raw) else None


def script_id(raw):
    return struct.unpack_from("<q", raw, 20)[0]


def components(go):
    for comp in go.m_Components:
        yield comp.read() if hasattr(comp, "read") else comp.component.read()


def scale_xz(transform):
    s = transform.m_LocalScale
    return max(abs(s.x), abs(s.z))


def areas(go, scale, script):
    """(nome do filho, raio) de cada EffectArea PlayerBase na arvore."""
    transform = None
    collider = None
    found = False
    for c in components(go):
        kind = c.object_reader.type.name
        if kind == "Transform":
            transform = c
        elif kind in ("SphereCollider", "CapsuleCollider") and c.m_IsTrigger:
            collider = c
        elif kind == "MonoBehaviour":
            raw = c.object_reader.get_raw_data()
            if len(raw) > 28 and script_id(raw) == script:
                t = effect_area_type(raw)
                found = found or (t is not None and t & PLAYER_BASE)
    if transform is None:
        return
    scale = scale * scale_xz(transform)
    if found and collider is not None:
        yield go.m_Name, round(collider.m_Radius * scale, 2)
    for child in transform.m_Children:
        yield from areas(child.read().m_GameObject.read(), scale, script)


def find_script(env):
    """PathID do script EffectArea: o do filho PlayerBase da bancada."""
    bench = env.container["Assets/GameElements/Pieces/piece_workbench.prefab"].read()
    stack = [bench]
    while stack:
        go = stack.pop()
        comps = list(components(go))
        if go.m_Name == "PlayerBase":
            for c in comps:
                if c.object_reader.type.name == "MonoBehaviour":
                    return script_id(c.object_reader.get_raw_data())
        for c in comps:
            if c.object_reader.type.name == "Transform":
                stack += [ch.read().m_GameObject.read() for ch in c.m_Children]
    raise SystemExit("EffectArea da bancada nao encontrado")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    data = os.path.join(args.game, "valheim_Data")
    manifest = catalog.read_manifest(data)
    prefabs = {p: b for p, b in manifest.items() if p.endswith(".prefab")}
    home = manifest["Assets/GameElements/Pieces/piece_workbench.prefab"]
    bundles = [home] + sorted(set(prefabs.values()) - {home})
    script = None
    out = {}
    for bundle in bundles:
        env = UnityPy.load(catalog.bundle_path(data, bundle))
        script = script or find_script(env)
        for path, obj in env.container.items():
            if path not in prefabs or obj.type.name != "GameObject":
                continue
            root = obj.read()
            found = list(areas(root, 1.0, script))
            if found:
                out[root.m_Name] = max(r for _, r in found)
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(dict(sorted(out.items())), handle, indent=1)
        handle.write("\n")
    print(f"{len(out)} prefabs com area de base -> {args.out}")


if __name__ == "__main__":
    main()
