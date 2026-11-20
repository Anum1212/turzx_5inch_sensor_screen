# turzx_5inch_sensor_screen

Live PC sensor dashboard (CPU, GPU, memory, network, clock, weather) for the **TURZX 5" USB screen** (800x480, USB serial 1D6B:0106).

Based on [hurricane_beast916_sensor_display](https://github.com/Anum1212/hurricane_beast916_sensor_display): the sensors, theme renderer, editor and streaming loop are the same; only the screen driver (`TurzxPanel/Screens/Turzx`) and the built-in 800x480 themes are new.

## Screen protocol (found by reading the vendor app and testing on the screen)

- USB CDC serial, 115200 (ignored), DTR and RTS on.
- Every command is one 250-byte packet: `cmd, EF, 69, length (4 bytes big endian), flag, 0, 0, payload...`
- `1` with payload `C5 D3`: info, answers e.g. `chs_5inch.dev1_rom1.88`.
- `123` payload `[0-100]`: brightness. `132`: reboot.
- `200` with length N: a picture follows as N raw bytes cut into 249-byte pieces, each padded to 250 bytes.
  Pixels are **BGRA, exactly 4 bytes each**, 800x480, row by row (1,536,000 bytes, about 1 s per frame).
  The screen answers `full_png_sucess`; the driver waits for that before sending the next frame.
- Write one packet per `Write` call (1 ms pause every 100 packets); bigger blocks lose data.
- PNG data is not accepted. The vendor app also has a diff mode (command 204) that is not implemented yet.

Close the vendor TURZX app first: only one program can hold the COM port.

## Build

```powershell
dotnet build TurzxPanel\TurzxPanel.csproj -c Release
TurzxPanel\bin\Release\net10.0-windows\TurzxPanel.exe
```