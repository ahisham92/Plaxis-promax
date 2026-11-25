"""
Plaxis ProMax Modeller: turns a Modeller model (.ppm, JSON) into a Plaxis project, and reads a calculated project's
results into a results file (.ppr) that the Viewer opens with no Plaxis.

This is the only part of the Modeller and the Viewer that needs Plaxis, and so the only part that uses a Plaxis
licence. It runs inside Plaxis ProMax's worker (op build_model and op export_results) and is also written into the
stand-alone script the Modeller exports ("Run script" in Plaxis Input).

Plaxis renames properties between versions (Eref / E, EA / EA1, MaterialName / Identification...). Every value is
set through set_first(), which tries the known names in turn and records the ones Plaxis refused, so nothing is lost
without being said: the answer lists what was made, what was refused and why.

Coordinates: 2D is x (horizontal) and y (up). 3D is x, y (horizontal) and z (up).
"""

import array
import gzip
import hashlib
import json
import math
import struct
import sys
import time

FORMAT_VERSION = 2

# ---- Names Plaxis uses for each value, newest first ------------------------------------------------------------------

SOIL_MODELS = {
    "Linear elastic": (1, "Linear elastic"),
    "Mohr-Coulomb": (2, "Mohr-Coulomb"),
    "Hardening soil": (3, "Hardening soil"),
    "HS small": (4, "HS small"),
    "Soft soil": (5, "Soft soil"),
}

DRAINAGE = {"Drained": 0, "Undrained A": 1, "Undrained B": 2, "Undrained C": 3, "Non-porous": 4}

# Modeller field -> Plaxis property names to try.
SOIL_FIELDS = {
    "gammaUnsat": ["gammaUnsat"],
    "gammaSat": ["gammaSat"],
    "E": ["Eref", "E"],
    "nu": ["nu", "nuUR"],
    "c": ["cref", "cRef"],
    "phi": ["phi"],
    "psi": ["psi"],
    "E50": ["E50ref", "E50Ref"],
    "Eoed": ["EoedRef", "Eoedref"],
    "Eur": ["EurRef", "Eurref"],
    "m": ["powerm"],
    "nuUR": ["nuUR"],
    "G0": ["G0ref", "G0Ref"],
    "gamma07": ["gamma07"],
    "lambda": ["lambdaModified", "lambda*"],
    "kappa": ["kappaModified", "kappa*"],
    "kx": ["perm_primary_horizontal_axis", "kx"],
    "ky": ["perm_vertical_axis", "ky"],
    "kz": ["perm_vertical_axis", "kz"],
    "Rinter": ["Rinter"],
    "K0": ["K0Primary", "K0X"],
    "Su": ["sURef", "Su"],
}

PLATE_FIELDS = {"EA": ["EA", "EA1"], "EI": ["EI"], "w": ["w"], "nu": ["nu", "StructNu"], "d": ["d"],
                "E": ["E1", "E"], "t": ["d"], "gamma": ["Gamma", "gamma"]}
ANCHOR_FIELDS = {"EA": ["EA"], "Lspacing": ["Lspacing"], "Fmax": ["FMaxTens", "Fmax"]}
GEOGRID_FIELDS = {"EA": ["EA", "EA1"]}
EMBEDDED_FIELDS = {"E": ["E"], "gamma": ["gamma", "Gamma"], "diameter": ["Diameter"], "Lspacing": ["Lspacing"],
                   "Tskin_start": ["TSkinStartMax", "Tskin_start_max"], "Tskin_end": ["TSkinEndMax", "Tskin_end_max"],
                   "Fmax": ["Fmax", "FMax"]}

# The 2D mesh command takes the element size factor; Plaxis's names for it.
MESH_SIZES = {"Very coarse": 0.12, "Coarse": 0.08, "Medium": 0.06, "Fine": 0.04, "Very fine": 0.03}

CALC_TYPES = {
    "K0 procedure": ["K0 procedure", "K0procedure"],
    "Gravity loading": ["Gravity loading", "Gravityloading"],
    "Plastic": ["Plastic"],
    "Consolidation": ["Consolidation"],
    "Safety": ["Safety"],
    "Fully coupled": ["Fully coupled flow-deformation", "Fullycoupled"],
}


# ---- Small helpers ---------------------------------------------------------------------------------------------------


def _one_line(ex):
    text = str(ex).strip().splitlines()
    text = [t for t in text if t.strip() and not t.startswith("Unsuccessful command")]
    return (text[-1] if text else type(ex).__name__)[:300]


def _label(obj):
    for name in ("Name", "Identification", "MaterialName"):
        try:
            v = getattr(obj, name)
            v = getattr(v, "value", v)
            if isinstance(v, str) and v:
                return v
        except Exception:
            continue
    return str(obj)


def _pick(made, prefix):
    """The object of a kind among what a Plaxis command made (plate() gives points, a line and the plate)."""
    items = made if isinstance(made, (list, tuple)) else [made]
    for obj in reversed(items):
        if _label(obj).startswith(prefix):
            return obj
    return items[-1] if items else None


class Log:
    """What the build did: objects made, values Plaxis refused, and steps that failed (with the reason)."""

    def __init__(self):
        self.made = []
        self.refused = []
        self.failed = []
        self.ids = {}

    def ok(self, what, obj=None, ident=None):
        self.made.append(what if obj is None else "{} ({})".format(what, _label(obj)))
        if ident and obj is not None:
            self.ids[ident] = _label(obj)

    def refuse(self, where, field, why):
        self.refused.append({"object": where, "value": field, "why": why})

    def fail(self, what, ex):
        self.failed.append({"step": what, "why": _one_line(ex)})

    def answer(self):
        return {"made": self.made, "refused": self.refused, "failed": self.failed, "plaxis_names": self.ids}


def set_first(obj, names, value, log, where, field):
    """Sets the first of names that Plaxis takes; records a refusal when none does."""
    last = None
    for name in names:
        try:
            obj.setproperties(name, value)
            return name
        except Exception as ex:
            last = ex
        try:
            setattr(obj, name, value)
            return name
        except Exception as ex:
            last = ex
    log.refuse(where, field, _one_line(last) if last else "no name to try")
    return None


def _num(v):
    return v is not None and isinstance(v, (int, float)) and not (isinstance(v, float) and math.isnan(v))


def _pt(p, is_3d):
    return (float(p[0]), float(p[1]), float(p[2])) if is_3d else (float(p[0]), float(p[1]))


# ---- Build ---------------------------------------------------------------------------------------------------------


def build(s_i, g_i, model, new_project=True):
    """Builds the whole model in the open Plaxis Input. Returns the log's answer and the map of object ids."""
    log = Log()
    is_3d = model.get("product") == "3D"
    if new_project:
        try:
            s_i.new()
        except Exception as ex:
            log.fail("new project", ex)
    project = getattr(g_i, "Project", None)
    if project is not None:
        if model.get("title"):
            set_first(project, ["Title"], model["title"], log, "project", "title")
        if not is_3d:
            set_first(project, ["ModelType"], "Axisymmetry" if model.get("model") == "axisymmetric" else "Plane strain", log, "project", "model")
            set_first(project, ["ElementType"], "6-Noded" if model.get("elements") == 6 else "15-Noded", log, "project", "elements")
        units = model.get("units") or {}
        for field, name in (("length", "UnitLength"), ("force", "UnitForce"), ("time", "UnitTime")):
            if units.get(field):
                set_first(project, [name], units[field], log, "project", field)

    _soil_mode(g_i)
    c = model.get("contour") or {}
    try:
        if is_3d:
            g_i.SoilContour.initializerectangular(float(c["xmin"]), float(c["ymin"]), float(c["xmax"]), float(c["ymax"]))
        else:
            g_i.SoilContour.initializerectangular(float(c["xmin"]), float(c.get("ymin", -50)), float(c["xmax"]), float(c.get("ymax", 0)))
        log.ok("soil contour")
    except Exception as ex:
        log.fail("soil contour", ex)

    mats = _materials(g_i, model, log)
    objects = {}
    _boreholes(g_i, model, mats, log, is_3d, objects)
    _structures(g_i, model, mats, log, is_3d, objects)
    _water(g_i, model, log, is_3d, objects)
    _mesh(g_i, model, log, is_3d, objects)
    _phases(g_i, model, log, is_3d, objects)
    out = log.answer()
    out["counts"] = {"materials": len(mats), "objects": len(objects), "phases": len(model.get("phases") or [])}
    return out, objects


def _soil_mode(g_i):
    try:
        g_i.gotosoil()
    except Exception:
        pass


def _materials(g_i, model, log):
    mats = {}
    for m in model.get("materials") or []:
        kind = m.get("kind", "soil")
        name = m.get("name") or m.get("id")
        try:
            if kind == "soil":
                mat = g_i.soilmat()
                fields = SOIL_FIELDS
            elif kind == "plate":
                mat = g_i.platemat()
                fields = PLATE_FIELDS
            elif kind == "anchor":
                mat = g_i.anchormat()
                fields = ANCHOR_FIELDS
            elif kind == "geogrid":
                mat = g_i.geogridmat()
                fields = GEOGRID_FIELDS
            elif kind == "embedded":
                mat = g_i.embeddedbeammat()
                fields = EMBEDDED_FIELDS
            else:
                log.refuse(name, "kind", "unknown material kind " + kind)
                continue
        except Exception as ex:
            log.fail("material " + name, ex)
            continue
        set_first(mat, ["MaterialName", "Identification"], name, log, name, "name")
        if m.get("colour"):
            try:
                rgb = int(m["colour"].lstrip("#"), 16)
                set_first(mat, ["Colour"], ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | (rgb >> 16), log, name, "colour")
            except ValueError:
                pass
        if kind == "soil":
            model_name = m.get("model", "Mohr-Coulomb")
            number, text = SOIL_MODELS.get(model_name, (2, "Mohr-Coulomb"))
            if set_first(mat, ["SoilModel"], text, Log(), name, "model") is None:
                set_first(mat, ["SoilModel"], number, log, name, "model")
            drainage = m.get("drainage", "Drained")
            if set_first(mat, ["DrainageType"], drainage, Log(), name, "drainage") is None:
                set_first(mat, ["DrainageType"], DRAINAGE.get(drainage, 0), log, name, "drainage")
            if _num(m.get("Rinter")):
                set_first(mat, ["InterfaceStrengthDetermination"], "Manual", Log(), name, "Rinter")
            if _num(m.get("K0")):
                set_first(mat, ["K0Determination"], "Manual", Log(), name, "K0")
        if kind == "plate" and m.get("isotropic", True):
            set_first(mat, ["Isotropic"], True, Log(), name, "isotropic")
        for field, names in fields.items():
            if _num(m.get(field)):
                set_first(mat, names, float(m[field]), log, name, field)
        mats[m.get("id")] = mat
        log.ok("material " + name, mat, m.get("id"))
    return mats


def _boreholes(g_i, model, mats, log, is_3d, objects):
    holes = model.get("boreholes") or []
    if not holes:
        return
    _soil_mode(g_i)
    layer_count = 0
    for index, hole in enumerate(holes):
        name = hole.get("name") or hole.get("id")
        try:
            bh = g_i.borehole(float(hole["x"]), float(hole.get("y", 0))) if is_3d else g_i.borehole(float(hole["x"]))
        except Exception as ex:
            log.fail("borehole " + name, ex)
            continue
        objects[hole.get("id")] = bh
        if _num(hole.get("head")):
            set_first(bh, ["Head"], float(hole["head"]), log, name, "head")
        layers = hole.get("layers") or []
        top = float(hole.get("top", 0))
        if layer_count == 0 and layers:
            level = top
            for layer in layers:
                try:
                    g_i.soillayer(max(0.01, level - float(layer["bottom"])))
                except Exception as ex:
                    log.fail("soil layer under {}".format(level), ex)
                level = float(layer["bottom"])
            layer_count = len(layers)
        levels = [top] + [float(l["bottom"]) for l in layers[:layer_count]]
        for i, level in enumerate(levels):
            try:
                g_i.setsoillayerlevel(bh, i, level)
            except Exception as ex:
                log.fail("{} level {}".format(name, i), ex)
        if len(layers) != layer_count:
            log.refuse(name, "layers", "{} layers given, the model has {}; boreholes must have the same layers (a layer can be 0 thick)".format(len(layers), layer_count))
        if index == 0:
            soil_layers = _list(lambda: g_i.SoilLayers)
            for i, layer in enumerate(layers[: len(soil_layers)]):
                mat = mats.get(layer.get("material"))
                if mat is None:
                    log.refuse(name, "layer {}".format(i + 1), "no material " + str(layer.get("material")))
                    continue
                try:
                    soil_layers[i].Soil.Material = mat
                except Exception as ex:
                    log.fail("layer {} material".format(i + 1), ex)
        log.ok("borehole " + name, bh, hole.get("id"))


def _list(fn):
    try:
        return list(fn())
    except Exception:
        return []


def _structures(g_i, model, mats, log, is_3d, objects):
    items = model.get("structures") or []
    if not items:
        return
    try:
        g_i.gotostructures()
    except Exception as ex:
        log.fail("structures mode", ex)
    for s in items:
        kind = s.get("type")
        name = s.get("name") or s.get("id")
        pts = [_pt(p, is_3d) for p in s.get("points") or []]
        try:
            obj = _make(g_i, s, kind, pts, is_3d)
        except Exception as ex:
            log.fail("{} {}".format(kind, name), ex)
            continue
        if obj is None:
            log.refuse(name, "type", "not made: " + str(kind))
            continue
        objects[s.get("id")] = obj
        mat = mats.get(s.get("material"))
        if mat is not None:
            target = obj
            if kind == "polygon":
                target = getattr(obj, "Soil", obj)
            try:
                target.Material = mat
            except Exception as ex:
                log.refuse(name, "material", _one_line(ex))
        _loads_values(obj, s, log, name, is_3d, None)
        if kind == "fixedanchor":
            angle = math.radians(float(s.get("angle", 0)))
            set_first(obj, ["Direction_x"], math.cos(angle), log, name, "direction")
            set_first(obj, ["Direction_y"], math.sin(angle), log, name, "direction")
            if _num(s.get("length")):
                set_first(obj, ["EquivalentLength"], float(s["length"]), log, name, "length")
        if s.get("interfaces") and kind in ("plate", "geogrid", "line"):
            line = getattr(obj, "_ppm_line", None) or getattr(obj, "Parent", None)
            for side in ("posinterface", "neginterface"):
                try:
                    getattr(g_i, side)(line if line is not None else obj)
                except Exception as ex:
                    log.fail("{} {}".format(name, side), ex)
        log.ok("{} {}".format(kind, name), obj, s.get("id"))


def _make(g_i, s, kind, pts, is_3d):
    if kind == "plate":
        if is_3d:
            if len(pts) == 2 and _num(s.get("depth")):
                (x1, y1, z1), (x2, y2, z2) = pts
                d = float(s["depth"])
                pts = [(x1, y1, z1), (x2, y2, z2), (x2, y2, z2 - d), (x1, y1, z1 - d)]
            return _pick(g_i.plate(*pts), "Plate")
        made = g_i.plate(*pts)
        plate = _pick(made, "Plate")
        line = _pick(made, "Line")
        try:
            object.__setattr__(plate, "_ppm_line", line)
        except Exception:
            pass
        return plate
    if kind == "anchor":
        return _pick(g_i.n2nanchor(*pts), "NodeToNodeAnchor")
    if kind == "fixedanchor":
        return _pick(g_i.fixedendanchor(*pts[:1]), "FixedEndAnchor")
    if kind == "geogrid":
        return _pick(g_i.geogrid(*pts), "Geogrid")
    if kind == "embedded":
        if is_3d:
            return _pick(g_i.embeddedbeam(*pts), "EmbeddedBeam")
        return _pick(g_i.embeddedbeamrow(*pts), "EmbeddedBeamRow")
    if kind == "interface":
        return _pick(g_i.posinterface(*pts) if s.get("side", "positive") == "positive" else g_i.neginterface(*pts), "Interface")
    if kind == "polygon":
        if is_3d:
            surface = _pick(g_i.surface(*pts), "Polygon")
            depth = float(s.get("depth", 1))
            return _pick(g_i.extrude(surface, 0, 0, -depth), "Volume")
        made = g_i.polygon(*pts)
        poly = _pick(made, "Polygon")
        soil = _pick(made, "Soil")
        try:
            object.__setattr__(poly, "Soil", soil)
        except Exception:
            pass
        return poly
    if kind == "lineload":
        return _pick(g_i.lineload(*pts), "LineLoad")
    if kind == "pointload":
        return _pick(g_i.pointload(*pts[:1]), "PointLoad")
    if kind == "surfaceload" and is_3d:
        return _pick(g_i.surfload(*pts), "SurfaceLoad")
    if kind == "linedisp":
        return _pick(g_i.linedispl(*pts), "LineDisplacement")
    return None


def _loads_values(obj, s, log, name, is_3d, phase):
    """A load's values (in the structures, or per phase when phase is given: Plaxis keeps a value per phase)."""
    kind = s.get("type")
    if kind not in ("lineload", "pointload", "surfaceload"):
        return
    if kind == "lineload":
        pairs = [("qx", ["qx_start"]), ("qy", ["qy_start"]), ("qz", ["qz_start"])]
    elif kind == "pointload":
        pairs = [("fx", ["Fx"]), ("fy", ["Fy"]), ("fz", ["Fz"])]
    else:
        pairs = [("qz", ["sigz", "sigmaz"])]
    values = s if phase is None else phase
    for field, names in pairs:
        if not _num(values.get(field)):
            continue
        if phase is None:
            set_first(obj, names, float(values[field]), log, name, field)


def _water(g_i, model, log, is_3d, objects):
    levels = model.get("waterlevels") or []
    if not levels:
        return
    try:
        g_i.gotoflow()
    except Exception as ex:
        log.fail("flow conditions mode", ex)
        return
    for w in levels:
        name = w.get("name") or w.get("id")
        pts = [_pt(p, is_3d) for p in w.get("points") or []]
        try:
            made = g_i.waterlevel(*pts)
            wl = _pick(made, "UserWaterLevel")
            objects[w.get("id")] = wl
            if w.get("name"):
                set_first(wl, ["Name"], name.replace(" ", "_"), Log(), name, "name")
            log.ok("water level " + name, wl, w.get("id"))
        except Exception as ex:
            log.fail("water level " + name, ex)


def _mesh(g_i, model, log, is_3d, objects):
    mesh = model.get("mesh") or {}
    try:
        g_i.gotomesh()
    except Exception as ex:
        log.fail("mesh mode", ex)
        return
    for r in mesh.get("refine") or []:
        obj = objects.get(r.get("object"))
        if obj is None or not _num(r.get("factor")):
            continue
        target = getattr(obj, "_ppm_line", None) or obj
        set_first(target, ["CoarsenessFactor"], float(r["factor"]), log, r.get("object"), "mesh refinement")
    size = mesh.get("factor") if _num(mesh.get("factor")) else MESH_SIZES.get(mesh.get("coarseness", "Medium"), 0.06)
    try:
        g_i.mesh(float(size))
        log.ok("mesh ({})".format(mesh.get("coarseness", "Medium")))
    except Exception as ex:
        log.fail("mesh", ex)


def _staged(g_i, design, label):
    """Plaxis's staged objects that come from a design object (a polygon's soil becomes Soil_7_1, Soil_7_2...)."""
    out = []
    if design is None:
        return out
    soil = getattr(design, "Soil", None)
    names = [_label(design)]
    if soil is not None:
        names.append(_label(soil))
    for coll in ("Soils", "Plates", "Geogrids", "EmbeddedBeamRows", "EmbeddedBeams", "NodeToNodeAnchors",
                 "FixedEndAnchors", "LineLoads", "PointLoads", "SurfaceLoads", "Interfaces"):
        for obj in _list(lambda c=coll: getattr(g_i, c)):
            n = _label(obj)
            if any(n == k + "_1" or n.startswith(k + "_") for k in names):
                out.append(obj)
    return out


def _phases(g_i, model, log, is_3d, objects):
    phases = model.get("phases") or []
    if not phases:
        return
    try:
        g_i.gotostages()
    except Exception as ex:
        log.fail("staged construction mode", ex)
        return
    structs = {s.get("id"): s for s in model.get("structures") or []}
    made = {}
    for index, ph in enumerate(phases):
        name = ph.get("name") or ph.get("id")
        try:
            if index == 0:
                phase = getattr(g_i, "InitialPhase", None) or _list(lambda: g_i.Phases)[0]
            else:
                parent = made.get(ph.get("parent")) or made.get(phases[index - 1].get("id"))
                phase = g_i.phase(parent)
        except Exception as ex:
            log.fail("phase " + name, ex)
            continue
        made[ph.get("id")] = phase
        set_first(phase, ["Identification"], name, log, name, "name")
        calc = ph.get("calc") or ("K0 procedure" if index == 0 else "Plastic")
        set_first(phase, ["DeformCalcType"], CALC_TYPES.get(calc, [calc])[0], Log(), name, "calculation") or \
            set_first(phase, ["DeformCalcType"], CALC_TYPES.get(calc, [calc])[-1], log, name, "calculation")
        if _num(ph.get("time")):
            set_first(phase, ["TimeInterval"], float(ph["time"]), log, name, "time")
        if ph.get("reset_displacements"):
            deform = getattr(phase, "Deform", None)
            if deform is not None:
                set_first(deform, ["ResetDisplacementsToZero"], True, log, name, "reset displacements")
        for ident, active in (ph.get("active") or {}).items():
            obj = objects.get(ident)
            if obj is None:
                continue
            what = "activate" if active else "deactivate"
            target = getattr(obj, "Soil", None) or obj
            try:
                getattr(g_i, what)(target, phase)
            except Exception as first:
                staged = _staged(g_i, obj, ident)
                if not staged:
                    log.fail("{}: {} {}".format(name, what, ident), first)
                    continue
                for st in staged:
                    try:
                        getattr(g_i, what)(st, phase)
                    except Exception as ex:
                        log.fail("{}: {} {}".format(name, what, _label(st)), ex)
        for ident, values in (ph.get("loads") or {}).items():
            obj = objects.get(ident)
            s = structs.get(ident) or {}
            if obj is None:
                continue
            fields = {"lineload": {"qx": "qx_start", "qy": "qy_start", "qz": "qz_start"},
                      "pointload": {"fx": "Fx", "fy": "Fy", "fz": "Fz"},
                      "surfaceload": {"qz": "sigz"}}.get(s.get("type"), {})
            for field, value in values.items():
                prop = fields.get(field)
                if prop is None or not _num(value):
                    continue
                done = False
                for target in [obj] + _staged(g_i, obj, ident):
                    try:
                        getattr(target, prop)[phase] = float(value)
                        done = True
                        break
                    except Exception:
                        continue
                if not done:
                    log.refuse(name, "{} {}".format(ident, field), "Plaxis did not take the value for this phase")
        wl = objects.get(ph.get("water")) if ph.get("water") else None
        if wl is not None:
            try:
                g_i.setglobalwaterlevel(wl, phase)
            except Exception as ex:
                log.fail(name + ": water level", ex)
        log.ok("phase " + name, phase, ph.get("id"))


# ---- Results -------------------------------------------------------------------------------------------------------

NODE_RESULTS_2D = [("Ux", ["Ux"]), ("Uy", ["Uy"]), ("Utot", ["Utot"]), ("GroundwaterHead", ["GroundwaterHead"])]
NODE_RESULTS_3D = [("Ux", ["Ux"]), ("Uy", ["Uy"]), ("Uz", ["Uz"]), ("Utot", ["Utot"])]
STRESS_RESULTS_2D = [
    ("SigxxE", ["SigxxE"]), ("SigyyE", ["SigyyE"]), ("SigzzE", ["SigzzE"]), ("Sigxy", ["Sigxy", "SigxyE"]),
    ("Sig1", ["SigmaEffective1"]), ("Sig3", ["SigmaEffective3"]),
    ("PActive", ["PActive"]), ("PExcess", ["PExcess"]), ("PSteady", ["PSteady"]),
    ("TauRel", ["RelativeShearStress", "TauRel"]), ("Plastic", ["PlasticPointType", "IsPlastic"]),
]
STRESS_RESULTS_3D = [
    ("SigxxE", ["SigxxE"]), ("SigyyE", ["SigyyE"]), ("SigzzE", ["SigzzE"]),
    ("Sig1", ["SigmaEffective1"]), ("Sig3", ["SigmaEffective3"]),
    ("PActive", ["PActive"]), ("PExcess", ["PExcess"]), ("TauRel", ["RelativeShearStress", "TauRel"]),
]
PLATE_RESULTS_2D = [("N", ["Nx2D", "N"]), ("Q", ["Q2D", "Q"]), ("M", ["M2D", "M"]), ("Ux", ["Ux"]), ("Uy", ["Uy"])]
PLATE_RESULTS_3D = [("N1", ["N1"]), ("N2", ["N2"]), ("M11", ["M11"]), ("M22", ["M22"]), ("Utot", ["Utot"])]
EMBEDDED_RESULTS = [("N", ["N", "Nx2D"]), ("Q", ["Q2D", "Q12"]), ("M", ["M2D", "M3"])]


# What "keep" can choose, by group: Calculate in Plaxis can leave out what a review does not need, so a big model's
# results file stays small.
RESULT_GROUPS = {
    "displacements": {"Ux", "Uy", "Uz", "Utot"},
    "stresses": {"SigxxE", "SigyyE", "SigzzE", "Sigxy", "Sig1", "Sig3", "TauRel"},
    "pore_pressures": {"PActive", "PExcess", "PSteady", "GroundwaterHead"},
    "plastic_points": {"Plastic"},
    "structures": set(),
}


def _kept(keep):
    """The result keys and phases to write (None: all)."""
    keep = keep or {}
    groups = keep.get("results")
    keys = None
    if groups:
        keys = set()
        for g in groups:
            keys |= RESULT_GROUPS.get(g, {g})
    phases = keep.get("phases")
    return keys, (set(str(p) for p in phases) if phases else None), (not groups or "structures" in groups)


def _rt(g_o, group, names):
    rt = getattr(g_o.ResultTypes, group, None)
    if rt is None:
        return None, None
    for n in names:
        t = getattr(rt, n, None)
        if t is not None:
            return t, n
    return None, None


def _round(values, digits=6):
    out = []
    for v in values:
        try:
            f = float(v)
            out.append(round(f, digits) if math.isfinite(f) else None)
        except (TypeError, ValueError):
            out.append(None)
    return out


def _get(g_o, args, missing, label):
    try:
        return list(g_o.getresults(*args))
    except Exception as ex:
        missing.append("{}: {}".format(label, _one_line(ex)))
        return None


def export(g_i, g_o, model, path, phases_info=None, plaxis_version="", most_nodes=200000, keep=None):
    """
    Reads every calculated phase from Plaxis Output into a results file: node displacements, stresses and pore
    pressures at stress points, and forces in plates, embedded beams and anchors. Everything is copied from Plaxis's
    own results; nothing is computed here. Returns a summary of what was written and what Plaxis did not give.
    keep: {"phases": [names or model ids], "results": [groups of RESULT_GROUPS]} to leave the rest out.
    """
    keys, keep_phases, keep_structures = _kept(keep)
    most_sp = most_nodes * 3  # a 3D mesh has about three stress points per node

    def cut(vals, most, what):
        if len(vals) > most:
            note = "{}: only the first {} of {} kept".format(what, most, len(vals))
            if note not in missing:
                missing.append(note)
        return vals[:most]
    ids = {str(p.get("name")): str(p.get("id")) for p in (model.get("phases") or [])}
    is_3d = model.get("product") == "3D"
    started = time.time()
    phases_out = []
    missing = []
    out_phases = _list(lambda: g_o.Phases)
    info = {p.get("name"): p for p in (phases_info or [])}
    for phase in out_phases:
        name = _label(phase)
        ident = name
        try:
            ident = getattr(phase, "Identification").value
        except Exception:
            pass
        entry = {"name": ident, "plaxis": name}
        entry.update({k: v for k, v in (info.get(ident) or {}).items() if k != "name"})
        if keep_phases is not None and ident not in keep_phases and ids.get(ident) not in keep_phases:
            entry["skipped"] = "left out of the results file, as chosen before the run"
            phases_out.append(entry)
            continue
        coords = ["X", "Y", "Z"] if is_3d else ["X", "Y"]
        nodes = {}
        for c in coords:
            t, _ = _rt(g_o, "Soil", [c])
            vals = _get(g_o, (phase, t, "node"), missing, ident + " node " + c) if t is not None else None
            if vals is None:
                break
            nodes[c.lower()] = _round(cut(vals, most_nodes, ident + " nodes"), 4)
        if len(nodes) != len(coords):
            entry["skipped"] = "no node coordinates (is this phase calculated?)"
            phases_out.append(entry)
            continue
        entry["nodes"] = nodes
        entry["node"] = {}
        for key, names in (NODE_RESULTS_3D if is_3d else NODE_RESULTS_2D):
            if keys is not None and key not in keys:
                continue
            t, _ = _rt(g_o, "Soil", names)
            if t is None:
                missing.append("{}: Soil.{} not in this Plaxis".format(ident, names[0]))
                continue
            vals = _get(g_o, (phase, t, "node"), missing, ident + " " + key)
            if vals is not None:
                entry["node"][key] = _round(cut(vals, most_nodes, ident + " nodes"))
        sp = {}
        stress_keys = [(k, n) for k, n in (STRESS_RESULTS_3D if is_3d else STRESS_RESULTS_2D) if keys is None or k in keys]
        for c in (coords if stress_keys else []):
            t, _ = _rt(g_o, "Soil", [c])
            vals = _get(g_o, (phase, t, "stresspoint"), missing, ident + " stress point " + c) if t is not None else None
            if vals is not None:
                sp[c.lower()] = _round(cut(vals, most_sp, ident + " stress points"), 4)
        if stress_keys and len(sp) == len(coords):
            for key, names in stress_keys:
                t, _ = _rt(g_o, "Soil", names)
                if t is None:
                    missing.append("{}: Soil.{} not in this Plaxis".format(ident, names[0]))
                    continue
                vals = _get(g_o, (phase, t, "stresspoint"), missing, ident + " " + key)
                if vals is not None:
                    sp[key] = _round(cut(vals, most_sp, ident + " stress points"), 3)
            entry["sp"] = sp
        if not keep_structures:
            phases_out.append(entry)
            continue
        entry["plates"] = _members(g_o, phase, "Plate", "Plates", PLATE_RESULTS_3D if is_3d else PLATE_RESULTS_2D, coords, missing, ident)
        entry["embedded"] = _members(g_o, phase, "EmbeddedBeam" if is_3d else "EmbeddedBeamRow",
                                     "EmbeddedBeams" if is_3d else "EmbeddedBeamRows", EMBEDDED_RESULTS, coords, missing, ident)
        entry["anchors"] = _anchors(g_o, phase, missing, ident)
        phases_out.append(entry)
    pack = {
        "format": "ppr", "version": FORMAT_VERSION, "title": model.get("title") or "Untitled",
        "product": model.get("product", "2D"), "plaxis": plaxis_version,
        "created": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "made_with": "Plaxis ProMax",
        "units": model.get("units") or {"length": "m", "force": "kN", "time": "day"},
        "model": model, "phases": phases_out, "missing": missing[:200],
    }
    if keep:
        pack["kept"] = keep
    size = write_pack(pack, path)
    return {
        "path": path, "phases": [p["name"] for p in phases_out if "nodes" in p],
        "skipped": [{"phase": p["name"], "why": p["skipped"]} for p in phases_out if "skipped" in p],
        "nodes": max([len(p.get("nodes", {}).get("x", [])) for p in phases_out] or [0]),
        "missing": missing[:40], "seconds": round(time.time() - started, 1), "bytes": size,
    }


# ---- The results file -----------------------------------------------------------------------------------------------
#
# Version 2, made small for big models: gzip of
#   "PPR2", the length of the header (4 bytes, little-endian), the header (JSON), then the numbers.
# In the header each long list of numbers under "phases" points into the numbers, stored byte plane by byte plane (all
# first bytes, then all second bytes...), which gzip packs far better than the numbers as they are:
#   {"$f": [offset, count]}            4-byte floats (coordinates, structure forces): NaN is a missing value.
#   {"$q": [offset, count, low, step]} 2-byte steps (soil results at nodes and stress points): value = low + q * step,
#                                      65535 is a missing value. 65534 steps over each list's own range, so a value is
#                                      within 1/131068 of that list's range of Plaxis's (0.0002 mm on a 30 mm range).
# A list that is the same as one already written (node coordinates, phase after phase) is written once and pointed at
# again. Version 1 (plain gzip JSON) still opens.

PACK_MAGIC = b"PPR2"
_PACKED_MIN = 8


def _numbers(v):
    return isinstance(v, list) and len(v) >= _PACKED_MIN and all(
        x is None or (isinstance(x, (int, float)) and not isinstance(x, bool)) for x in v)


_QUANTISED = ("node", "sp")
_COORDS = ("x", "y", "z")
_Q_MISSING = 65535
_Q_TOP = 65534


def write_pack(pack, path):
    """Writes a results file (version 2). Returns its size in bytes."""
    blob = bytearray()
    seen = {}

    def store(raw, width):
        planes = b"".join(raw[i::width] for i in range(width))
        key = hashlib.sha1(planes + bytes([width])).digest()
        if key not in seen:
            seen[key] = len(blob)
            blob.extend(planes)
        return seen[key]

    def floats(values):
        f = array.array("f", (float("nan") if x is None else float(x) for x in values))
        if sys.byteorder != "little":
            f.byteswap()
        return {"$f": [store(f.tobytes(), 4), len(values)]}

    def steps(values):
        real = [float(x) for x in values if x is not None and math.isfinite(float(x))]
        low = min(real) if real else 0.0
        high = max(real) if real else 0.0
        step = (high - low) / _Q_TOP if high > low else 1.0
        q = array.array("H", (_Q_MISSING if x is None or not math.isfinite(float(x)) else int(round((float(x) - low) / step)) for x in values))
        if sys.byteorder != "little":
            q.byteswap()
        return {"$q": [store(q.tobytes(), 2), len(values), low, step]}

    def walk(v, parent=None, key=None):
        if _numbers(v):
            return steps(v) if parent in _QUANTISED and key not in _COORDS else floats(v)
        if isinstance(v, dict):
            return {k: walk(x, key, k) for k, x in v.items()}
        if isinstance(v, list):
            return [walk(x, parent, key) for x in v]
        return v

    head = dict(pack)
    head["phases"] = walk(pack.get("phases") or [])
    text = json.dumps(head, separators=(",", ":")).encode("utf-8")
    with open(path, "wb") as raw_file:
        with gzip.GzipFile(fileobj=raw_file, mode="wb", compresslevel=6) as f:
            f.write(PACK_MAGIC)
            f.write(struct.pack("<I", len(text)))
            f.write(text)
            f.write(bytes(blob))
        return raw_file.tell()


def read_pack(path):
    """Reads a results file, version 1 or 2, into plain lists (missing values back to None)."""
    with gzip.open(path, "rb") as f:
        data = f.read()
    if not data.startswith(PACK_MAGIC):
        return json.loads(data.decode("utf-8"))
    n = struct.unpack("<I", data[4:8])[0]
    head = json.loads(data[8:8 + n].decode("utf-8"))
    blob = data[8 + n:]

    def planes(offset, count, width, code):
        part = blob[offset:offset + width * count]
        raw = bytearray(width * count)
        for i in range(width):
            raw[i::width] = part[i * count:(i + 1) * count]
        a = array.array(code)
        a.frombytes(bytes(raw))
        if sys.byteorder != "little":
            a.byteswap()
        return a

    def walk(v):
        if isinstance(v, dict):
            if set(v) == {"$f"}:
                return [None if math.isnan(x) else float("%.7g" % x) for x in planes(v["$f"][0], v["$f"][1], 4, "f")]
            if set(v) == {"$q"}:
                offset, count, low, step = v["$q"]
                return [None if q == _Q_MISSING else float("%.7g" % (low + q * step)) for q in planes(offset, count, 2, "H")]
            return {k: walk(x) for k, x in v.items()}
        if isinstance(v, list):
            return [walk(x) for x in v]
        return v

    head["phases"] = walk(head.get("phases") or [])
    return head


def _members(g_o, phase, group, collection, results, coords, missing, ident):
    out = []
    for obj in _list(lambda: getattr(g_o, collection)):
        row = {"name": _label(obj)}
        ok = True
        for c in coords:
            t, _ = _rt(g_o, group, [c])
            vals = _get(g_o, (obj, phase, t, "node"), missing, "{} {} {}".format(ident, row["name"], c)) if t is not None else None
            if vals is None:
                ok = False
                break
            row[c.lower()] = _round(vals, 4)
        if not ok:
            continue
        for key, names in results:
            t, _ = _rt(g_o, group, names)
            if t is None:
                continue
            vals = _get(g_o, (obj, phase, t, "node"), missing, "{} {} {}".format(ident, row["name"], key))
            if vals is not None:
                row[key] = _round(vals, 3)
        out.append(row)
    return out


def _anchors(g_o, phase, missing, ident):
    out = []
    for coll, group, names in (("NodeToNodeAnchors", "NodeToNodeAnchor", ["AnchorForce2D", "AnchorForce3D"]),
                               ("FixedEndAnchors", "FixedEndAnchor", ["AnchorForce2D", "AnchorForce3D"])):
        for obj in _list(lambda c=coll: getattr(g_o, c)):
            t, _ = _rt(g_o, group, names)
            if t is None:
                continue
            vals = _get(g_o, (obj, phase, t, "node"), missing, "{} {}".format(ident, _label(obj)))
            if vals:
                out.append({"name": _label(obj), "kind": group, "F": _round(vals[:1], 3)[0]})
    return out


# ---- Stand-alone script ------------------------------------------------------------------------------------------

def standalone_main(namespace, model):
    """
    What the exported script runs in Plaxis Input (Expert > Python > Run script): Plaxis hands it the scripting
    server's port and password; it builds the model in a new project and says what it did.
    """
    from plxscripting.easy import new_server
    port = namespace.get("AppServerPort") or 10000
    password = namespace.get("AppServerPassword") or ""
    s_i, g_i = new_server("localhost", port, password=password)
    answer, _ = build(s_i, g_i, model)
    print("Plaxis ProMax: built '{}': {} objects made.".format(model.get("title"), len(answer["made"])))
    for r in answer["refused"]:
        print("  not set: {} {}: {}".format(r["object"], r["value"], r["why"]))
    for f in answer["failed"]:
        print("  failed: {}: {}".format(f["step"], f["why"]))
