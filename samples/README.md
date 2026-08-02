# Legal sample ROMs

`axetos-cpu-smoke.nes` is an original minimal NROM-128 image created for this repository. It performs a few implemented CPU instructions and loops forever. No Nintendo or third-party game code or assets are included.

Run it with:

```powershell
dotnet run --project .\src\Products\NES\AxetosOS.Products.NES.HeadlessHost -- .\samples\axetos-cpu-smoke.nes --cycles 1000
```


## axetos-ppu-background.nes

Original AXETOS NROM test cartridge that initializes a checkerboard CHR tile, nametable and palette, enables background rendering and loops. Use the headless host `--frame` option to export the framebuffer as PPM.
