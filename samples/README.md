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
```
