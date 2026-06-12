# Auto-discovers tileset packs from the sprite library.
# For every sprite family (common filename prefix, uniform square size) it
# classifies pieces by visual structure and emits public/packs.json:
#   fill   = most uniform fully-opaque tile
#   edge   = tile whose TOP strip differs from its interior, other sides plain
#   corner = tile whose TOP and LEFT differ (rounded-corner alpha is a bonus)
# Pieces are kept color-consistent with the chosen fill so mixed sheets
# (e.g. Asset_Sheet) yield one coherent pack instead of a color mash.
import json, os, re, sys
from collections import defaultdict
from PIL import Image

SPRITE_DIR = r"C:\Users\macro\AppData\Roaming\IGTAPEditor\sprites"
OUT_PATH   = os.path.join(os.path.dirname(os.path.abspath(__file__)), "public", "packs.json")

MIN_GROUP   = 6
SIZE_RANGE  = (12, 80)

# Families that aren't box tilesets (hazards, props, slopes, platform caps, decor)
EXCLUDE_RE = re.compile(r"spike|platform|banner|flower|plant|gate|fresco$|dot idle|background|^line_tileset$", re.I)

# Hand-verified piece corrections on top of auto-discovery
OVERRIDES = {
    "ground1_tileset": {"edge": "ground1_tileset_57"},
}

# Confidence floors (visually-broken packs score low on at least one role)
MIN_EDGE_SCORE, MIN_CORNER_SCORE = 25, 15

def region_stats(px, x0, y0, x1, y1):
    n = 0; a_sum = 0; r = g = b = 0; opq = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            pr, pg, pb, pa = px[x, y]
            a_sum += pa; n += 1
            if pa > 96:
                r += pr; g += pg; b += pb; opq += 1
    alpha = a_sum / (255.0 * n) if n else 0.0
    col = (r / opq, g / opq, b / opq) if opq else None
    return alpha, col

def cdist(c1, c2):
    if c1 is None or c2 is None: return 160.0
    return (sum((a - b) ** 2 for a, b in zip(c1, c2))) ** 0.5

def analyze(path):
    im = Image.open(path).convert("RGBA")
    S = min(im.width, im.height)   # near-square sprites: stay in bounds
    px = im.load()
    t = max(3, S // 5)
    center = region_stats(px, t, t, S - t, S - t)
    sides = {
        "top":    region_stats(px, 0, 0, S, t),
        "bottom": region_stats(px, 0, S - t, S, S),
        "left":   region_stats(px, 0, 0, t, S),
        "right":  region_stats(px, S - t, 0, S, S),
    }
    # distinctness of each side vs the interior (color + alpha difference)
    d = {}
    for k, (al, co) in sides.items():
        d[k] = cdist(co, center[1]) + abs(al - center[0]) * 220.0
    # corner-region alpha (rounded corners go transparent)
    ca = {
        "tl": region_stats(px, 0, 0, t, t)[0],
        "tr": region_stats(px, S - t, 0, S, t)[0],
        "bl": region_stats(px, 0, S - t, t, S)[0],
        "br": region_stats(px, S - t, S - t, S, S)[0],
    }
    return {"d": d, "ca": ca, "center_alpha": center[0], "center_col": center[1]}

def main():
    groups = defaultdict(list)
    for fn in os.listdir(SPRITE_DIR):
        if not fn.lower().endswith(".png"): continue
        m = re.match(r"^(.*)_(\d+)$", fn[:-4])
        if not m: continue
        groups[m.group(1)].append(fn[:-4])

    packs = []
    for fam, names in sorted(groups.items()):
        if len(names) < MIN_GROUP: continue
        if fam.startswith(("gametile_", "tile_", "obj_")): continue
        if EXCLUDE_RE.search(fam): continue
        # bucket by exact size; analyze the dominant square-ish bucket
        buckets = defaultdict(list)
        for n in names:
            try:
                with Image.open(os.path.join(SPRITE_DIR, n + ".png")) as im:
                    w, h = im.width, im.height
                if h == 0 or not (0.8 <= w / h <= 1.25): continue
                if not (SIZE_RANGE[0] <= w <= SIZE_RANGE[1]): continue
                buckets[(w, h)].append(n)
            except Exception: pass
        if not buckets: continue
        (S, _), ok = max(buckets.items(), key=lambda kv: len(kv[1]))
        if len(ok) < MIN_GROUP: continue

        stats = {}
        for n in ok:
            try: stats[n] = analyze(os.path.join(SPRITE_DIR, n + ".png"))
            except Exception: pass
        if len(stats) < MIN_GROUP: continue

        # ── fill: most uniform fully-opaque tile ──
        fill, fill_s = None, None
        for n, st in stats.items():
            if st["center_alpha"] < 0.97 or st["center_col"] is None: continue
            tot = sum(st["d"].values()) + sum(abs(a - 1.0) for a in st["ca"].values()) * 150
            if fill_s is None or tot < fill_s: fill, fill_s = n, tot
        if fill is None: continue
        fcol = stats[fill]["center_col"]

        def color_ok(st):   # piece interior must match the fill's palette
            return st["center_col"] is not None and cdist(st["center_col"], fcol) < 60

        # ── edge: horizontal strip piece — left/right plain, feature on top
        # (and possibly bottom too: floor strips have finished undersides) ──
        edge, edge_s = None, 0.0
        for n, st in stats.items():
            if n == fill or not color_ok(st): continue
            d = st["d"]
            margin = max(d["top"], d["bottom"]) - max(d["left"], d["right"])
            margin += 0.3 * (d["top"] - d["bottom"])           # prefer top-featured
            # corner pieces masquerading as edges: asymmetric corner alpha
            if abs(st["ca"]["tl"] - st["ca"]["tr"]) > 0.18: margin -= 60
            if margin > edge_s: edge, edge_s = n, margin

        # ── corner: top AND left distinct, right plain (bottom may be finished) ──
        corner, corner_s = None, 0.0
        for n, st in stats.items():
            if n == fill or not color_ok(st): continue
            d = st["d"]
            margin = min(d["top"], d["left"]) - d["right"]
            # rounded top-left corner bonus: TL more transparent than BR
            margin += max(0.0, (st["ca"]["br"] - st["ca"]["tl"])) * 140
            # reject pieces whose TR corner is also open (would be a full-top cap)
            margin -= max(0.0, (st["ca"]["br"] - st["ca"]["tr"])) * 70
            if margin > corner_s: corner, corner_s = n, margin

        if edge is None or corner is None or corner == edge: continue
        if edge_s < MIN_EDGE_SCORE or corner_s < MIN_CORNER_SCORE: continue

        ov = OVERRIDES.get(fam, {})
        corner = ov.get("corner", corner)
        edge   = ov.get("edge", edge)
        fill   = ov.get("fill", fill)

        label = re.sub(r"[_\s]*tile\s*set[_\s]*", " ", fam, flags=re.I)
        label = re.sub(r"[_\s]*tileset[_\s]*", " ", label, flags=re.I)
        label = re.sub(r"[@_]", " ", label).strip().title() or fam
        packs.append({"family": fam, "label": label, "size": S,
                      "corner": corner, "edge": edge, "fill": fill,
                      "scores": {"edge": round(edge_s, 1), "corner": round(corner_s, 1)}})

    packs.sort(key=lambda p: -(p["scores"]["edge"] + p["scores"]["corner"]))
    with open(OUT_PATH, "w") as f:
        json.dump(packs, f, indent=2)
    print(f"{len(packs)} packs -> {OUT_PATH}")
    for p in packs:
        print(f"  {p['label']:<24} corner={p['corner']:<28} edge={p['edge']:<28} fill={p['fill']}  (e={p['scores']['edge']}, c={p['scores']['corner']})")

if __name__ == "__main__":
    main()
