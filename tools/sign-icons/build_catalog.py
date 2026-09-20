#!/usr/bin/env python3
"""Gera o catalogo de icones de placa a partir da SUA instalacao do Valheim.

A placa do jogo e um TextMeshPro com rich text: um icone vira pixel art de texto, um bloco
colorido por pixel. O catalogo sai dos arquivos do jogo de quem roda o script e vai para o
volume de config do servidor; ele carrega arte do jogo e por isso nao entra no repositorio.

    python build_catalog.py --game "<...>/Valheim/valheim_Data" --out catalog.txt
"""
import argparse
import csv
import io
import math
import os
import re
import sys
import unicodedata

import UnityPy
from PIL import Image, ImageDraw

BLOCK = "█"
ITEM_ICONS = "Assets/GameElements/Items/_icons/"

# Medido no jogo: a placa e Bold, e o TMP soma ao avanco de cada caractere
# (normalSpacingOffset + boldSpacing) * tamanho base * 0,01. No Noto Sans JP, que serve o bloco,
# isso e (-2 + 5) * 8 * 0,01 = 0,24 unidade, qualquer que seja o <size>. O <cspace> devolve.
# Vale enquanto o desenho couber na area de texto (18,29 x 8,55), que e quando o auto-size fecha em 8.
BOLD_ADVANCE = 0.24
TEXT_AREA_HEIGHT = 8.55
# A 1a linha reserva a ascendente da fonte e a ultima a descendente: (linhas + 0,45) * pixel.
LINE_OVERHEAD = 0.45

# Visto no jogo: o material da placa e iluminado pela cena (icone some no escuro) e corta a cor pela
# metade. A tag <material> aceita qualquer preset de Resources, e o bloco, que vem de fonte de
# fallback, herda o material corrente. Este e sem iluminacao: a cor sai como escrita, de dia ou de
# noite, e quem dosa o brilho e a propria cor (cheia estoura em bloom; ~0,6 a 0,7 le bem no escuro).
UNLIT_MATERIAL = "Valheim_Fonts/Valheim-Norse"

# O hover do jogo mostra o texto da placa depois de tirar as tags com uma regex que nao aceita
# ponto: <size=2> some, <size=0.317> fica e vale no hover (que tambem e TextMeshPro). Dai as regras:
# tag do desenho sempre com ponto (no hover o icone vira um cisco, em vez de blocos gigantes), tag
# do rotulo sempre sem ponto (no hover ele sai no tamanho normal), e o texto termina devolvendo
# tamanho e espacamento com tags de ponto, senao o "[E] Usar" sai do tamanho do cisco.
HOVER_RESET = "<size=100.0%><cspace=0.0><line-height=100.0%>"
# Comeco de todo texto gerado: e por ele que o plugin reconhece um corte de catalogo antigo.
GENERATED = "<cspace=-0.0>"
# Rotulo a mostra: tamanho 2 (o plugin troca {ls} por 1 quando o rotulo e comprido) e 1 unidade ate
# a primeira linha do desenho. Ascendente da Norsebold, que e quem desenha o rotulo: 0,92 em.
LABEL_SIZE = 2
LABEL_GAP = 1
LABEL_ASCENT = 0.92
NOTO_ASCENT = 1.16
NOTO_DESCENT = 0.288


def normalize(name):
    """Mesma regra do plugin: minusculas, sem acento, so letras e digitos."""
    text = unicodedata.normalize("NFKD", name)
    text = "".join(c for c in text if not unicodedata.combining(c))
    return re.sub(r"[^a-z0-9]", "", text.lower())


# ---------- imagem -> texto ----------

def shrink(image, px):
    """Reduz com alfa pre-multiplicado, senao a borda transparente puxa preto para dentro."""
    image = image.convert("RGBA")
    # recorta no contorno antes de reduzir: icone fino (cardo, espada) aproveita os pixels todos
    box = image.getchannel("A").point(lambda a: 255 if a >= 110 else 0).getbbox()
    if box:
        image = image.crop(box)
    side = max(image.size)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(image, ((side - image.width) // 2, (side - image.height) // 2))
    image = canvas
    src = image.load()
    pre = Image.new("RGBA", image.size)
    dst = pre.load()
    for y in range(image.height):
        for x in range(image.width):
            r, g, b, a = src[x, y]
            dst[x, y] = (r * a // 255, g * a // 255, b * a // 255, a)
    small = pre.resize((px, px), Image.BOX)
    out = Image.new("RGBA", (px, px))
    s, o = small.load(), out.load()
    for y in range(px):
        for x in range(px):
            r, g, b, a = s[x, y]
            o[x, y] = (min(255, r * 255 // a), min(255, g * 255 // a), min(255, b * 255 // a), 255) if a >= 110 else (0, 0, 0, 0)
    return out


def quantize(image, colors, brightness=1.0):
    """Paleta curta alonga as sequencias de mesma cor; depois dosa o brilho e encaixa em #rgb (12 bits)."""
    px = image.load()
    opaque = [(x, y) for y in range(image.height) for x in range(image.width) if px[x, y][3]]
    if not opaque:
        return image
    strip = Image.new("RGB", (len(opaque), 1))
    sp = strip.load()
    for i, (x, y) in enumerate(opaque):
        sp[i, 0] = px[x, y][:3]
    quant = strip.quantize(colors=colors, method=Image.MEDIANCUT, dither=Image.NONE).convert("RGB").load()
    out = image.copy()
    op = out.load()
    for i, (x, y) in enumerate(opaque):
        r, g, b = quant[i, 0]
        op[x, y] = tuple(min(15, round(c * brightness / 17)) * 17 for c in (r, g, b)) + (255,)
    return out


def number(value):
    """Sempre com ponto: e o que faz a tag sobreviver no hover (ver HOVER_RESET)."""
    text = f"{value:.3f}".rstrip("0")
    return text + "0" if text.endswith(".") else text


def to_rich_text(image, units, material="", overlap=0.0, titled=False, label_style=""):
    box = image.getchannel("A").getbbox()
    if box:
        image = image.crop(box)
    width, height = image.size
    # O bloco e maior que o passo da grade: cada um cobre a borda suave do vizinho, que sozinha
    # deixa uma costura fina entre os pixels. O passo (avanco e altura de linha) nao muda.
    glyph = 1 + overlap
    room = TEXT_AREA_HEIGHT * 0.97
    if titled:
        # rotulo em cima: sobra menos altura, e o desenho tem que caber inteiro na tabua para o
        # auto-size da placa fechar em 8 (e dele que sai o 0,24 do BOLD_ADVANCE)
        room -= LABEL_ASCENT * LABEL_SIZE + LABEL_GAP
        pixel = min(units / max(width, height), room / (height - 1 + NOTO_DESCENT * glyph))
        head = f"{GENERATED}{label_style}<size={{ls}}><line-height={LABEL_GAP}>{{label}}\n"
    else:
        # nome escondido (so para o hover): transparente, tamanho 1 e altura de linha 0, o desenho
        # comeca na mesma linha de base; so a ascendente dele conta na altura
        hidden = LABEL_ASCENT * 1
        pixel = min(units / max(width, height),
                    (room - hidden) / (height - 1 + NOTO_DESCENT * glyph),
                    room / (height - 1 + (NOTO_ASCENT + NOTO_DESCENT) * glyph))
        head = f"{GENERATED}<size=1><line-height=0><#0000>{{label}}\n"
    px = image.load()
    parts = [head,
             f"<cspace=-{number(BOLD_ADVANCE + overlap * pixel)}>",
             f"<material={material}>" if material else "",
             f"<line-height={number(pixel)}><size={number(pixel * glyph)}>"]
    current = None
    for y in range(height):
        if y:
            parts.append("\n")
        x = 0
        while x < width:
            key = color_key(px[x, y])
            run = 1
            while x + run < width and color_key(px[x + run, y]) == key:
                run += 1
            if key != current:
                parts.append(f"<#{key}>")
                current = key
            parts.append(BLOCK * run)
            x += run
    parts.append(HOVER_RESET)
    return "".join(parts)


def color_key(pixel):
    if pixel[3] == 0:
        return "0000"
    return "%x%x%x" % (pixel[0] // 17, pixel[1] // 17, pixel[2] // 17)


# ---------- folha padrao ----------

def weed_leaf(side=256):
    """Desenho proprio (nao e asset do jogo): sete foliolos saindo de um ponto, com nervura."""
    image = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    cx, cy = side * 0.5, side * 0.70
    reach = side * 0.66
    leaflets = [(0, 1.0), (-32, 0.90), (32, 0.90), (-62, 0.70), (62, 0.70), (-104, 0.42), (104, 0.42)]
    draw.line([(cx, cy), (cx, side * 0.97)], fill=(70, 110, 40, 255), width=max(3, side // 36))
    for angle, scale in sorted(leaflets, key=lambda l: l[1]):
        rad = math.radians(angle)
        dx, dy = math.sin(rad), -math.cos(rad)
        nx, ny = -dy, dx
        length = reach * scale
        half = length * 0.13
        left, right = [], []
        steps = 24
        for i in range(steps + 1):
            t = i / steps
            w = half * math.sin(math.pi * t ** 0.85) * (1 - 0.25 * t)
            # serrilha: some em 24 px, aparece em 32
            w *= 1 + 0.16 * (1 if i % 2 else -1) * (0 < i < steps)
            bx, by = cx + dx * length * t, cy + dy * length * t
            left.append((bx + nx * w, by + ny * w))
            right.append((bx - nx * w, by - ny * w))
        shade = 0.82 + 0.18 * scale
        body = (int(58 * shade), int(150 * shade), int(52 * shade), 255)
        draw.polygon(left + right[::-1], fill=body)
        light = [(cx + dx * length * (i / steps) + nx * half * 0.45 * math.sin(math.pi * (i / steps) ** 0.85),
                  cy + dy * length * (i / steps) + ny * half * 0.45 * math.sin(math.pi * (i / steps) ** 0.85))
                 for i in range(2, steps - 1)]
        draw.line(light, fill=(int(96 * shade), int(190 * shade), int(80 * shade), 255), width=max(2, side // 64))
        draw.line([(cx, cy), (cx + dx * length * 0.94, cy + dy * length * 0.94)],
                  fill=(30, 92, 34, 255), width=max(2, side // 72))
    return image


# ---------- arquivos do jogo ----------

def read_manifest(game):
    """caminho do asset -> bundle, do manifesto SoftRef."""
    path = os.path.join(game, "StreamingAssets", "SoftRef", "manifest_extended")
    table, bundle = {}, None
    with open(path, encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.strip()
            if line.startswith("bundle:"):
                bundle = line.split(":", 1)[1].strip()
            elif line.startswith("path in bundle:") and bundle:
                table[line.split(":", 1)[1].strip()] = bundle
    return table


def bundle_path(game, name):
    return os.path.join(game, "StreamingAssets", "SoftRef", "Bundles", name)


def load_icons(game, manifest):
    """nome do sprite -> imagem, e PathID -> nome do sprite (e por PathID que o item aponta o icone)."""
    icons, by_path_id = {}, {}
    for bundle in sorted({b for p, b in manifest.items() if p.startswith(ITEM_ICONS)}):
        env = UnityPy.load(bundle_path(game, bundle))
        for path, obj in env.container.items():
            if path.startswith(ITEM_ICONS) and obj.type.name == "Sprite":
                sprite = obj.read()
                icons[sprite.m_Name] = sprite.image
                by_path_id[obj.path_id] = sprite.m_Name
    return icons, by_path_id


def load_items(game, manifest):
    """prefab do item -> (token do nome, nome do sprite do icone)."""
    prefabs = {p: b for p, b in manifest.items()
               if p.startswith("Assets/GameElements/Items/") and p.endswith(".prefab")}
    items = {}
    for bundle in sorted(set(prefabs.values())):
        env = UnityPy.load(bundle_path(game, bundle))
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
                items[game_object.m_Name] = (shared.get("m_name", ""), shared["m_icons"][0].get("m_PathID"))
                break
    return items


def load_localization(game, languages):
    """token -> [nomes], das planilhas de localizacao embutidas no jogo."""
    env = UnityPy.load(os.path.join(game, "resources.assets"))
    names = {}
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        asset = obj.read()
        if not asset.m_Name.lower().startswith("localization"):
            continue
        raw = asset.m_Script
        text = raw if isinstance(raw, str) else bytes(raw).decode("utf-8", "replace")
        rows = csv.reader(io.StringIO(text.lstrip("﻿")))
        header = next(rows, None)
        if not header:
            continue
        columns = [header.index(lang) for lang in languages if lang in header]
        for row in rows:
            if not row or not row[0]:
                continue
            for column in columns:
                if column < len(row) and row[column].strip():
                    names.setdefault(row[0], []).append(row[column].strip())
    return names


# ---------- catalogo ----------

def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", required=True, help="pasta valheim_Data do cliente")
    parser.add_argument("--out", required=True, help="arquivo de catalogo a escrever")
    parser.add_argument("--px", type=int, default=24, help="lado do icone em pixels (16, 24 ou 32)")
    parser.add_argument("--colors", type=int, default=16, help="cores por icone")
    parser.add_argument("--units", type=float, default=7.6, help="lado do icone em unidades da placa (a tabua tem 8,55 de altura)")
    parser.add_argument("--material", default=UNLIT_MATERIAL,
                        help="preset de material do jogo para os blocos; o padrao e sem iluminacao (icone visivel no escuro). Vazio = material da placa, iluminado pela cena")
    parser.add_argument("--brightness", type=float, default=0.7,
                        help="fator de brilho das cores (sem iluminacao, 1 estoura em bloom a noite; com --material vazio use 1)")
    parser.add_argument("--label-unlit", action="store_true",
                        help="rotulo ('Madeira :wood:') tambem no material sem iluminacao, em tom claro; sem isso ele e o texto normal da placa")
    parser.add_argument("--overlap", type=float, default=0.15, help="quanto cada bloco invade o vizinho, em fracao de pixel, para fechar a costura")
    parser.add_argument("--languages", default="English,Portuguese_Brazilian", help="colunas de localizacao que viram apelido")
    parser.add_argument("--preview", help="pasta para gravar PNGs de conferencia")
    args = parser.parse_args()

    manifest = read_manifest(args.game)
    icons, sprite_names = load_icons(args.game, manifest)
    items = load_items(args.game, manifest)
    localization = load_localization(args.game, args.languages.split(","))
    print(f"{len(icons)} icones, {len(items)} itens com icone, {len(localization)} tokens de localizacao", file=sys.stderr)

    texts, titled, aliases, clashes = {}, {}, {}, 0
    label_style = f"<material={UNLIT_MATERIAL}><#a98>" if args.label_unlit else ""

    def render(icon_id, image):
        small = quantize(shrink(image, args.px), args.colors, args.brightness)
        texts[icon_id] = to_rich_text(small, args.units, args.material, args.overlap)
        titled[icon_id] = to_rich_text(small, args.units, args.material, args.overlap, titled=True, label_style=label_style)
        if args.preview:
            os.makedirs(args.preview, exist_ok=True)
            small.resize((small.width * 8, small.height * 8), Image.NEAREST).save(os.path.join(args.preview, icon_id + ".png"))

    def alias(name, icon_id):
        nonlocal clashes
        key = normalize(name)
        if not key or key == icon_id:
            return
        if key in texts or aliases.get(key, icon_id) != icon_id:
            clashes += 1
            return
        aliases[key] = icon_id

    render("weed", weed_leaf())
    for name, image in sorted(icons.items()):
        icon_id = normalize(name)
        if icon_id and icon_id not in texts:
            render(icon_id, image)

    # prefab primeiro, nome traduzido depois: o primeiro a chegar fica com a chave
    resolved = []
    for prefab, (token, path_id) in sorted(items.items()):
        icon_id = normalize(sprite_names.get(path_id, ""))
        if icon_id in texts:
            resolved.append((prefab, token, icon_id))
            alias(prefab, icon_id)
    for prefab, token, icon_id in resolved:
        for name in localization.get(token.lstrip("$"), []):
            alias(name, icon_id)
    for name in ("maconha", "cannabis", "erva"):
        alias(name, "weed")

    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(f"# valheim-server sign-icons v1 px={args.px} colors={args.colors} units={args.units:g} "
                     f"material={args.material or '-'} brightness={args.brightness:g} overlap={args.overlap:g}\n")
        handle.write("D\tweed\n")
        # {u} numa placa escrita a mao: o mesmo material sem iluminacao, em 3 caracteres em vez de 38.
        handle.write("M\tu\t<material=" + UNLIT_MATERIAL + ">\n")
        for icon_id, text in sorted(texts.items()):
            handle.write("I\t" + icon_id + "\t" + text.replace("\n", "\\n") + "\n")
            handle.write("T\t" + icon_id + "\t" + titled[icon_id].replace("\n", "\\n") + "\n")
        for key, icon_id in sorted(aliases.items()):
            handle.write("A\t" + key + "\t" + icon_id + "\n")
    sizes = sorted(len(t) for t in texts.values())
    print(f"{len(texts)} icones, {len(aliases)} apelidos ({clashes} colisoes descartadas); "
          f"texto por icone: mediana {sizes[len(sizes) // 2]}, maximo {sizes[-1]} caracteres; "
          f"{os.path.getsize(args.out) // 1024} KiB em {args.out}", file=sys.stderr)


if __name__ == "__main__":
    main()
