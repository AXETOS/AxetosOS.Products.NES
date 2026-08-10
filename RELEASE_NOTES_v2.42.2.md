# AxetosOS Products / NES v2.42.2

## MMC2 physical PPU-bus fixture hotfix

- Corrects `Raw_physical_ppu_trigger_holds_old_chr_data_for_the_triggering_bus_read` by attaching passive high-impedance physical traces to cartridge PPU data pins D0-D7 before sampling them.
- The previous fixture attached VCC, GND, `/RD`, `/WR`, and PPU address pins but left PPU D0-D7 electrically isolated. `DigitalPin.Drive` correctly updated the package drive state, but an unconnected pin has no `DigitalNet` to resolve and publish a sampled bus level, so `PpuData.TrySample` correctly returned false.
- The fixture now mirrors the established raw MMC1 cartridge tests: each PPU data pin is connected to a high-impedance external source, creating a real trace without adding any active driver.
- MMC2 timing semantics are unchanged: the triggering `$0FD8/$0FE8/$1FD8-$1FDF/$1FE8-$1FEF` CHR read is driven from the previously selected bank, and the FD/FE latch affects subsequent accesses.
- No Mapper 9 cartridge implementation, compiler, motherboard, CPU, PPU, host, or sample-ROM behavior changes.
- Expected Release suite remains **494 tests**, pending local validation.
