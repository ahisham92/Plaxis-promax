"""
Makes the sample models and SAMPLE results files used by the tests, the screenshots and the Modeller's
File > New from sample. The results are made up from simple formulas so the Viewer has something to show without
Plaxis: they are marked "sample" and are not Plaxis results.

    python tools/sample/make_sample.py <out folder>
"""

import gzip
import json
import math
import os
import random
import sys


def excavation_2d():
    """A 10 m deep excavation beside an anchored diaphragm wall, in four layers, with a surcharge and a lowered water level."""
    mats = [
        {"id": "m_fill", "kind": "soil", "name": "Fill", "model": "Mohr-Coulomb", "drainage": "Drained", "colour": "#C9A66B",
         "gammaUnsat": 17, "gammaSat": 19, "E": 10000, "nu": 0.3, "c": 1, "phi": 28, "psi": 0, "kx": 1, "ky": 1, "Rinter": 0.67},
        {"id": "m_sand", "kind": "soil", "name": "Medium dense sand", "model": "Hardening soil", "drainage": "Drained", "colour": "#E8C872",
         "gammaUnsat": 18, "gammaSat": 20, "E50": 30000, "Eoed": 30000, "Eur": 90000, "m": 0.5, "nu": 0.2, "c": 1, "phi": 33, "psi": 3,
         "kx": 1, "ky": 1, "Rinter": 0.7},
        {"id": "m_clay", "kind": "soil", "name": "Stiff clay", "model": "Hardening soil", "drainage": "Undrained A", "colour": "#8FA9B8",
         "gammaUnsat": 18, "gammaSat": 19, "E50": 12000, "Eoed": 9000, "Eur": 36000, "m": 0.8, "nu": 0.2, "c": 8, "phi": 24, "psi": 0,
         "kx": 1e-4, "ky": 1e-4, "Rinter": 0.6},
        {"id": "m_dense", "kind": "soil", "name": "Dense sand", "model": "HS small", "drainage": "Drained", "colour": "#D9A441",
         "gammaUnsat": 19, "gammaSat": 21, "E50": 60000, "Eoed": 60000, "Eur": 180000, "m": 0.5, "nu": 0.2, "c": 1, "phi": 37, "psi": 7,
         "G0": 150000, "gamma07": 1.5e-4, "kx": 2, "ky": 2, "Rinter": 0.7},
        {"id": "m_wall", "kind": "plate", "name": "Diaphragm wall 0.8 m", "colour": "#5B6B7F", "EA": 2.4e7, "EI": 1.28e6, "w": 8.0, "nu": 0.15},
        {"id": "m_anchor", "kind": "anchor", "name": "Anchor rod", "colour": "#7C3AED", "EA": 2.5e5, "Lspacing": 2.5},
        {"id": "m_grout", "kind": "geogrid", "name": "Grout body", "colour": "#0EA5E9", "EA": 1.0e5},
    ]
    boreholes = [
        {"id": "bh1", "name": "BH-1", "x": 0, "top": 0, "head": -2,
         "layers": [{"bottom": -3, "material": "m_fill"}, {"bottom": -12, "material": "m_sand"},
                    {"bottom": -20, "material": "m_clay"}, {"bottom": -30, "material": "m_dense"}]},
        {"id": "bh2", "name": "BH-2", "x": 60, "top": 0, "head": -2,
         "layers": [{"bottom": -3.5, "material": "m_fill"}, {"bottom": -13, "material": "m_sand"},
                    {"bottom": -21, "material": "m_clay"}, {"bottom": -30, "material": "m_dense"}]},
    ]
    structures = [
        {"id": "wall", "type": "plate", "name": "Diaphragm wall", "points": [[20, 0], [20, -18]], "material": "m_wall", "interfaces": True},
        {"id": "anc1", "type": "anchor", "name": "Anchor A1", "points": [[20, -2], [31, -8.5]], "material": "m_anchor"},
        {"id": "grout1", "type": "geogrid", "name": "Grout A1", "points": [[31, -8.5], [38, -12.6]], "material": "m_grout"},
        {"id": "exc1", "type": "polygon", "name": "Excavation 1", "points": [[0, 0], [20, 0], [20, -3], [0, -3]], "material": None},
        {"id": "exc2", "type": "polygon", "name": "Excavation 2", "points": [[0, -3], [20, -3], [20, -7], [0, -7]], "material": None},
        {"id": "exc3", "type": "polygon", "name": "Excavation 3", "points": [[0, -7], [20, -7], [20, -10], [0, -10]], "material": None},
        {"id": "q1", "type": "lineload", "name": "Traffic surcharge", "points": [[22, 0], [32, 0]], "qx": 0, "qy": -10},
    ]
    waterlevels = [
        {"id": "wl1", "name": "Lowered inside", "points": [[0, -11], [20, -11], [20, -2], [60, -2]]},
    ]
    off = {k: False for k in ("wall", "anc1", "grout1", "q1")}
    phases = [
        {"id": "p0", "name": "Initial phase", "calc": "K0 procedure", "parent": None, "active": dict(off)},
        {"id": "p1", "name": "Wall and surcharge", "calc": "Plastic", "parent": "p0", "active": {"wall": True, "q1": True}, "loads": {"q1": {"qy": -10}}},
        {"id": "p2", "name": "Excavate to -3.0 m", "calc": "Plastic", "parent": "p1", "active": {"exc1": False}},
        {"id": "p3", "name": "Install and prestress anchor", "calc": "Plastic", "parent": "p2", "active": {"anc1": True, "grout1": True}},
        {"id": "p4", "name": "Excavate to -7.0 m", "calc": "Plastic", "parent": "p3", "active": {"exc2": False}},
        {"id": "p5", "name": "Excavate to -10.0 m, lower water", "calc": "Plastic", "parent": "p4", "active": {"exc3": False}, "water": "wl1"},
        {"id": "p6", "name": "Factor of safety", "calc": "Safety", "parent": "p5", "active": {}},
    ]
    return {
        "format": "ppm", "version": 1, "title": "Anchored excavation, Block B", "product": "2D", "model": "plane strain", "elements": 15,
        "units": {"length": "m", "force": "kN", "time": "day"},
        "contour": {"xmin": 0, "xmax": 60, "ymin": -30, "ymax": 0},
        "materials": mats, "boreholes": boreholes, "structures": structures, "waterlevels": waterlevels,
        "mesh": {"coarseness": "Medium", "refine": [{"object": "wall", "factor": 0.5}]},
        "phases": phases,
    }


def pit_3d():
    """A 20 x 14 m pit, 8 m deep, inside plate walls, with a pile row and a raft surcharge beside it."""
    mats = [
        {"id": "m_sand", "kind": "soil", "name": "Sand", "model": "Hardening soil", "drainage": "Drained", "colour": "#E8C872",
         "gammaUnsat": 18, "gammaSat": 20, "E50": 35000, "Eoed": 35000, "Eur": 105000, "m": 0.5, "nu": 0.2, "c": 1, "phi": 34, "psi": 4},
        {"id": "m_clay", "kind": "soil", "name": "Clay", "model": "Soft soil", "drainage": "Undrained A", "colour": "#8FA9B8",
         "gammaUnsat": 17, "gammaSat": 18, "lambda": 0.08, "kappa": 0.016, "c": 2, "phi": 25},
        {"id": "m_wall", "kind": "plate", "name": "Sheet pile wall", "colour": "#5B6B7F", "E": 2.1e8, "d": 0.012, "gamma": 78.5, "nu": 0.3},
        {"id": "m_pile", "kind": "embedded", "name": "Bored pile 0.9 m", "colour": "#7C3AED", "E": 3e7, "gamma": 24, "diameter": 0.9,
         "Tskin_start": 80, "Tskin_end": 200, "Fmax": 3000},
    ]
    walls = []
    corners = [(20, 20), (40, 20), (40, 34), (20, 34)]
    for i in range(4):
        a, b = corners[i], corners[(i + 1) % 4]
        walls.append({"id": "w{}".format(i + 1), "type": "plate", "name": "Wall {}".format(i + 1),
                      "points": [[a[0], a[1], 0], [b[0], b[1], 0]], "depth": 14, "material": "m_wall"})
    piles = [{"id": "pile{}".format(i + 1), "type": "embedded", "name": "Pile P{}".format(i + 1),
              "points": [[46, 22 + i * 4, 0], [46, 22 + i * 4, -18]], "material": "m_pile"} for i in range(4)]
    structures = walls + piles + [
        {"id": "pit1", "type": "polygon", "name": "Pit, stage 1", "points": [[20, 20, 0], [40, 20, 0], [40, 34, 0], [20, 34, 0]], "depth": 4, "material": None},
        {"id": "pit2", "type": "polygon", "name": "Pit, stage 2", "points": [[20, 20, -4], [40, 20, -4], [40, 34, -4], [20, 34, -4]], "depth": 4, "material": None},
        {"id": "raft", "type": "surfaceload", "name": "Building beside", "points": [[44, 20, 0], [52, 20, 0], [52, 36, 0], [44, 36, 0]], "qz": -40},
    ]
    off = {s["id"]: False for s in walls + piles}
    off["raft"] = False
    on = {s["id"]: True for s in walls}
    phases = [
        {"id": "p0", "name": "Initial phase", "calc": "K0 procedure", "parent": None, "active": off},
        {"id": "p1", "name": "Piles and building load", "calc": "Plastic", "parent": "p0", "active": dict({p["id"]: True for p in piles}, raft=True)},
        {"id": "p2", "name": "Walls", "calc": "Plastic", "parent": "p1", "active": on},
        {"id": "p3", "name": "Excavate to -4 m", "calc": "Plastic", "parent": "p2", "active": {"pit1": False}},
        {"id": "p4", "name": "Excavate to -8 m", "calc": "Plastic", "parent": "p3", "active": {"pit2": False}},
    ]
    return {
        "format": "ppm", "version": 1, "title": "Basement pit beside Building C", "product": "3D",
        "units": {"length": "m", "force": "kN", "time": "day"},
        "contour": {"xmin": 0, "xmax": 60, "ymin": 0, "ymax": 54, "zmin": -30},
        "materials": mats,
        "boreholes": [{"id": "bh1", "name": "BH-1", "x": 0, "y": 0, "top": 0, "head": -3,
                       "layers": [{"bottom": -12, "material": "m_sand"}, {"bottom": -30, "material": "m_clay"}]}],
        "structures": structures, "waterlevels": [], "mesh": {"coarseness": "Medium"}, "phases": phases,
    }


# ---- Sample results ------------------------------------------------------------------------------------------------


def _inside(poly, x, y):
    c = False
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i][0], poly[i][1]
        x2, y2 = poly[(i + 1) % n][0], poly[(i + 1) % n][1]
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
            c = not c
    return c


def _excavated(model, upto):
    """Polygons off at phase index upto (following the phase chain)."""
    state = {}
    for ph in model["phases"][: upto + 1]:
        state.update(ph.get("active") or {})
    return [s for s in model["structures"] if s["type"] == "polygon" and state.get(s["id"]) is False]


def sample_results_2d(model):
    rnd = random.Random(7)
    c = model["contour"]
    pts = []
    step = 0.75
    y = c["ymin"]
    row = 0
    while y <= c["ymax"] + 1e-9:
        x = c["xmin"] + (step / 2 if row % 2 else 0)
        while x <= c["xmax"] + 1e-9:
            jx = 0 if x in (c["xmin"], c["xmax"]) else rnd.uniform(-0.12, 0.12)
            jy = 0 if y in (c["ymin"], c["ymax"]) else rnd.uniform(-0.12, 0.12)
            pts.append((min(c["xmax"], max(c["xmin"], x + jx)), min(c["ymax"], max(c["ymin"], y + jy))))
            x += step
        y += step * 0.866
        row += 1
    # Nodes along the wall, so the wall line is sharp.
    for i in range(0, 37):
        pts.append((20.0, -i * 0.5))
    exc_depths = [0, 0, 3, 3, 7, 10, 10]
    phases_out = []
    for index, ph in enumerate(model["phases"]):
        d = exc_depths[index]
        gone = _excavated(model, index)
        nodes = [(x, y) for (x, y) in pts if not any(_inside(p["points"], x, y) and y > -d + 1e-6 and x < 20 - 1e-6 for p in gone)]
        f = d / 10.0
        safety = ph["calc"] == "Safety"
        if safety:
            f = 2.6
        ux, uy, ut = [], [], []
        for x, y in nodes:
            depth = -y
            behind = x >= 20
            if behind:
                a = math.exp(-((x - 20) / 14.0) ** 2)
                bx = -0.028 * f * a * math.exp(-((depth - 0.7 * max(d, 1)) / 9.0) ** 2)
                by = -0.020 * f * math.exp(-((x - 26) / 10.0) ** 2) * math.exp(-depth / 16.0)
            else:
                a = math.exp(-((20 - x) / 6.0) ** 2)
                bx = -0.018 * f * a * math.exp(-((depth - d - 2) / 6.0) ** 2) if depth >= d else -0.02 * f * a
                by = 0.024 * f * math.exp(-((depth - d) / 7.0) ** 2) * (0.6 + 0.4 * (1 - math.exp(-(20 - x) / 6.0)))
            if index == 1:
                by += -0.004 * math.exp(-((x - 27) / 5.0) ** 2) * math.exp(-depth / 6)
            if safety and behind:
                band = math.exp(-((depth - (x - 20) * 0.9 - 2) / 2.2) ** 2) * (1 if x < 34 else 0.2)
                bx += -0.06 * band
                by += -0.05 * band
            ux.append(bx)
            uy.append(by)
            ut.append(math.hypot(bx, by))
        # Stress points: centroids of a coarser set.
        spx, spy, sxx, syy, sxy, s1, s3, pa, tr, pl = [], [], [], [], [], [], [], [], [], []
        for i, (x, y) in enumerate(nodes[::2]):
            depth = -y
            ground = -d if x < 20 else 0.0
            dz = max(0.0, ground - y)
            water = (-11 if (index >= 5 and x < 20) else -2)
            pw = max(0.0, water - y) * 10.0
            sv = -(18.5 * dz) + pw
            k0 = 0.45 + (0.25 if (x > 20 and x < 24 and depth < d + 2) else 0)
            sh = k0 * sv
            txy = -6.0 * f * math.exp(-((x - 20) / 5.0) ** 2) * math.exp(-((depth - d) / 6.0) ** 2)
            spx.append(round(x, 3)); spy.append(round(y, 3))
            sxx.append(round(sh, 2)); syy.append(round(sv, 2)); sxy.append(round(txy, 2))
            mean = (sh + sv) / 2
            rad = math.hypot((sh - sv) / 2, txy)
            s1.append(round(mean - rad, 2)); s3.append(round(mean + rad, 2))
            pa.append(round(-pw, 2))
            rel = min(1.0, 0.25 + 0.7 * f / 1.2 * math.exp(-((x - 21) / 3.5) ** 2) * math.exp(-((depth - d - 1) / 4.0) ** 2))
            if safety:
                rel = min(1.0, rel + math.exp(-((depth - (x - 20) * 0.9 - 2) / 2.0) ** 2) * (1 if 20 < x < 34 else 0))
            tr.append(round(rel, 3))
            pl.append(1 if rel >= 0.99 else 0)
        wall_y = [-i * 0.5 for i in range(37)]
        M, Q, N, wx = [], [], [], []
        for y in wall_y:
            z = -y
            m = f * (-180 * math.sin(math.pi * min(z, 18) / 18) * math.exp(-((z - d - 1) / 6) ** 2) + 60 * math.exp(-((z - 2) / 2.5) ** 2) * (index >= 3))
            M.append(round(m, 2))
            Q.append(round(f * 70 * math.cos(math.pi * z / 12) * math.exp(-((z - d) / 7) ** 2), 2))
            N.append(round(-(8 * z + (60 if index >= 3 else 0)), 2))
            wx.append(round(-0.028 * f * math.exp(-((z - 0.7 * max(d, 1)) / 9.0) ** 2), 5))
        entry = {
            "name": ph["name"], "calc": ph["calc"], "ended": "OK",
            "nodes": {"x": [round(p[0], 3) for p in nodes], "y": [round(p[1], 3) for p in nodes]},
            "node": {"Ux": [round(v, 6) for v in ux], "Uy": [round(v, 6) for v in uy], "Utot": [round(v, 6) for v in ut]},
            "sp": {"x": spx, "y": spy, "SigxxE": sxx, "SigyyE": syy, "Sigxy": sxy, "Sig1": s1, "Sig3": s3, "PActive": pa, "TauRel": tr, "Plastic": pl},
            "plates": [] if index == 0 else [{"name": "Plate_1_1", "struct": "wall", "x": [20.0] * len(wall_y), "y": wall_y, "M": M, "Q": Q, "N": N, "Ux": wx}],
            "anchors": [{"name": "NodeToNodeAnchor_1_1", "struct": "anc1", "kind": "NodeToNodeAnchor", "F": round(-150 - 90 * f, 1)}] if index >= 3 else [],
            "embedded": [],
        }
        if safety:
            entry["SumMsf"] = 1.47
        entry["SumMstage"] = 1.0
        phases_out.append(entry)
    return phases_out


def sample_results_3d(model):
    c = model["contour"]
    phases_out = []
    xs = [c["xmin"] + i * 2.0 for i in range(int((c["xmax"] - c["xmin"]) / 2) + 1)]
    ys = [c["ymin"] + i * 2.0 for i in range(int((c["ymax"] - c["ymin"]) / 2) + 1)]
    zs = [-i * 2.0 for i in range(16)]
    depths = [0, 0, 0, 4, 8]
    for index, ph in enumerate(model["phases"]):
        d = depths[index]
        X, Y, Z, ux, uy, uz, ut = [], [], [], [], [], [], []
        f = d / 8.0
        for x in xs:
            for y in ys:
                for z in zs:
                    inpit = 20 < x < 40 and 20 < y < 34
                    if inpit and z > -d:
                        continue
                    dx = max(20 - x, 0, x - 40)
                    dy = max(20 - y, 0, y - 34)
                    dist = math.hypot(dx, dy)
                    if inpit:
                        vz = 0.03 * f * math.exp(-((-z - d) / 6) ** 2)
                        vx = vy = 0
                    else:
                        a = math.exp(-(dist / 9.0) ** 2)
                        cx, cy = 30, 27
                        nx, ny = (cx - x), (cy - y)
                        nn = math.hypot(nx, ny) or 1
                        mag = 0.022 * f * a * math.exp(-((-z - 0.7 * max(d, 1)) / 7) ** 2)
                        vx, vy = mag * nx / nn, mag * ny / nn
                        vz = -0.015 * f * a * math.exp(z / 12)
                    if index >= 1 and 42 < x < 54 and 18 < y < 38:
                        vz += -0.012 * math.exp(z / 8)
                    X.append(x); Y.append(y); Z.append(z)
                    ux.append(round(vx, 6)); uy.append(round(vy, 6)); uz.append(round(vz, 6)); ut.append(round(math.sqrt(vx * vx + vy * vy + vz * vz), 6))
        phases_out.append({"name": ph["name"], "calc": ph["calc"], "ended": "OK", "SumMstage": 1.0,
                           "nodes": {"x": X, "y": Y, "z": Z}, "node": {"Ux": ux, "Uy": uy, "Uz": uz, "Utot": ut},
                           "plates": [], "embedded": [], "anchors": []})
    return phases_out


def pack(model, phases):
    return {"format": "ppr", "version": 1, "title": model["title"], "product": model["product"], "plaxis": "sample",
            "sample": True, "created": "2026-11-25T10:00:00Z", "made_with": "Plaxis ProMax (sample results, not from Plaxis)",
            "units": model["units"], "model": model, "phases": phases, "missing": []}


def main(out):
    os.makedirs(out, exist_ok=True)
    m2, m3 = excavation_2d(), pit_3d()
    for name, m, ph in (("excavation-2d", m2, sample_results_2d(m2)), ("pit-3d", m3, sample_results_3d(m3))):
        with open(os.path.join(out, name + ".ppm"), "w", encoding="utf-8") as f:
            json.dump(m, f, indent=1)
        with gzip.open(os.path.join(out, name + ".ppr"), "wb") as f:
            f.write(json.dumps(pack(m, ph), separators=(",", ":")).encode("utf-8"))
    print("Wrote samples to", out)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "samples")
