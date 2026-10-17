#!/usr/bin/env python3
"""Summarize a recording written by the server plugin's recorder (plugin/src/Recording).

    python3 -I inspect_recording.py FILE.vrec.gz            # what is in it
    python3 -I inspect_recording.py FILE.vrec.gz --track Fader   # health and position of a prefab or player, per second

Format: RecordFormat.cs. Standard library only.
"""
import argparse
import collections
import gzip
import struct
import sys

KINDS = {1: "Meta", 2: "PrefabName", 3: "MethodName", 4: "Peer", 5: "Zdo", 6: "Snapshot", 7: "Routed", 8: "World", 9: "Marker"}


def stable_hash(s):
    """Valheim's string.GetStableHashCode, as a signed 32-bit int."""
    def i32(v):
        v &= 0xFFFFFFFF
        return v - (1 << 32) if v >= 1 << 31 else v
    a = b = 5381
    i = 0
    while i < len(s) and s[i] != "\0":
        a = i32(((a << 5) + a) ^ ord(s[i]))
        if i == len(s) - 1 or s[i + 1] == "\0":
            break
        b = i32(((b << 5) + b) ^ ord(s[i + 1]))
        i += 2
    return i32(a + b * 1566083941)


FIELDS = {stable_hash(n): n for n in ("health", "max_health", "playerName", "level", "dead", "tamed", "noise")}


class Cursor:
    def __init__(self, data):
        self.d, self.at = data, 0

    def take(self, fmt):
        v = struct.unpack_from("<" + fmt, self.d, self.at)
        self.at += struct.calcsize("<" + fmt)
        return v if len(v) > 1 else v[0]

    def str(self):
        n = self.take("H")
        s = self.d[self.at:self.at + n].decode("utf-8", "replace")
        self.at += n
        return s

    def blob(self):
        n = self.take("I")
        b = self.d[self.at:self.at + n]
        self.at += n
        return b

    def zdoid(self):
        return self.take("qI")

    # BinaryWriter.Write(string): 7-bit encoded length + UTF-8.
    def dotnet_str(self):
        n, shift = 0, 0
        while True:
            byte = self.take("B")
            n |= (byte & 0x7F) << shift
            shift += 7
            if not byte & 0x80:
                break
        s = self.d[self.at:self.at + n].decode("utf-8", "replace")
        self.at += n
        return s

    def byte_array(self):
        n = self.take("i")
        b = self.d[self.at:self.at + n]
        self.at += n
        return b

    def num_items(self):
        n = self.take("B")
        if n & 0x80:
            n = ((n & 0x7F) << 8) | self.take("B")
        return n


def zdo_fields(data):
    """Decode ZDO.Serialize: prefab, rotation and the floats/ints/strings it carries."""
    c = Cursor(data)
    flags = c.take("H")
    out = {"prefab": c.take("i")}
    if flags & 0x1000:
        out["rotation"] = c.take("fff")
    if flags & 0xFF:
        if flags & 0x01:
            c.take("B")
            c.zdoid()
        readers = [(0x02, lambda: c.take("f")), (0x04, lambda: c.take("fff")), (0x08, lambda: c.take("ffff")),
                   (0x10, lambda: c.take("i")), (0x20, lambda: c.take("q")), (0x40, c.dotnet_str), (0x80, c.byte_array)]
        for bit, read in readers:
            if not flags & bit:
                continue
            for _ in range(c.num_items()):
                key = c.take("i")
                out[FIELDS.get(key, key)] = read()
    return out


def records(path):
    with gzip.open(path, "rb") as f:
        head = f.read(6)
        if head[:4] != b"VREC":
            sys.exit(f"{path}: not a recording")
        while True:
            try:
                h = f.read(9)
                if len(h) < 9:
                    return
                kind, ms, n = struct.unpack("<BII", h)
                payload = f.read(n)
                if len(payload) < n:
                    return
            except (EOFError, OSError):
                print("(file ends mid-stream: server stopped while recording)")
                return
            yield kind, ms, payload


def clock(ms):
    s = ms / 1000
    return f"{int(s // 60)}:{s % 60:04.1f}"


def summarize(path):
    prefabs, methods, peers = {}, {}, {}
    kinds = collections.Counter()
    kind_bytes = collections.Counter()
    updates = collections.Counter()
    zdos_of = collections.defaultdict(set)
    routed = collections.Counter()
    markers, deaths = [], []
    last_ms, last_world, world_gaps = 0, None, []
    meta = None
    for kind, ms, payload in records(path):
        kinds[kind] += 1
        kind_bytes[kind] += len(payload) + 9
        last_ms = max(last_ms, ms)
        c = Cursor(payload)
        if kind == 1:
            meta = dict(zip(("plugin", "game", "world", "label", "reason", "started"), (c.str() for _ in range(6))))
            meta["world_time"], meta["radius"] = c.take("d"), c.take("f")
        elif kind == 2:
            h = c.take("i")
            prefabs[h] = c.str()
        elif kind == 3:
            h = c.take("i")
            methods[h] = c.str()
        elif kind == 4:
            uid, name, host, char = c.take("q"), c.str(), c.str(), c.zdoid()
            if uid in peers and peers[uid][1] != char:
                deaths.append((ms, name))
            peers[uid] = (name, char)
        elif kind in (5, 6):
            zid = c.zdoid()
            c.take("HIq")
            c.take("fff")
            fields = zdo_fields(c.blob())
            name = prefabs.get(fields["prefab"], str(fields["prefab"]))
            zdos_of[name].add(zid)
            if kind == 5:
                updates[name] += 1
        elif kind == 7:
            c.take("qq")
            c.zdoid()
            routed[c.take("i")] += 1
        elif kind == 8:
            if last_world is not None and ms - last_world > 1500:
                world_gaps.append((last_world, ms - last_world))
            last_world = ms
        elif kind == 9:
            markers.append((ms, c.str()))

    if meta:
        print(f"{meta['label']} ({meta['reason']}), started {meta['started']} UTC, world {meta['world']}, "
              f"game {meta['game']}, plugin {meta['plugin']}, radius {meta['radius']:.0f} m")
    raw = sum(kind_bytes.values())
    seconds = max(last_ms / 1000, 0.001)
    print(f"duration {clock(last_ms)}, {raw / 1048576:.1f} MB raw ({raw / seconds / 1024:.0f} KB/s)")
    print("records: " + ", ".join(f"{KINDS.get(k, k)} {n} ({kind_bytes[k] / 1048576:.1f} MB)" for k, n in sorted(kinds.items())))
    print("players: " + ", ".join(sorted({n for n, _ in peers.values()})))
    if deaths:
        print("new character (death/respawn): " + ", ".join(f"{n} at {clock(ms)}" for ms, n in deaths))
    print("\nprefab                          objects   updates   updates/s per object")
    for name, n in updates.most_common(25):
        objs = len(zdos_of[name])
        print(f"  {name:30} {objs:7} {n:9}   {n / seconds / objs:6.1f}")
    snapshot_only = sorted(n for n in zdos_of if n not in updates)
    if snapshot_only:
        print(f"  ({len(snapshot_only)} prefabs only in the snapshot)")
    print("\nrouted RPCs: " + ", ".join(f"{methods.get(h, h)} {n}" for h, n in routed.most_common(20)))
    if markers:
        print("\nmarkers:")
        for ms, text in markers:
            print(f"  {clock(ms):>8}  {text}")
    if world_gaps:
        print("\nworld ticks late by >1.5 s (server hitch or dropped chunk): " +
              ", ".join(f"{clock(at)} +{gap / 1000:.1f}s" for at, gap in world_gaps[:20]))


def track(path, wanted):
    prefabs, names = {}, {}
    last = {}
    print("time      object                       x        y        z     health")
    for kind, ms, payload in records(path):
        c = Cursor(payload)
        if kind == 2:
            h = c.take("i")
            prefabs[h] = c.str()
        elif kind in (5, 6):
            zid = c.zdoid()
            c.take("HIq")
            x, y, z = c.take("fff")
            f = zdo_fields(c.blob())
            name = f.get("playerName") or prefabs.get(f["prefab"], "")
            if wanted.lower() not in name.lower():
                continue
            second = ms // 1000
            if last.get(zid) == second:
                continue
            last[zid] = second
            names.setdefault(zid, f"{name}#{len(names) + 1}")
            hp = f.get("health")
            print(f"{clock(ms):>8}  {names[zid]:24} {x:8.1f} {y:8.1f} {z:8.1f}  {'' if hp is None else f'{hp:9.1f}'}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--track", help="prefab or player name (substring)")
    a = ap.parse_args()
    if a.track:
        track(a.file, a.track)
    else:
        summarize(a.file)


if __name__ == "__main__":
    main()
