// Screenshots of the Modeller and the Viewer on the sample models, with no Plaxis and no server.
//   node tools/screens/shoot.js <samples folder> <out folder> [only]
const { chromium } = require("playwright");
const fs = require("fs"), path = require("path"), zlib = require("zlib");
const [samplesDir, outDir, only] = process.argv.slice(2);
const web = path.join(__dirname, "../../src/PlaxisProMax.Core/Modeller/web");
const m2 = JSON.parse(fs.readFileSync(path.join(samplesDir, "excavation-2d.ppm"), "utf8"));
const m3 = JSON.parse(fs.readFileSync(path.join(samplesDir, "pit-3d.ppm"), "utf8"));
const shots = [
  ["m-soil", "modeller", { start: m2, mode: { mode: "soil" } }],
  ["m-structures", "modeller", { start: m2, mode: { mode: "structures", sel: "wall" } }],
  ["m-mesh", "modeller", { start: m2, mode: { mode: "mesh" } }],
  ["m-flow", "modeller", { start: m2, mode: { mode: "flow", sel: "wl1" } }],
  ["m-stages", "modeller", { start: m2, mode: { mode: "stages", phase: "p4" } }],
  ["m-3d", "modeller", { start: m3, mode: { mode: "structures" } }],
  ["m-3d-stages", "modeller", { start: m3, mode: { mode: "stages", phase: "p4" } }],
  ["m-layers", "modeller", { start: m2, mode: { mode: "soil" }, act: "layers" }],
  ["m-materials", "modeller", { start: m2, mode: { mode: "soil" }, act: "materials" }],
  ["m-welcome", "modeller", {}],
  ["v-utot", "viewer", { pack: "excavation-2d", ui: { phase: 5, type: "Utot", arrows: false } }],
  ["v-deformed", "viewer", { pack: "excavation-2d", ui: { phase: 5, type: "deformed" } }],
  ["v-sigyy", "viewer", { pack: "excavation-2d", ui: { phase: 5, type: "SigyyE", contours: true } }],
  ["v-plastic", "viewer", { pack: "excavation-2d", ui: { phase: 6, type: "Plastic" } }],
  ["v-plate", "viewer", { pack: "excavation-2d", ui: { phase: 5, type: "plate:M" } }],
  ["v-tools", "viewer", { pack: "excavation-2d", ui: { phase: 5, type: "Ux", arrows: true, section: [[0, -14], [45, -14]], curve: { at: [21, -1] } } }],
  ["v-3d", "viewer", { pack: "pit-3d", ui: { phase: 4, type: "Utot" } }],
  ["v-3d-section", "viewer", { pack: "pit-3d", ui: { phase: 4, type: "Utot", view3d: "section" } }],
  ["v-tables", "viewer", { pack: "excavation-2d", ui: { phase: 5, type: "Utot" }, act: "tables" }],
  ["v-open", "viewer", {}],
];
(async () => {
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await chromium.launch();
  for (const [name, page, o] of shots) {
    if (only && !name.startsWith(only)) continue;
    const p = await browser.newPage({ viewport: { width: 1600, height: 960 }, deviceScaleFactor: 1 });
    const errors = [];
    p.on("pageerror", (e) => errors.push(e.message));
    p.on("console", (m) => { if (m.type() === "error") errors.push(m.text()); });
    const packB64 = o.pack ? fs.readFileSync(path.join(samplesDir, o.pack + ".ppr")).toString("base64") : null;
    await p.addInitScript(({ start, mode, s2, s3, packB64, packName, vui }) => {
      window.PPM_SAMPLES = { excavation: s2, pit: s3 }; if (start) window.PPM_START = start; if (mode) window.PPM_START_MODE = mode;
      if (packB64) { window.PPR_START = Uint8Array.from(atob(packB64), (c) => c.charCodeAt(0)).buffer; window.PPR_START_NAME = packName; window.PPR_START_UI = vui; }
    }, { start: o.start, mode: o.mode, s2: m2, s3: m3, packB64, packName: o.pack ? o.pack + ".ppr" : "", vui: o.ui });
    await p.goto("file://" + path.join(web, page + ".html") + "?offline");
    await p.waitForTimeout(400);
    if (o.act === "layers") await p.evaluate(() => layersDialog());
    if (o.act === "tables") await p.evaluate(() => tables());
    if (o.act === "materials") await p.evaluate(() => materialsDialog("m_sand"));
    await p.waitForTimeout(o.pack ? 900 : 250);
    await p.screenshot({ path: path.join(outDir, name + ".png") });
    if (errors.length) console.log(name, "ERRORS:", errors.join(" | "));
    else console.log(name, "ok");
    await p.close();
  }
  await browser.close();
})();
