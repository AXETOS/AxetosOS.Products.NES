# NES samples

The retired high-level `AxetosOS.Products.NES.HeadlessHost` execution path was removed in v1.3.7.
NES execution now uses the physical `VirtualHardware` machine only.

Run an NROM sample/ROM through the same physical IC-boundary host used for games:

```powershell
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-cpu-smoke.nes --board famicom
```

Controller samples now enter through external physical button-contact sources and the standard controller package; they do not bypass the console's `$4016/$4017` circuitry.

Mapper smoke ROMs:

```powershell
# Mapper 2 / UxROM
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-uxrom-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 3 / CNROM
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-cnrom-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 4 / MMC3
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-mmc3-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 7 / AxROM
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-axrom-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 9 / MMC2 / PxROM tile-trigger latch
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-mmc2-tile-latch.nes --board famicom --uncapped --stop-frame 120

# Mapper 10 / MMC4 / FxROM tile-trigger latch + PRG RAM
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-mmc4-tile-latch.nes --board famicom --uncapped --stop-frame 120

# Mapper 11 / Color Dreams
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-colordreams-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 16 / Bandai LZ93D50 / FCG
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-bandai-fcg-irq.nes --board famicom --uncapped --stop-frame 120

# Mapper 18 / Jaleco SS88006
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-jaleco-ss88006-irq.nes --board famicom --uncapped --stop-frame 120


# Mapper 21 / Konami VRC4a synthetic banking + IRQ smoke
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-konami-vrc4-irq.nes --board famicom --uncapped --stop-frame 120

# Mapper 34 / BNROM
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-bnrom-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 34 / NINA-001
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-nina001-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 66 / GxROM
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-gxrom-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 69 / Sunsoft FME-7 / 5B IRQ + PSG register smoke
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-sunsoft-fme7-irq-5b.nes --board famicom --uncapped --stop-frame 120

# Mapper 71 / Camerica (BF9097 / Fire Hawk variant)
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-camerica-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 79 / NINA-03/NINA-06
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-nina-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 206 / DxROM / Namco 108 / MIMIC-1
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-dxrom-bank-switch.nes --board famicom --uncapped --stop-frame 120

# Mapper 227 / address-latch multicart (512 KiB legacy-iNES geometry)
dotnet run -c Release --project .\src\Products\NES\AxetosOS.Products.NES.DesktopHost -- .\samples\axetos-mapper227-multicart.nes --board famicom --uncapped --stop-frame 120
```
