"""
Plaxis ProMax's worker: the only part that talks to Plaxis.

It runs on Plaxis's own Python (which ships Bentley's plxscripting package) and is started by the Plaxis ProMax
window. It reads one JSON request per line on stdin and writes one JSON answer per line on stdout:

    {"id": 1, "op": "status", "args": {}}          ->  {"id": 1, "ok": true, "result": {...}}
                                                   ->  {"id": 1, "ok": false, "error": "..."}

Every op is a plain function below. Answers carry what changed (names, counts, values) so the chat can check its own
work instead of claiming it (Sketch ProMax bugs 95, 96, 123, 132). Lists are paged (bug 78).

Nothing here waits on a person: no input(), no dialogs. The window enforces the time limit on each request.
"""

import contextlib
import io
import json
import os
import re
import sys
import time
import traceback

PROTOCOL = 1
MAX_LIST = 200

_state = {
    "address": "localhost",
    "input_port": None,
    "password": "",
    "s_i": None,
    "g_i": None,
    "s_o": None,
    "g_o": None,
    "output_port": None,
}


class UserError(Exception):
    """A problem the chat can act on: said in one line, without a traceback."""


# ---- Connection ---------------------------------------------------------------------------------------------------


def _new_server(port):
    try:
        from plxscripting.easy import new_server
    except ImportError:
        raise UserError(
            "This Python has no plxscripting. Plaxis ProMax must run on Plaxis's own Python "
            "(Settings > Plaxis Python)."
        )
    try:
        return new_server(_state["address"], port, password=_state["password"])
    except Exception as ex:  # plxscripting raises plain Exceptions for refused connections and bad passwords
        raise UserError(
            "Plaxis didn't answer on port {}: {}. In Plaxis Input, open Expert > Configure remote scripting server, "
            "start the server, and check the port and password.".format(port, _one_line(ex))
        )


def op_connect(args):
    """Connects to Plaxis Input (port and password as Plaxis set them)."""
    _state["address"] = args.get("address") or "localhost"
    _state["input_port"] = int(args.get("input_port") or 10000)
    _state["password"] = args.get("password") or ""
    _state["s_o"] = _state["g_o"] = _state["output_port"] = None
    s_i, g_i = _new_server(_state["input_port"])
    _state["s_i"], _state["g_i"] = s_i, g_i
    return op_status({})


def _input():
    if _state["g_i"] is None:
        if _state["input_port"] is None:
            raise UserError("Not connected to Plaxis yet.")
        _state["s_i"], _state["g_i"] = _new_server(_state["input_port"])
    return _state["s_i"], _state["g_i"]


def _output_for(phase):
    """Opens Output on a phase (g_i.view) and connects to it; reused while the port stays the same."""
    s_i, g_i = _input()
    port = g_i.view(phase)
    try:
        port = int(port)
    except (TypeError, ValueError):
        raise UserError("Plaxis didn't open Output for that phase (is it calculated?).")
    if _state["g_o"] is None or _state["output_port"] != port:
        _state["s_o"], _state["g_o"] = _new_server(port)
        _state["output_port"] = port
    return _state["s_o"], _state["g_o"]


def op_status(args):
    """What Plaxis is open: 2D or 3D, version, project, and the phases."""
    s_i, g_i = _input()
    full = _safe(lambda: s_i.server_full_name, "")
    info = {
        "connected": True,
        "server": full,
        "product": "PLAXIS 3D" if _safe(lambda: s_i.is_3d, False) else ("PLAXIS 2D" if _safe(lambda: s_i.is_2d, False) else "unknown"),
        "version": "{}.{}".format(_safe(lambda: s_i.major_version, "?"), _safe(lambda: s_i.minor_version, "?")),
        "python": sys.version.split()[0],
        "worker_protocol": PROTOCOL,
    }
    project = _safe(lambda: g_i.Project, None)
    if project is not None:
        for name in ("Title", "Filename", "Company", "UnitLength", "UnitForce", "UnitTime"):
            value = _value_of(project, name)
            if value is not None:
                info[name[0].lower() + name[1:]] = value
    phases = _safe(lambda: list(g_i.Phases), [])
    info["phases"] = [_label(p) for p in phases[:MAX_LIST]]
    return info


# ---- Looking at the model -----------------------------------------------------------------------------------------

# The collections a Plaxis model has; each version and product has some of them.
COLLECTIONS = [
    "Phases", "Materials", "Boreholes", "Soils", "SoilLayers", "Points", "Lines", "Polygons", "Surfaces", "Volumes",
    "Plates", "Beams", "Geogrids", "Interfaces", "NodeToNodeAnchors", "FixedEndAnchors", "EmbeddedBeams",
    "EmbeddedBeamRows", "Wells", "Drains", "LineLoads", "PointLoads", "SurfaceLoads", "LineDisplacements",
    "PointDisplacements", "SurfaceDisplacements", "WaterLevels", "Waterlevels", "GroundwaterFlowBCs", "Tunnels",
]


def op_list_model(args):
    """Every collection of the open model with its count and the first names (paged)."""
    s_i, g_i = _input()
    only = set(args.get("collections") or [])
    limit = min(int(args.get("limit") or 30), MAX_LIST)
    out = {}
    for name in COLLECTIONS:
        if only and name not in only:
            continue
        items = _safe(lambda: list(getattr(g_i, name)), None)
        if items is None:
            continue
        out[name] = {"count": len(items), "names": [_label(i) for i in items[:limit]]}
        if len(items) > limit:
            out[name]["more"] = len(items) - limit
    return {"collections": out}


_PATH_PART = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)(?:\[(-?\d+)\])?")


def _resolve(path):
    """'Phases[1].PreviousPhase' from g_i, or 'output:ResultTypes.Soil' from g_o. No eval: attribute and index steps only."""
    if path.startswith("output:"):
        if _state["g_o"] is None:
            raise UserError("Output isn't connected: read results with get_results first, then inspect output: paths.")
        obj, path = _state["g_o"], path[len("output:"):]
    else:
        obj = _input()[1]
    walked = []
    for part in [p for p in path.split(".") if p.strip()]:
        m = _PATH_PART.fullmatch(part.strip())
        if not m:
            raise UserError("Can't read '{}': use names and [index] only, like Phases[1].Identification.".format(part))
        name, index = m.group(1), m.group(2)
        if name.startswith("_"):
            raise UserError("Can't read '{}': names starting with _ belong to Python, not Plaxis.".format(name))
        try:
            obj = getattr(obj, name)
        except AttributeError:
            raise UserError("'{}' has no '{}'. It has: {}.".format(".".join(walked) or "g_i", name, ", ".join(_members(obj)[:40])))
        walked.append(name)
        if index is not None:
            items = list(obj)
            i = int(index)
            if not -len(items) <= i < len(items):
                raise UserError("{} has {} items; [{}] is outside.".format(".".join(walked), len(items), i))
            obj = items[i]
            walked[-1] += "[{}]".format(i)
    return obj


def op_inspect(args):
    """An object's properties (with values), its commands and its items, so the chat reads Plaxis instead of guessing."""
    path = (args.get("path") or "").strip()
    obj = _resolve(path) if path else _input()[1]
    limit = min(int(args.get("limit") or 80), MAX_LIST)
    result = {"path": path or "g_i", "label": _label(obj), "type": type(obj).__name__}
    value = _plain(obj)
    if value is not _NOT_PLAIN:
        result["value"] = value
        return result
    items = _safe(lambda: list(obj), None) if _is_listable(obj) else None
    if items is not None:
        result["count"] = len(items)
        result["items"] = [_label(i) for i in items[:limit]]
    props, commands = {}, []
    for name in _members(obj):
        if len(props) + len(commands) >= limit * 2:
            result["truncated"] = True
            break
        member = _safe(lambda: getattr(obj, name), None)
        if member is None:
            continue
        if callable(member) and not hasattr(member, "value"):
            commands.append(name)
            continue
        v = _value_of(obj, name)
        props[name] = v if v is not None else _label(member)
    result["properties"] = props
    result["commands"] = commands
    return result


# ---- Changing the model -------------------------------------------------------------------------------------------


def op_run_commands(args):
    """Plaxis command-line commands, one after the other; stops at the first that fails and says which."""
    s_i, g_i = _input()
    commands = [c for c in (args.get("commands") or []) if isinstance(c, str) and c.strip()]
    if not commands:
        raise UserError("No commands given.")
    done = []
    for cmd in commands:
        try:
            answer = s_i.call_and_handle_command(cmd)
        except Exception as ex:
            return {"done": done, "failed": cmd, "error": _one_line(ex), "not_run": commands[len(done) + 1:]}
        done.append({"command": cmd, "result": _label_any(answer), "feedback": _one_line(_safe(lambda: s_i.last_response, ""))})
    return {"done": done}


def op_run_code(args):
    """
    Python the chat wrote (or kept code), run with g_i and s_i, and helpers: output(phase) connects Output and returns
    (s_o, g_o); label(obj) names an object. Whatever it prints comes back, and the value of `result` if it sets one.
    """
    s_i, g_i = _input()
    code = args.get("code") or ""
    if not code.strip():
        raise UserError("No code given.")
    namespace = {
        "s_i": s_i, "g_i": g_i, "s_o": _state["s_o"], "g_o": _state["g_o"],
        "output": _output_for, "label": _label, "inputs": args.get("inputs") or {}, "result": None,
    }
    printed = io.StringIO()
    started = time.time()
    try:
        with contextlib.redirect_stdout(printed):
            exec(compile(code, "<plaxis_promax>", "exec"), namespace)
    except Exception as ex:
        tb = traceback.extract_tb(sys.exc_info()[2])
        line = next((f.lineno for f in reversed(tb) if f.filename == "<plaxis_promax>"), None)
        return {"ok": False, "error": _one_line(ex), "line": line, "printed": _cut(printed.getvalue()), "hint": _hint(ex, namespace)}
    finally:
        if namespace.get("g_o") is not None:
            _state["g_o"] = namespace["g_o"]
    return {"ok": True, "printed": _cut(printed.getvalue()), "result": _label_any(namespace.get("result")), "seconds": round(time.time() - started, 1)}


def op_new_project(args):
    s_i, g_i = _input()
    s_i.new()
    _state["s_o"] = _state["g_o"] = _state["output_port"] = None
    return op_status({})


def op_open_project(args):
    s_i, g_i = _input()
    path = args.get("path") or ""
    if not os.path.exists(path):
        raise UserError("There is no project at {}.".format(path))
    s_i.open(path)
    _state["s_o"] = _state["g_o"] = _state["output_port"] = None
    return op_status({})


def op_save_project(args):
    s_i, g_i = _input()
    path = args.get("path")
    if path:
        g_i.save(path)
    else:
        g_i.save()
    return {"saved": path or _value_of(g_i.Project, "Filename") or "(the project's own file)"}


def op_undo(args):
    s_i, g_i = _input()
    steps = max(1, min(int(args.get("steps") or 1), 50))
    done = 0
    for _ in range(steps):
        try:
            s_i.call_and_handle_command("undo")
        except Exception as ex:
            return {"undone": done, "stopped": _one_line(ex)}
        done += 1
    return {"undone": done}


MODES = {"soil": "gotosoil", "structures": "gotostructures", "mesh": "gotomesh", "flow": "gotoflow", "stages": "gotostages"}


def op_go_to_mode(args):
    s_i, g_i = _input()
    mode = (args.get("mode") or "").lower()
    if mode not in MODES:
        raise UserError("Mode is one of: {}.".format(", ".join(MODES)))
    getattr(g_i, MODES[mode])()
    return {"mode": mode}


def op_soil_contour(args):
    s_i, g_i = _input()
    xmin, xmax = float(args["xmin"]), float(args["xmax"])
    ymin, ymax = float(args["ymin"]), float(args["ymax"])
    g_i.gotosoil()
    g_i.SoilContour.initializerectangular(xmin, ymin, xmax, ymax)
    return {"contour": {"xmin": xmin, "ymin": ymin, "xmax": xmax, "ymax": ymax}}


# Plaxis renamed a material's name property: MaterialName in recent versions, Identification before.
NAME_PROPERTIES = ("MaterialName", "Identification")


def op_create_soil_material(args):
    """A soil material with its properties; reports the ones Plaxis refused by name, so nothing fails silently."""
    s_i, g_i = _input()
    name = args.get("name") or ""
    props = dict(args.get("properties") or {})
    if args.get("model") is not None:
        props.setdefault("SoilModel", args["model"])
    existing = [m for m in _safe(lambda: list(g_i.Materials), []) if _label(m) == name]
    if existing and not args.get("replace_existing"):
        raise UserError("A material named '{}' already exists. Pick another name, or pass replace_existing.".format(name))
    mat = g_i.soilmat()
    named = False
    for key in NAME_PROPERTIES:
        try:
            mat.setproperties(key, name)
            named = True
            break
        except Exception:
            continue
    refused = {}
    for key, value in props.items():
        try:
            mat.setproperties(key, value)
        except Exception as ex:
            refused[key] = _one_line(ex)
    return {"material": _label(mat), "named": named, "set": [k for k in props if k not in refused], "refused": refused}


def op_create_boreholes(args):
    """
    Boreholes with soil layers, given top-down: the borehole's top (ground level) and each layer's bottom level and
    material. The first borehole makes the layers when the model has none; every borehole sets its own levels (level 0
    is the top of layer 1, level i the bottom of layer i). Head is the water level in the borehole.
    """
    s_i, g_i = _input()
    holes = args.get("boreholes") or []
    if not holes:
        raise UserError("No boreholes given.")
    g_i.gotosoil()
    is_3d = _safe(lambda: s_i.is_3d, False)
    materials = {_label(m): m for m in _safe(lambda: list(g_i.Materials), [])}
    layer_count = len(_safe(lambda: list(g_i.SoilLayers), []))
    made = []
    for h_index, hole in enumerate(holes):
        x = float(hole["x"])
        bh = g_i.borehole(x, float(hole.get("y", 0))) if is_3d else g_i.borehole(x)
        if hole.get("head") is not None:
            _set(bh, "Head", float(hole["head"]))
        layers = hole.get("layers") or []
        top = float(hole.get("top", 0))
        notes = []
        if layer_count == 0 and layers:
            level = top
            for layer in layers:
                g_i.soillayer(max(0.01, level - float(layer["bottom"])))
                level = float(layer["bottom"])
            layer_count = len(layers)
        if len(layers) != layer_count:
            notes.append("{} layers given, the model has {}: only the first {} were set".format(len(layers), layer_count, min(len(layers), layer_count)))
        levels = [top] + [float(l["bottom"]) for l in layers[:layer_count]]
        for i, level in enumerate(levels):
            try:
                g_i.setsoillayerlevel(bh, i, level)
            except Exception as ex:
                notes.append("level {} ({}): {}".format(i, level, _one_line(ex)))
        if h_index == 0:
            soil_layers = _safe(lambda: list(g_i.SoilLayers), [])
            for i, layer in enumerate(layers[: len(soil_layers)]):
                mat = materials.get(layer.get("material") or "")
                if mat is None:
                    notes.append("layer {}: no material named '{}'".format(i + 1, layer.get("material")))
                    continue
                try:
                    soil_layers[i].Soil.Material = mat
                except Exception as ex:
                    notes.append("layer {} material: {}".format(i + 1, _one_line(ex)))
        made.append({"borehole": _label(bh), "x": x, "top": top, "layers": len(layers), "notes": notes})
    return {"boreholes": made, "soil_layers": layer_count}


def op_generate_mesh(args):
    s_i, g_i = _input()
    g_i.gotomesh()
    coarseness = args.get("coarseness")
    answer = g_i.mesh(float(coarseness)) if coarseness is not None else g_i.mesh()
    return {"meshed": True, "answer": _label_any(answer), "feedback": _one_line(_safe(lambda: s_i.last_response, ""))}


PHASE_FIELDS = ("Identification", "Name", "PreviousPhase", "DeformCalcType", "PorePresCalcType", "ShouldCalculate", "CalculationResult", "MaxCores")


def op_list_phases(args):
    s_i, g_i = _input()
    out = []
    for p in _safe(lambda: list(g_i.Phases), [])[:MAX_LIST]:
        row = {"label": _label(p)}
        for f in PHASE_FIELDS:
            v = _value_of(p, f)
            if v is not None:
                row[f] = v
        out.append(row)
    return {"phases": out}


def _phase(g_i, name):
    phases = _safe(lambda: list(g_i.Phases), [])
    if name in (None, "", "last"):
        if not phases:
            raise UserError("The project has no phases.")
        return phases[-1]
    for p in phases:
        if name in (_label(p), _value_of(p, "Identification"), _value_of(p, "Name")):
            return p
    raise UserError("No phase named '{}'. The phases are: {}.".format(name, ", ".join(_label(p) for p in phases)))


def op_add_phase(args):
    s_i, g_i = _input()
    g_i.gotostages()
    previous = _phase(g_i, args.get("after"))
    phase = g_i.phase(previous)
    if args.get("name"):
        _set(phase, "Identification", args["name"])
    refused = {}
    for key, value in (args.get("settings") or {}).items():
        try:
            _set(phase, key, value)
        except Exception as ex:
            refused[key] = _one_line(ex)
    return {"phase": _label(phase), "after": _label(previous), "refused": refused}


def op_calculate(args):
    """Calculates (the named phases, or every phase set to calculate) and reads back how each phase ended."""
    s_i, g_i = _input()
    names = args.get("phases") or []
    if names:
        wanted = [_phase(g_i, n) for n in names]
        for p in _safe(lambda: list(g_i.Phases), []):
            try:
                _set(p, "ShouldCalculate", p in wanted)
            except Exception:
                pass
    started = time.time()
    g_i.gotostages()
    answer = g_i.calculate()
    phases = op_list_phases({})["phases"]
    return {"seconds": round(time.time() - started, 1), "answer": _label_any(answer), "phases": phases}


def op_get_results(args):
    """
    One or more result types for a phase, as figures (count, min, max, mean) and the first values; all of them to a
    CSV file when asked. Result types are paths under Output's ResultTypes, like Soil.Utot or Plate.M2D.
    """
    s_i, g_i = _input()
    phase_in = _phase(g_i, args.get("phase"))
    s_o, g_o = _output_for(phase_in)
    target = _label(phase_in)
    phase_out = None
    for p in _safe(lambda: list(g_o.Phases), []):
        if target in (_label(p), _value_of(p, "Identification"), _value_of(p, "Name")):
            phase_out = p
    if phase_out is None:
        raise UserError("Output has no phase '{}' (calculate it first).".format(target))
    location = args.get("location") or "node"
    obj = _resolve_output_object(g_o, args.get("object"))
    columns = {}
    for path in args.get("result_types") or ["Soil.Utot"]:
        rt = g_o.ResultTypes
        for part in path.split("."):
            try:
                rt = getattr(rt, part)
            except AttributeError:
                raise UserError("ResultTypes has no '{}'. Inspect output:ResultTypes to see the names.".format(path))
        values = g_o.getresults(obj, phase_out, rt, location) if obj is not None else g_o.getresults(phase_out, rt, location)
        columns[path] = [float(v) for v in values]
    figures = {}
    for path, values in columns.items():
        if values:
            figures[path] = {"count": len(values), "min": min(values), "max": max(values), "mean": sum(values) / len(values),
                             "max_abs": max(abs(v) for v in values)}
        else:
            figures[path] = {"count": 0}
    result = {"phase": target, "location": location, "object": args.get("object"), "figures": figures}
    first = min(int(args.get("first") or 0), 50)
    if first:
        result["first"] = {path: values[:first] for path, values in columns.items()}
    if args.get("csv_path"):
        path = args["csv_path"]
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        names = list(columns)
        rows = max((len(v) for v in columns.values()), default=0)
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(",".join(names) + "\n")
            for i in range(rows):
                f.write(",".join(repr(columns[n][i]) if i < len(columns[n]) else "" for n in names) + "\n")
        result["csv"] = path
    return result


def _resolve_output_object(g_o, name):
    if not name:
        return None
    for collection in COLLECTIONS:
        for item in _safe(lambda: list(getattr(g_o, collection)), []):
            if _label(item) == name:
                return item
    raise UserError("Output has no object named '{}'.".format(name))


# ---- Helpers ------------------------------------------------------------------------------------------------------

_NOT_PLAIN = object()


def _safe(fn, fallback):
    try:
        return fn()
    except Exception:
        return fallback


def _members(obj):
    return sorted(n for n in _safe(lambda: dir(obj), []) if not n.startswith("_") and n not in _PY_NOISE)


_PY_NOISE = {"attr_cache", "get_cmd_line_repr", "get_equivalent", "selection", "value", "echo", "info", "commands"}


def _is_listable(obj):
    return hasattr(obj, "__iter__") and not isinstance(obj, (str, bytes, dict))


def _plain(obj):
    if obj is None or isinstance(obj, (bool, int, float, str)):
        return obj
    return _NOT_PLAIN


def _value_of(obj, name):
    """A property's plain value (number, text, true/false) or an object's name; None when there is none."""
    member = _safe(lambda: getattr(obj, name), None)
    if member is None:
        return None
    if hasattr(member, "value"):
        v = _safe(lambda: member.value, None)
        plain = _plain(v)
        return plain if plain is not _NOT_PLAIN else _label(v)
    plain = _plain(member)
    return plain if plain is not _NOT_PLAIN else None


def _label(obj):
    """How the chat names an object: its Plaxis name when it has one."""
    plain = _plain(obj)
    if plain is not _NOT_PLAIN:
        return plain
    # Phases and materials show their Identification (or MaterialName); Name is Plaxis's internal name (Phase_1).
    for name in ("Identification", "MaterialName", "Name"):
        v = _safe(lambda: getattr(obj, name).value, None)
        if isinstance(v, str) and v:
            return v
    return _safe(lambda: repr(obj), "?")


def _label_any(value):
    if isinstance(value, (list, tuple)):
        return [_label_any(v) for v in list(value)[:MAX_LIST]]
    if isinstance(value, dict):
        return {str(k): _label_any(v) for k, v in list(value.items())[:MAX_LIST]}
    return _label(value)


def _set(obj, name, value):
    member = getattr(obj, name)
    if hasattr(member, "set"):
        member.set(value)
    else:
        setattr(obj, name, value)


def _one_line(ex):
    text = str(ex).strip().replace("\r", "")
    lines = [l.strip() for l in text.split("\n") if l.strip()]
    return " ".join(lines)[:600] if lines else type(ex).__name__


def _cut(text, limit=12000):
    return text if len(text) <= limit else text[:limit] + "\n... ({} more characters)".format(len(text) - limit)


def _hint(ex, namespace):
    """When an attribute is missing, the real names it could have been: read them instead of guessing again."""
    m = re.search(r"attribute '([A-Za-z_0-9]+)'", str(ex))
    if not m:
        return None
    wanted = m.group(1).lower()
    names = set()
    for root in ("g_i", "g_o"):
        obj = namespace.get(root)
        if obj is not None:
            names.update(_safe(lambda: dir(obj), []))
    close = sorted(n for n in names if wanted[:4] in n.lower())[:15]
    return "Close names: " + ", ".join(close) if close else None


OPS = {name[3:]: fn for name, fn in globals().items() if name.startswith("op_") and callable(fn)}


def handle(request):
    rid = request.get("id")
    op = request.get("op") or ""
    fn = OPS.get(op)
    if fn is None:
        return {"id": rid, "ok": False, "error": "Unknown op '{}'.".format(op)}
    try:
        return {"id": rid, "ok": True, "result": fn(request.get("args") or {})}
    except UserError as ex:
        return {"id": rid, "ok": False, "error": str(ex)}
    except KeyError as ex:
        return {"id": rid, "ok": False, "error": "Missing argument {}.".format(ex)}
    except Exception as ex:
        if _looks_disconnected(ex):
            _state["s_i"] = _state["g_i"] = _state["s_o"] = _state["g_o"] = None
            return {"id": rid, "ok": False, "error": "Lost the connection to Plaxis ({}). Is Plaxis Input still open?".format(_one_line(ex))}
        return {"id": rid, "ok": False, "error": "{}: {}".format(type(ex).__name__, _one_line(ex))}


def _looks_disconnected(ex):
    text = (type(ex).__name__ + " " + str(ex)).lower()
    return any(w in text for w in ("connectionerror", "connection refused", "max retries", "connection aborted", "remotedisconnected"))


def main():
    stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")
    out = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="\n", write_through=True)
    # Anything a library prints must not break the protocol: plain prints go to stderr.
    sys.stdout = sys.stderr
    out.write(json.dumps({"ready": True, "protocol": PROTOCOL, "python": sys.version.split()[0]}) + "\n")
    for line in stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except ValueError:
            out.write(json.dumps({"id": None, "ok": False, "error": "Not JSON."}) + "\n")
            continue
        answer = handle(request)
        try:
            text = json.dumps(answer, default=_label)
        except (TypeError, ValueError) as ex:
            text = json.dumps({"id": answer.get("id"), "ok": False, "error": "Answer not JSON: " + _one_line(ex)})
        out.write(text + "\n")


if __name__ == "__main__":
    main()
