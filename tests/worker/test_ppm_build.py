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
            with gzip.open(path, "rt", encoding="utf-8") as f:
                r = json.load(f)
        self.assertEqual(r["format"], "ppr")
        self.assertIn("missing", r)


if __name__ == "__main__":
    unittest.main()
