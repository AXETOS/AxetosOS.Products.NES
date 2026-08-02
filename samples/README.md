# Legal sample ROMs

`axetos-cpu-smoke.nes` is an original minimal NROM-128 image created for this repository. It performs a few implemented CPU instructions and loops forever. No Nintendo or third-party game code or assets are included.

Run it with:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-cpu-smoke.nes --cycles 1000
```


## axetos-ppu-background.nes

Original AXETOS NROM test cartridge that initializes a checkerboard CHR tile, nametable and palette, enables background rendering and loops. Use the headless host `--frame` option to export the framebuffer as PPM.

## `axetos-ppu-sprites.nes`

Original NROM visual test cartridge for background rendering, primary OAM, sprite priority/flipping and `$4014` OAM DMA. It contains no commercial code or assets.


## `axetos-controller-motion.nes`

Original NROM test cartridge that polls controller port 1 once per VBlank and moves sprite 0 left or right through OAM DMA. `axetos-controller-motion.input.json` supplies a deterministic cycle-based input timeline for the headless host.

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-controller-motion.nes --cycles 420000 --input-script .\samples\axetos-controller-motion.input.json --frame .\output\controller-motion.ppm
```
