"""
The Modeller's builder (ppm_build.py) on the two sample models, against a stand-in Plaxis that takes every command
(unittest.mock). It shows the builder walks a whole model, 2D and 3D, without a slip of its own; whether real Plaxis
takes each property name is for the field test. Run from the repository root:

    python -m unittest discover -s tests/worker -v
"""
import gzip
import importlib.util
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BUILD = os.path.join(ROOT, "src", "PlaxisProMax.Core", "Plaxis", "ppm_build.py")
SAMPLES = os.path.join(ROOT, "src", "PlaxisProMax.Core", "Modeller", "samples")

spec = importlib.util.spec_from_file_location("ppm_build", BUILD)
ppm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ppm)


def sample(name):
    with open(os.path.join(SAMPLES, name + ".ppm"), encoding="utf-8") as f:
        return json.load(f)


class BuilderTests(unittest.TestCase):
    def run_build(self, name, is_3d):
        s_i, g_i = mock.MagicMock(), mock.MagicMock()
        s_i.is_3d = is_3d
        answer, objects = ppm.build(s_i, g_i, sample(name))
        return answer, objects, g_i

    def check(self, name, is_3d):
        answer, objects, g_i = self.run_build(name, is_3d)
        own = [f for f in answer.get("failed", []) if "Mock" not in str(f.get("why", ""))]
        self.assertEqual(own, [], json.dumps(answer.get("failed"), default=str)[:2000])
        model = sample(name)
        self.assertGreaterEqual(len(answer.get("made", [])), len(model["materials"]) + len(model["structures"]))
        self.assertTrue(g_i.mesh.called or any("mesh" in str(c) for c in g_i.method_calls), "the model was not meshed")
        self.assertTrue(g_i.phase.called, "no phases were made")
        for s in model["structures"]:
            self.assertIn(s["id"], objects, "structure not made: " + s["id"])

    def test_2d_sample_builds(self):
        self.check("excavation-2d", False)

    def test_3d_sample_builds(self):
        self.check("pit-3d", True)

    def test_set_first_tries_the_next_name(self):
        class Obj:
            taken = {}

            def setproperties(self, name, value):
                if name != "E":
                    raise Exception("no property " + name)
                self.taken[name] = value

            def __setattr__(self, name, value):
                raise Exception("read only")
        o, log = Obj(), ppm.Log()
        self.assertEqual(ppm.set_first(o, ["Eref", "E"], 1000, log, "Clay", "E"), "E")
        self.assertEqual(o.taken, {"E": 1000})
        self.assertEqual(log.refused, [])
        self.assertIsNone(ppm.set_first(o, ["Nope"], 1, log, "Clay", "x"))
        self.assertEqual(len(log.refused), 1)

    def test_export_writes_a_results_file_even_when_results_are_missing(self):
        g_i, g_o = mock.MagicMock(), mock.MagicMock()
        g_o.getresults.side_effect = Exception("not calculated")
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "r.ppr")
            ppm.export(g_i, g_o, sample("excavation-2d"), path, [], "2024.2")
            r = ppm.read_pack(path)
        self.assertEqual(r["format"], "ppr")
        self.assertIn("missing", r)


class ResultsFileTests(unittest.TestCase):
    def pack(self, phases):
        return {"format": "ppr", "version": 2, "title": "t", "product": "2D", "model": {"phases": []}, "phases": phases}

    def test_numbers_come_back_with_missing_values(self):
        values = [0.0, -0.0123456, 1e-7, None, 123456.7, 3.0, -2.5, 0.001, 42.0]
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "r.ppr")
            ppm.write_pack(self.pack([{"name": "A", "node": {"Utot": values}, "plates": [{"M": values}], "short": [1, 2]}]), path)
            r = ppm.read_pack(path)
        exact = r["phases"][0]["plates"][0]["M"]
        stepped = r["phases"][0]["node"]["Utot"]
        self.assertIsNone(exact[3])
        self.assertIsNone(stepped[3])
        span = 123456.7 + 2.5
        for a, b, c in zip(values, exact, stepped):
            if a is not None:
                self.assertAlmostEqual(a, b, delta=abs(a) * 1e-6 + 1e-12)       # structure forces: 7 digits
                self.assertAlmostEqual(a, c, delta=span / 65534 / 2 * 1.0001)   # soil results: half a step of the range
        self.assertEqual(stepped[4], 123456.7)
        self.assertEqual(stepped[6], -2.5)
        self.assertEqual(r["phases"][0]["short"], [1, 2])

    def test_coordinates_are_written_once_for_all_phases(self):
        n = 20000
        xs = [i * 0.37 % 60 for i in range(n)]
        ys = [-(i * 0.11 % 30) for i in range(n)]
        phases = [{"name": "P%d" % k, "nodes": {"x": xs, "y": ys}, "node": {"Utot": [((i * 7919 + k) % 1000) / 1e5 for i in range(n)]}} for k in range(10)]
        with tempfile.TemporaryDirectory() as d:
            once, plain = os.path.join(d, "a.ppr"), os.path.join(d, "b.ppr")
            size = ppm.write_pack(self.pack(phases), once)
            with gzip.open(plain, "wb", compresslevel=6) as f:
                f.write(json.dumps(self.pack(phases), separators=(",", ":")).encode("utf-8"))
            r = ppm.read_pack(once)
            self.assertLess(size, os.path.getsize(plain) / 2)
        self.assertEqual(len(r["phases"]), 10)
        self.assertAlmostEqual(r["phases"][9]["nodes"]["x"][123], xs[123], places=4)

    def test_old_files_still_open(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "old.ppr")
            with gzip.open(path, "wb") as f:
                f.write(json.dumps({"format": "ppr", "version": 1, "phases": [{"name": "A"}]}).encode("utf-8"))
            self.assertEqual(ppm.read_pack(path)["phases"][0]["name"], "A")

    def test_keep_leaves_out_phases_and_results(self):
        keys, phases, structures = ppm._kept({"phases": ["p2"], "results": ["displacements"]})
        self.assertEqual(keys, {"Ux", "Uy", "Uz", "Utot"})
        self.assertEqual(phases, {"p2"})
        self.assertFalse(structures)
        self.assertEqual(ppm._kept(None), (None, None, True))


if __name__ == "__main__":
    unittest.main()
