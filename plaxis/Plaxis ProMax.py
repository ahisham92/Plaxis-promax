"""
Opens Plaxis ProMax beside Plaxis.

Run it from Plaxis Input: Expert > Python > Run script..., then pick this file (the installer puts a copy in
Documents\Plaxis ProMax). When Plaxis runs it, Plaxis hands it the remote scripting server's port and password, so the
chat connects without asking. It also passes on this Python (Plaxis's own), which runs the chat's Plaxis worker.

The password goes to Plaxis ProMax in its environment only, never on the command line.
"""
import os
import subprocess
import sys

APP = os.path.join(os.environ.get("LOCALAPPDATA", ""), "PlaxisProMax", "app", "PlaxisProMax.exe")


def main(namespace):
    if not os.path.exists(APP):
        print("Plaxis ProMax isn't installed for this user ({} is missing). Run its Install.cmd first.".format(APP))
        return
    port = namespace.get("AppServerPort")
    password = namespace.get("AppServerPassword")
    args = [APP]
    python = sys.executable or ""
    if os.path.basename(python).lower().startswith("python"):
        args += ["--python", python]
    if port:
        args += ["--port", str(port)]
    env = dict(os.environ)
    if password:
        env["PLAXISPROMAX_PASSWORD"] = str(password)
    flags = 0
    if os.name == "nt":
        flags = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP: it stays open when this script ends
    subprocess.Popen(args, env=env, close_fds=True, creationflags=flags)
    print("Plaxis ProMax is opening beside Plaxis.")


main(globals())
