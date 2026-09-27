#!/usr/bin/env python3
"""Monta data/terrain-full.bin, o terreno do mundo inteiro no formato do mapa do jogo.

Entrada: grade do gerador de mundo (`gen grid seed versao -12282 -12282 12282 12282 12 world.bin`,
pontos no centro de cada pixel do mapa do jogo). A seed nao muda, entao roda uma vez por mundo.
Este arquivo e spoiler: fica so no servidor, que manda ao navegador uma copia zerada fora do que
as mesas mostram (terrain.mjs). A camada de explorado sai vazia aqui e e preenchida la.

Formato (little endian), N = size * size, linha 0 = sul (z minimo):
    'VHM1' | u32 size | f32 pixelSize | u32 reservado
    f16 altura[N] | u8 bioma[N] | u8 floresta[N] | u8 nevoa-mistlands[N] | u8 explorado[N]
Bioma: 0 oceano/nada, 1 prado, 2 pantano, 3 montanha, 4 floresta negra, 5 planicie,
6 cinzas, 7 extremo norte, 8 terras nebulosas.

    python3 tools/build_terrain.py world.bin data/terrain-full.bin
"""
import struct
import sys

import numpy as np

SIZE = 2048
PIXEL = 12.0
# Heightmap.Biome -> indice do arquivo
BIOMES = {1: 1, 2: 2, 4: 3, 8: 4, 16: 5, 32: 6, 64: 7, 256: 0, 512: 8}


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def main():
    grid_path, out = sys.argv[1:3]
    with open(grid_path, "rb") as f:
        nx, nz, x0, z0, step = struct.unpack("<iifff", f.read(20))
        assert (nx, nz, step) == (SIZE, SIZE, PIXEL), (nx, nz, step)
        rec = np.frombuffer(f.read(), dtype=np.dtype([("b", "<i2"), ("h", "<f4"), ("ff", "<f4")]))
    rec = rec.reshape(SIZE, SIZE)
    biome_raw = rec["b"].astype(np.int32)
    height = rec["h"].astype(np.float32)
    ff = rec["ff"]
    biome = np.zeros((SIZE, SIZE), np.uint8)
    for raw, idx in BIOMES.items():
        biome[biome_raw == raw] = idx

    # Minimap.GetMaskColor: floresta no vermelho, nevoa das terras nebulosas no verde.
    land = height >= 30
    forest = np.zeros((SIZE, SIZE), np.float32)
    forest[(biome == 1) & (ff < 1.15)] = 1
    forest[(biome == 5) & (ff < 0.8)] = 1
    forest[biome == 4] = 1
    forest[~land] = 0
    mist = np.where((biome == 8) & land, 1 - smoothstep(1.1, 1.3, ff), 0).astype(np.float32)

    with open(out, "wb") as f:
        f.write(b"VHM1" + struct.pack("<IfI", SIZE, PIXEL, 0))
        f.write(height.astype("<f2").tobytes())
        f.write(biome.tobytes())
        f.write((forest * 255).round().astype(np.uint8).tobytes())
        f.write((mist * 255).round().astype(np.uint8).tobytes())
        f.write(bytes(SIZE * SIZE))
    print(f"terreno {SIZE}x{SIZE} -> {out}")


if __name__ == "__main__":
    main()
