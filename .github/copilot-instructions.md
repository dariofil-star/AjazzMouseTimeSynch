# Copilot Instructions

## Project Guidelines
- Workspace defaults: project targets .NET 10; IDE is Visual Studio Community 2026 (18.9.2); workspace root E:\source\AjazzMouseTimeSynch\ with solution E:\source\AjazzMouseTimeSynch\AjazzMouseTimeSynch.slnx; preferred shell is pwsh.exe; active repository is E:\source\AjazzMouseTimeSynch on branch master (origin https://github.com/dariofil-star/AjazzMouseTimeSynch).

## AJAZZ Protocol Instructions
- Battery/status response frame: `05 00 00 64 01 00/01 00/01 02` with battery status at byte[3] on Windows.
- Requires poll request frame: `00 F7 ...` before `GET_FEATURE 0x05`.
- Clock sync opcode: `0x28` with marker `D7` at byte[8] and big-endian year at bytes[9..10].
- Firmware query uses: `05 80 00 00` (GET_REV).
- AJAZZ HID opcode map: 
  - `0x28`: set device clock
  - `0x80`: get firmware revision
  - `0xC2`: DPI change event
  - `0xD3`: read option parameter block 0
  - `0xD4`: read option parameter block 1
  - `0xF7`: battery/status query

## USB Debug Logging
- Log all data for the same VendorId regardless of ProductId filtering.