# The Modeller and the Viewer

Plaxis ProMax has two windows that need no Plaxis and no Plaxis licence, as ETABS ProMax has its Modeler and Viewer:

- **Plaxis ProMax Modeller** builds a Plaxis model: soil (boreholes, layers, soil polygons), structures (plates, anchors,
  geogrids, embedded beams, interfaces, loads), the mesh, flow conditions (water levels) and staged construction, in the
  same five modes as Plaxis Input, for PLAXIS 2D and 3D. The chat window opens with it and can read and change the same
  model.
- **Plaxis ProMax Viewer** shows the results: deformed mesh, total and component displacements, effective and principal
  stresses, principal directions, relative shear stress, plastic points, pore pressures, groundwater head, plate
  moments, shear and axial forces, anchor forces, cross sections, curves at a point across phases and tables, in 2D and
  3D (outer surface and sections). Pictures (PNG), tables (CSV) and a single HTML file to send to a reviewer.

Both open from their own shortcuts, on the desktop and in the Start menu's Plaxis ProMax folder. The program makes
them at its first start and points them at the new version after an update (`install.ps1` makes them as well).

## When the licence is used

Only **Calculate in Plaxis** uses Plaxis, and so a licence, and only while it runs:

1. Start PLAXIS Input with its remote scripting server on a free port and a new password (the licence is taken here).
2. Connect to it with Plaxis's own Python.
3. Build the model in a new project and mesh it (`ppm_build.py`). The project is saved in a folder beside the model:
   `<model> (Plaxis)\<model>.p2dx` or `.p3d`.
4. Calculate the phases (one by one with a save after each by default, so a failed phase keeps the ones before it).
5. Copy the results from Output into the results file beside the model (`<model>.ppr`).
6. Close Plaxis (the licence is given back). "Keep Plaxis open" leaves it open, and the licence in use, until you close it.

Stop closes Plaxis at once. If anything fails, Plaxis is closed too, so the licence is never left taken. From the chat,
`calculate_in_plaxis` always asks before it starts, because it takes a licence.

The Modeller can also export the build as one Python script to run in Plaxis Input yourself (Expert > Python > Run
script). That is the same builder.

## Files

**Model (.ppm)**: JSON, saved to `Documents\Plaxis ProMax\Models` unless you pick another folder.

| Key | What it holds |
|---|---|
| `format`, `version` | `"ppm"`, `1` |
| `title`, `product` | the project title; `"2D"` or `"3D"` |
| `model`, `elements` | plane strain or axisymmetry; 6 or 15 noded (2D) |
| `units` | `length`, `force`, `time` |
| `contour` | `xmin`, `xmax`, `ymin`, `ymax` (2D) or `xmin`..`ymax`, `zmin` (3D) |
| `materials` | `id`, `kind` (soil, plate, anchor, geogrid, embedded), `name`, `model` and its parameters by Plaxis's names |
| `boreholes` | `id`, `name`, `x` (and `y` in 3D), `top`, `head`, `layers` top-down with `bottom` and `material` |
| `structures` | `id`, `type`, `name`, `points`, `material`, `interfaces`, and for loads their values (`qx`, `qy`...); in 3D walls and piles have a `depth` |
| `waterlevels` | `id`, `points` |
| `mesh` | `coarseness`, `refine` (`object` and `factor`) |
| `phases` | `id`, `name`, `parent`, `calc` (K0 procedure, Gravity loading, Plastic, Consolidation, Safety...), `active` (id to true or false), `loads` (values per load in this phase), `water` (the water level used), and when needed `time` (consolidation) and `reset_displacements` |

**Results (.ppr)**: written by Calculate in Plaxis beside the model. For each phase: its name, calculation type, how it
ended, ΣMstage and ΣMsf, node coordinates and displacements, stress point coordinates and stresses, pore pressures,
plastic points, and plate, embedded beam and anchor results. Anything Plaxis would not give is listed under `missing`,
so a gap is never silent. Very large models keep the first 200,000 nodes and 600,000 stress points, and the file says
so when it cut any.

This is the file to send to another PC: the Viewer there needs nothing else (or use the Viewer's Share to make one
HTML file). The Plaxis project folder, which holds Plaxis's own results and can run to gigabytes, stays behind.

It is made small for big models (format version 2):

- Lists that are the same in several phases (node and stress point coordinates) are written once.
- Soil results are kept as 2-byte steps over each list's own range: a value is within 1/131,068 of that range of
  Plaxis's (0.0002 mm on a 30 mm range, 0.004 kPa on a 500 kPa range). Coordinates and structure forces keep 7
  digits.
- The numbers are stored byte plane by byte plane and the whole file is gzip-compressed.
- Before a run you can choose what to keep: displacements, stresses, pore pressures, plastic points, structure
  forces, and which phases (the Calculate dialog, or `keep_results` and `keep_phases` from the chat). The Plaxis
  project still has everything.

Sizes measured on made-up data (smooth fields with noise, so real results should pack at least as well): a 3D model
with 50,000 nodes, 150,000 stress points and 10 phases, every result kept, is 28 MB, against 63 MB in the first format;
keeping only displacements for the last 3 phases, 1.1 MB. A model four times that size scales about in proportion.
The format is in `ppm_build.py` (`write_pack`, `read_pack`); files in the first format still open.

## What to know

- The Modeller's mesh view is a **preview** drawn to show where the mesh is finer. Plaxis makes the real mesh when it
  calculates, and the Viewer shows Plaxis's own nodes and stress points.
- The Viewer draws its colours over a triangulation of Plaxis's nodes (and stress points for stresses), cut to the soil
  of that phase. Values are Plaxis's; the shading between them is the Viewer's.
- The builder tries more than one property name where Plaxis versions differ (2023, 2024, CONNECT Edition V22), and
  lists every value Plaxis did not take. It has been run against a stand-in Plaxis only: the field test on real PLAXIS 2D
  and 3D is still to come.
- The two samples (File > Open sample) come with made-up results for the screenshots, marked as samples in the file and
  in the Viewer. They are not from Plaxis.
