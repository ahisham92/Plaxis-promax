"""
A stand-in for Bentley's plxscripting, for the tests: enough of PLAXIS 2D Input and Output to run every worker op
without Plaxis. Objects behave like plxscripting's proxies: properties have .value and .set(), collections iterate,
commands are methods, and a wrong name raises AttributeError.
"""
import os

PASSWORD = os.environ.get("FAKE_PLAXIS_PASSWORD", "secret")
PRODUCT = os.environ.get("FAKE_PLAXIS_PRODUCT", "PLAXIS 2D")


class Prop:
    def __init__(self, value):
        self.value = value

    def set(self, value):
        self.value = value

    def __repr__(self):
        return repr(self.value)


class Obj:
    def __init__(self, kind, name, **props):
        object.__setattr__(self, "_kind", kind)
        object.__setattr__(self, "_props", {"Name": Prop(name)})
        for k, v in props.items():
            self._props[k] = Prop(v)

    def __getattr__(self, name):
        props = object.__getattribute__(self, "_props")
        if name in props:
            return props[name]
        raise AttributeError("Requested attribute '{}' is not present".format(name))

    def __setattr__(self, name, value):
        props = object.__getattribute__(self, "_props")
        if name in props:
            props[name].value = value
        else:
            props[name] = Prop(value)

    def __dir__(self):
        return list(object.__getattribute__(self, "_props"))

    def __repr__(self):
        return "<{} {}>".format(self._kind, self._props["Name"].value)


class Material(Obj):
    def __dir__(self):
        return list(object.__getattribute__(self, "_props")) + ["setproperties"]

    ALLOWED = {"MaterialName", "SoilModel", "gammaUnsat", "gammaSat", "Eref", "nu", "cref", "phi"}

    def setproperties(self, *pairs):
        for i in range(0, len(pairs), 2):
            key, value = pairs[i], pairs[i + 1]
            if key not in self.ALLOWED:
                raise Exception("Unsuccessful command:\nProperty {} is not present".format(key))
            self._props[key] = Prop(value)


class Model:
    def __init__(self):
        self.reset()

    def reset(self):
        self.Phases = [Obj("Phase", "Initial phase", Identification="Initial phase", ShouldCalculate=True, DeformCalcType="K0 procedure", PreviousPhase=None)]
        self.Materials = []
        self.Boreholes = []
        self.SoilLayers = []
        self.Plates = []
        self.log = []
        self.mode = "soil"
        self.calculated = False


MODEL = Model()


class Commands:
    def __init__(self, model):
        object.__setattr__(self, "m", model)

    def __dir__(self):
        return ["Phases", "Materials", "Boreholes", "SoilLayers", "Plates", "Project", "SoilContour", "soilmat", "borehole",
                "soillayer", "setsoillayerlevel", "mesh", "phase", "calculate", "view", "save", "gotosoil", "gotostructures",
                "gotomesh", "gotoflow", "gotostages"]

    def __getattr__(self, name):
        m = object.__getattribute__(self, "m")
        if name in ("Phases", "Materials", "Boreholes", "SoilLayers", "Plates"):
            return getattr(m, name)
        if name == "Project":
            return Obj("Project", "Project", Title="Test project", Filename="C:\\Work\\test.p2dx", UnitLength="m", UnitForce="kN", UnitTime="day")
        if name == "SoilContour":
            return Contour(m)
        if name.startswith("goto"):
            def go():
                m.mode = name[4:]
                return "OK"
            return go
        raise AttributeError("Requested attribute '{}' is not present".format(name))

    def soilmat(self, *pairs):
        mat = Material("SoilMat", "Material_{}".format(len(self.m.Materials) + 1))
        self.m.Materials.append(mat)
        if pairs:
            mat.setproperties(*pairs)
        return mat

    def borehole(self, x, *yz):
        bh = Obj("Borehole", "Borehole_{}".format(len(self.m.Boreholes) + 1), x=x, Head=0.0)
        bh.levels = {}
        self.m.Boreholes.append(bh)
        return bh

    def soillayer(self, thickness):
        layer = Obj("SoilLayer", "Soillayer_{}".format(len(self.m.SoilLayers) + 1), Thickness=thickness)
        layer._props["Soil"] = Obj("Soil", "Soil_{}".format(len(self.m.SoilLayers) + 1), Material=None)
        self.m.SoilLayers.append(layer)
        return layer

    def setsoillayerlevel(self, bh, index, level):
        if index > len(self.m.SoilLayers):
            raise Exception("Unsuccessful command:\nLevel index out of range")
        bh.levels.value[index] = level

    def mesh(self, *coarseness):
        if not self.m.SoilLayers:
            raise Exception("Unsuccessful command:\nNo geometry to mesh")
        self.m.meshed = True
        return "Generated 812 elements, 6851 nodes"

    def phase(self, previous):
        p = Obj("Phase", "Phase_{}".format(len(self.m.Phases)), Identification="Phase_{}".format(len(self.m.Phases)), ShouldCalculate=True, DeformCalcType="Plastic", PreviousPhase=previous)
        self.m.Phases.append(p)
        return p

    def calculate(self):
        for p in self.m.Phases:
            p.CalculationResult = "OK"
        self.m.calculated = True
        return "OK"

    def view(self, phase):
        if not self.m.calculated:
            raise Exception("Unsuccessful command:\nPhase not calculated")
        return 10001

    def save(self, *path):
        return "Project saved"


class Contour:
    def __init__(self, m):
        self.m = m

    def initializerectangular(self, xmin, ymin, xmax, ymax):
        self.m.contour = (xmin, ymin, xmax, ymax)
        return "OK"


class Server:
    def __init__(self, model):
        self.m = model
        self.last_response = ""

    server_full_name = "{} Input 2024.2.0.1144".format(PRODUCT)
    major_version = 2024
    minor_version = 2
    is_2d = PRODUCT == "PLAXIS 2D"
    is_3d = PRODUCT == "PLAXIS 3D"

    def call_and_handle_command(self, command):
        word = command.split()[0] if command.split() else ""
        if word == "fail":
            raise Exception("Unsuccessful command:\nThe command 'fail' is not known")
        if word == "undo":
            if not self.m.log:
                raise Exception("Unsuccessful command:\nNothing to undo")
            self.m.log.pop()
            return "OK"
        if word == "slow":
            import time
            time.sleep(float(command.split()[1]))
        self.m.log.append(command)
        self.last_response = "OK: " + command
        return "Added " + command

    def new(self):
        self.m.reset()
        return "OK"

    def open(self, path):
        self.m.reset()
        return "OK"


class ResultTypes:
    class Soil:
        Utot = "Soil.Utot"
        Ux = "Soil.Ux"

    class Plate:
        M2D = "Plate.M2D"


class OutputCommands:
    def __init__(self, model):
        self.m = model
        self.ResultTypes = ResultTypes
        self.Plates = []

    @property
    def Phases(self):
        return self.m.Phases

    def getresults(self, *args):
        rt = args[-2]
        if rt == "Soil.Utot":
            return [0.0, 0.012, 0.031, 0.025]
        if rt == "Soil.Ux":
            return [-0.01, 0.0, 0.02, 0.005]
        return [10.0, -42.5, 7.0]


def new_server(address=None, port=None, timeout=5.0, request_timeout=None, password=None, error_mode=()):
    if address not in (None, "localhost", "127.0.0.1", "plaxis-pc"):
        raise Exception("Connection refused")
    if password != PASSWORD:
        raise Exception("Invalid password")
    if port == 10000:
        return Server(MODEL), Commands(MODEL)
    if port == 10001:
        return Server(MODEL), OutputCommands(MODEL)
    raise Exception("ConnectionError: Connection refused on port {}".format(port))
