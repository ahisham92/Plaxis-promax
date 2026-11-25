"""
The worker's ops against the stand-in Plaxis (tests/fake_plaxis). Run from the repository root:

    python -m unittest discover -s tests/worker -v
"""
import importlib.util
import json
import os
import subprocess
import sys
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
WORKER = os.path.join(ROOT, "src", "PlaxisProMax.Core", "Plaxis", "plaxis_worker.py")
FAKE = os.path.join(ROOT, "tests", "fake_plaxis")
sys.path.insert(0, FAKE)

spec = importlib.util.spec_from_file_location("plaxis_worker", WORKER)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
from plxscripting import easy  # noqa: E402  (the stand-in)


def call(op, **args):
    return worker.handle({"id": 1, "op": op, "args": args})


class WorkerTests(unittest.TestCase):
    def setUp(self):
        easy.MODEL.reset()
        worker._state.update({"s_i": None, "g_i": None, "s_o": None, "g_o": None, "output_port": None, "input_port": None})
        answer = call("connect", input_port=10000, password="secret")
        self.assertTrue(answer["ok"], answer)
        self.status = answer["result"]

    def test_connect_reports_product_version_and_project(self):
        self.assertEqual(self.status["product"], "PLAXIS 2D")
        self.assertEqual(self.status["version"], "2024.2")
        self.assertEqual(self.status["title"], "Test project")
        self.assertEqual(self.status["unitLength"], "m")
        self.assertEqual(self.status["phases"], ["Initial phase"])

    def test_wrong_password_says_where_to_look(self):
        worker._state.update({"s_i": None, "g_i": None})
        answer = call("connect", input_port=10000, password="nope")
        self.assertFalse(answer["ok"])
        self.assertIn("Configure remote scripting server", answer["error"])

    def test_calls_before_connecting_say_so(self):
        worker._state.update({"s_i": None, "g_i": None, "input_port": None})
        self.assertEqual(call("status")["error"], "Not connected to Plaxis yet.")

    def test_unknown_op(self):
        self.assertFalse(call("explode")["ok"])

    def test_material_named_and_refused_properties_listed(self):
        r = call("create_soil_material", name="Sand", model=3, properties={"gammaUnsat": 17, "Eref": 30000, "phiBogus": 30})["result"]
        self.assertEqual(r["material"], "Sand")
        self.assertTrue(r["named"])
        self.assertEqual(sorted(r["set"]), ["Eref", "SoilModel", "gammaUnsat"])
        self.assertIn("phiBogus", r["refused"])
        again = call("create_soil_material", name="Sand")
        self.assertFalse(again["ok"])
        self.assertIn("already exists", again["error"])

    def test_boreholes_make_layers_levels_and_materials(self):
        call("create_soil_material", name="Clay")
        call("create_soil_material", name="Sand")
        r = call("create_boreholes", boreholes=[
            {"x": 0, "top": 0, "head": -2, "layers": [{"bottom": -4, "material": "Clay"}, {"bottom": -15, "material": "Sand"}]},
            {"x": 30, "top": 0.5, "layers": [{"bottom": -5}, {"bottom": -14}]},
        ])["result"]
        self.assertEqual(r["soil_layers"], 2)
        self.assertEqual(len(easy.MODEL.SoilLayers), 2)
        self.assertEqual(easy.MODEL.SoilLayers[0].Thickness.value, 4)
        self.assertEqual(easy.MODEL.SoilLayers[1].Thickness.value, 11)
        self.assertEqual(easy.MODEL.Boreholes[0].levels.value, {0: 0.0, 1: -4.0, 2: -15.0})
        self.assertEqual(easy.MODEL.Boreholes[1].levels.value, {0: 0.5, 1: -5.0, 2: -14.0})
        self.assertEqual(easy.MODEL.Boreholes[0].Head.value, -2.0)
        self.assertEqual(easy.MODEL.SoilLayers[0].Soil.Material.value.MaterialName.value, "Clay")
        self.assertEqual(r["boreholes"][0]["notes"], [])

    def test_borehole_with_a_missing_material_says_which(self):
        r = call("create_boreholes", boreholes=[{"x": 0, "layers": [{"bottom": -3, "material": "Peat"}]}])["result"]
        self.assertIn("no material named 'Peat'", r["boreholes"][0]["notes"][0])

    def test_list_model_and_inspect_read_real_names(self):
        call("create_soil_material", name="Clay")
        r = call("list_model")["result"]["collections"]
        self.assertEqual(r["Materials"], {"count": 1, "names": ["Clay"]})
        self.assertEqual(r["Phases"]["names"], ["Initial phase"])
        i = call("inspect", path="Materials[0]")["result"]
        self.assertEqual(i["label"], "Clay")
        self.assertEqual(i["properties"]["MaterialName"], "Clay")
        self.assertIn("setproperties", i["commands"])
        bad = call("inspect", path="Phases[5]")
        self.assertIn("has 1 items", bad["error"])
        missing = call("inspect", path="Bogus")
        self.assertIn("has no 'Bogus'", missing["error"])
        self.assertIn("Materials", missing["error"])
        self.assertFalse(call("inspect", path="__class__.__init__")["ok"])

    def test_commands_stop_at_the_first_failure_and_say_which(self):
        r = call("run_commands", commands=["point 0 0", "fail now", "point 1 1"])["result"]
        self.assertEqual([d["command"] for d in r["done"]], ["point 0 0"])
        self.assertEqual(r["failed"], "fail now")
        self.assertIn("not known", r["error"])
        self.assertEqual(r["not_run"], ["point 1 1"])
        self.assertEqual(call("undo")["result"], {"undone": 1})
        self.assertEqual(call("undo", steps=3)["result"]["undone"], 0)

    def test_code_runs_with_inputs_and_reports_print_result_and_line(self):
        r = call("run_code", code="print(len(g_i.Phases))\nresult = inputs['x'] * 2", inputs={"x": 21})["result"]
        self.assertTrue(r["ok"])
        self.assertEqual(r["printed"].strip(), "1")
        self.assertEqual(r["result"], 42)
        bad = call("run_code", code="a = 1\ng_i.Phasez")["result"]
        self.assertFalse(bad["ok"])
        self.assertEqual(bad["line"], 2)
        self.assertIn("Phases", bad["hint"] or "")

    def test_phases_calculate_and_results(self):
        call("create_soil_material", name="Clay")
        call("create_boreholes", boreholes=[{"x": 0, "layers": [{"bottom": -10, "material": "Clay"}]}])
        self.assertTrue(call("generate_mesh", coarseness=0.06)["ok"])
        added = call("add_phase", name="Excavation", settings={"DeformCalcType": "Plastic", "Bogus": 1})["result"]
        self.assertEqual(added["phase"], "Excavation")
        self.assertIn("Bogus", added["refused"])
        early = call("get_results", phase="Excavation")
        self.assertFalse(early["ok"])
        calc = call("calculate")["result"]
        self.assertEqual([p.get("CalculationResult") for p in calc["phases"]], ["OK", "OK"])
        res = call("get_results", phase="Excavation", result_types=["Soil.Utot", "Soil.Ux"], first=2)["result"]
        self.assertEqual(res["figures"]["Soil.Utot"]["max"], 0.031)
        self.assertEqual(res["figures"]["Soil.Ux"]["min"], -0.01)
        self.assertEqual(res["first"]["Soil.Utot"], [0.0, 0.012])
        wrong = call("get_results", phase="Excavation", result_types=["Soil.Bogus"])
        self.assertIn("ResultTypes has no", wrong["error"])
        self.assertIn("No phase named 'Nope'", call("get_results", phase="Nope")["error"])

    def _three_phases(self):
        call("create_soil_material", name="Clay")
        call("create_boreholes", boreholes=[{"x": 0, "layers": [{"bottom": -10, "material": "Clay"}]}])
        for name in ("Wall", "Dredge", "Anchor"):
            call("add_phase", name=name)

    def test_big_model_one_phase_at_a_time_saves_and_keeps_few_steps(self):
        self._three_phases()
        res = call("calculate", one_by_one=True, stored_steps=1)["result"]
        self.assertEqual([d["phase"] for d in res["one_by_one"]], ["Initial phase", "Wall", "Dredge", "Anchor"])
        self.assertTrue(all(d["ended"] == "OK" for d in res["one_by_one"]))
        self.assertEqual(res["not_calculated"], [])
        self.assertEqual(easy.MODEL.saves, 4)
        self.assertEqual(easy.MODEL.calculations, 4)
        # The first phase of the stand-in has no MaxStepsStored: said, not hidden.
        self.assertEqual(res["stored_steps"]["phases"], 3)
        self.assertIn("Initial phase", res["stored_steps"]["refused"])
        self.assertEqual(easy.MODEL.Phases[2].MaxStepsStored.value, 1)

    def test_big_model_stops_at_the_first_phase_that_fails_and_keeps_the_rest_marked(self):
        self._three_phases()
        call("add_phase", name="Dredge fails")
        call("add_phase", name="Last")
        res = call("calculate", one_by_one=True)["result"]
        self.assertEqual(res["stopped_at"], "Dredge fails")
        self.assertEqual(res["one_by_one"][-1]["ended"], "Failed")
        self.assertEqual(res["not_calculated"], ["Last"])
        self.assertTrue(easy.MODEL.Phases[-1].ShouldCalculate.value)
        self.assertEqual(easy.MODEL.saves, 5)

    def test_results_to_excel_one_sheet_per_phase_and_a_summary(self):
        import tempfile
        import zipfile
        self._three_phases()
        call("calculate")
        path = os.path.join(tempfile.mkdtemp(), "out", "Quay wall results.xlsx")
        res = call("results_to_excel", path=path, phases=["Wall", "Dredge"], results=["Soil.Utot", {"type": "Plate.M2D"}, {"type": "Soil.Bogus"}])
        self.assertTrue(res["ok"], res)
        res = res["result"]
        self.assertEqual((res["phases"], res["columns"], res["values"]), (2, 4, 14))
        self.assertEqual(len(res["problems"]), 2)
        self.assertIn("Soil.Bogus", res["problems"][0])
        self.assertEqual(res["figures"][0], {"phase": "Wall", "result": "Soil.Utot", "object": "", "location": "node", "count": 4, "min": 0.0, "max": 0.031, "max_abs": 0.031})
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            book = z.read("xl/workbook.xml").decode("utf-8")
            wall = z.read("xl/worksheets/sheet2.xml").decode("utf-8")
        self.assertIn("xl/worksheets/sheet3.xml", names)
        self.assertIn('name="Summary"', book)
        self.assertIn('name="Wall"', book)
        self.assertIn("<t xml:space=\"preserve\">Soil.Utot</t>", wall)
        self.assertIn('<c r="B3"><v>-42.5</v></c>', wall)
        try:
            import openpyxl
        except ImportError:
            return
        wb = openpyxl.load_workbook(path)
        self.assertEqual(wb.sheetnames, ["Summary", "Wall", "Dredge"])
        self.assertEqual(wb["Wall"]["A3"].value, 0.012)
        self.assertEqual(wb["Summary"]["H3"].value, 42.5)

    def test_results_to_excel_says_what_it_needs(self):
        self.assertIn(".xlsx", call("results_to_excel", path="x.csv", results=["Soil.Utot"])["error"])
        self.assertIn("results", call("results_to_excel", path="x.xlsx")["error"])
        self.assertIn("No phase is calculated yet", call("results_to_excel", path="x.xlsx", results=["Soil.Utot"])["error"])

    def test_results_to_csv(self):
        import tempfile
        call("calculate")
        path = os.path.join(tempfile.mkdtemp(), "out", "u.csv")
        res = call("get_results", result_types=["Soil.Utot"], csv_path=path)["result"]
        self.assertEqual(res["csv"], path)
        with open(path) as f:
            self.assertEqual(f.read().splitlines()[:2], ["Soil.Utot", "0.0"])


class ProtocolTests(unittest.TestCase):
    def test_lines_in_lines_out_and_prints_never_break_the_protocol(self):
        env = dict(os.environ, PYTHONPATH=FAKE)
        # -I ignores PYTHONPATH, as on a user's PC; the stand-in is added with -c instead.
        bootstrap = "import sys, runpy; sys.path.insert(0, {!r}); sys.argv = [{!r}]; runpy.run_path({!r}, run_name='__main__')".format(FAKE, WORKER, WORKER)
        p = subprocess.Popen([sys.executable, "-u", "-c", bootstrap], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
        requests = [
            {"id": 1, "op": "connect", "args": {"input_port": 10000, "password": "secret"}},
            {"id": 2, "op": "run_code", "args": {"code": "print('hello')"}},
        ]
        out, _ = p.communicate(("\n".join(json.dumps(r) for r in requests) + "\nnot json\n").encode(), timeout=30)
        lines = [json.loads(l) for l in out.decode().splitlines()]
        self.assertTrue(lines[0]["ready"])
        self.assertEqual(lines[1]["id"], 1)
        self.assertTrue(lines[1]["ok"])
        self.assertEqual(lines[2]["result"]["printed"].strip(), "hello")
        self.assertEqual(lines[3]["error"], "Not JSON.")


if __name__ == "__main__":
    unittest.main()
