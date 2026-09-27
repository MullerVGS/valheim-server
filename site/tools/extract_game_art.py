#!/usr/bin/env python3
"""Extrai da instalacao do jogo a arte do mapa: texturas do material `minimap` (shader
Custom/mapshader), cores de bioma do componente Minimap, icones de pin e as fontes nordicas.

A arte e da Iron Gate: fica em public/game/, fora do git. Precisa de UnityPy e Pillow >= 10
(venv), e le os bundles de valheim_Data/StreamingAssets/SoftRef/Bundles.

    python3 tools/extract_game_art.py --game "/path/to/Valheim" --out public/game
"""
import argparse
import glob
import json
import os
from concurrent.futures import ProcessPoolExecutor

import UnityPy

# Bundles gigantes (malhas, terreno) nao tem nada do mapa e estouram a memoria.
MAX_BUNDLE = 400 * 1024 * 1024
ICON_PREFIX = "mapicon_"
FONTS = {"Norse", "Norsebold"}


def scan(path):
    """Nomes que interessam num bundle e os CABs que ele carrega."""
    found = []
    cabs = []
    try:
        env = UnityPy.load(path)
        for bundle in env.files.values():
            cabs.extend(k.lower() for k in getattr(bundle, "files", {}).keys())
        for obj in env.objects:
            kind = obj.type.name
            if kind not in ("Material", "Sprite", "Font"):
                continue
            try:
                name = obj.peek_name() or ""
            except Exception:
                continue
            if (kind == "Material" and name == "minimap") or (kind == "Sprite" and name.startswith(ICON_PREFIX)) \
                    or (kind == "Font" and name in FONTS):
                found.append((kind, name, obj.path_id))
    except Exception:
        pass
    return path, found, cabs


def rgb(c):
    return [round(c["r"], 4), round(c["g"], 4), round(c["b"], 4)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    bundles_dir = os.path.join(args.game, "valheim_Data", "StreamingAssets", "SoftRef", "Bundles")
    paths = [p for p in glob.glob(os.path.join(bundles_dir, "*")) if os.path.getsize(p) <= MAX_BUNDLE]

    where = {}
    cab_owner = {}
    with ProcessPoolExecutor(6) as ex:
        for path, found, cabs in ex.map(scan, paths):
            for kind, name, pid in found:
                where[(kind, name)] = (path, pid)
            for cab in cabs:
                cab_owner[cab] = path
    if ("Material", "minimap") not in where:
        raise SystemExit("material minimap nao encontrado")

    out = args.out
    os.makedirs(os.path.join(out, "icons"), exist_ok=True)
    os.makedirs(os.path.join(out, "fonts"), exist_ok=True)

    # Material + tudo de que ele depende no mesmo ambiente, para resolver as referencias externas.
    mat_path, mat_id = where[("Material", "minimap")]
    env = UnityPy.load(mat_path)
    objs = {o.path_id: o for o in env.objects}
    material = objs[mat_id].read()
    extra = set()
    for _, t in material.m_SavedProperties.m_TexEnvs:
        if t.m_Texture.m_FileID:
            ext = material.assets_file.externals[t.m_Texture.m_FileID - 1].path.split("/")[-1].lower()
            if ext in cab_owner:
                extra.add(cab_owner[ext])
    if extra:
        env = UnityPy.load(mat_path, *sorted(extra))
        objs = {o.path_id: o for o in env.objects}
        material = objs[mat_id].read()

    textures = {}
    for name, t in material.m_SavedProperties.m_TexEnvs:
        if not t.m_Texture.m_PathID:
            continue
        tex = t.m_Texture.deref().read()
        file = name.lstrip("_").lower() + ".png"
        tex.image.save(os.path.join(out, file))
        # O jogo roda em espaco de cor linear: textura sRGB e decodificada na amostragem.
        textures[name] = {"file": file, "size": [tex.m_Width, tex.m_Height], "srgb": tex.m_ColorSpace == 1,
                          "mips": tex.m_MipCount > 1, "filter": tex.m_TextureSettings.m_FilterMode}
    meta = {
        "textures": textures,
        "colors": {n: [round(c.r, 4), round(c.g, 4), round(c.b, 4), round(c.a, 4)]
                   for n, c in material.m_SavedProperties.m_Colors},
        "floats": {n: v for n, v in material.m_SavedProperties.m_Floats},
    }

    # Cores de bioma do componente Minimap (as do codigo sao outras: o prefab sobrescreve).
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if "m_meadowsColor" in tree:
            meta["biomes"] = {k[2:-5]: rgb(v) for k, v in tree.items() if k.endswith("Color") and isinstance(v, dict)}
            meta["minimap"] = {k: tree[k] for k in ("m_textureSize", "m_pixelSize", "m_minZoom", "m_maxZoom")}
            break

    icons = []
    for (kind, name), (path, pid) in sorted(where.items()):
        if kind != "Sprite":
            continue
        sprite = {o.path_id: o for o in UnityPy.load(path).objects}[pid].read()
        sprite.image.save(os.path.join(out, "icons", name[len(ICON_PREFIX):] + ".png"))
        icons.append(name[len(ICON_PREFIX):])
    meta["icons"] = icons

    for font in sorted(FONTS):
        if ("Font", font) not in where:
            continue
        path, pid = where[("Font", font)]
        data = {o.path_id: o for o in UnityPy.load(path).objects}[pid].read().m_FontData
        with open(os.path.join(out, "fonts", font + ".otf"), "wb") as f:
            f.write(bytes(data))

    with open(os.path.join(out, "art.json"), "w") as f:
        json.dump(meta, f, indent=1, ensure_ascii=False)
    print(f"{len(textures)} texturas, {len(icons)} icones, fontes em {out}/fonts")


if __name__ == "__main__":
    main()
