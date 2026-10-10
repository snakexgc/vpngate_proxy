# Third-party notices

## sing-box 1.14.2

Source: https://github.com/SagerNet/sing-box/tree/v1.14.2

Binary: https://github.com/SagerNet/sing-box/releases/download/v1.14.2/sing-box-1.14.2-windows-amd64.zip

Archive SHA256: `c2d8bfff918755808781dfdeeb8581b6c91eb3a243d9a7b55483cfc0c0684d32`

Executable SHA256: `7bbef1dea9189ee12799ae834ea4b4658355da25c47a21ad8804904c0ccd9410`

Copyright SagerNet and contributors. License: GPL-3.0-or-later, with the upstream naming notice. See `runtime/COPYING-sing-box`, `runtime/LICENSE-sing-box` and `runtime/GPL-3.0.txt`; upstream provides complete source and dependency information at the versioned source link above. The GUI invokes the unmodified core as an independent process.

## SagerNet country rule sets

Bundled initial data: `runtime/geosite-cn.srs` and `runtime/geoip-cn.srs`.

- geosite-cn source: https://github.com/SagerNet/sing-geosite/tree/df6851d87aeaeb9d6a9464288b317e7ff2288d78
  SHA256: `7e5edaa3156918ebb732629f7b4ab3f03040b9940456438f976c2046033fc15c`
- geoip-cn source: https://github.com/SagerNet/sing-geoip/tree/7fe82a879ad2666526730c195b55a6d8d9147908
  SHA256: `ebee603fdf402314b44b9f653cdcf6d9cc9c41e84e2b3515e3123c5c920a93bc`

Copyright SagerNet and data contributors. Upstream repositories use GPL-3.0-or-later; see `runtime/LICENSE-sing-geosite.txt`, `runtime/LICENSE-sing-geoip.txt` and `runtime/GPL-3.0.txt`. The application downloads subsequent country-data updates from each repository's `rule-set` branch and validates them with the unmodified sing-box core before use.

## Wintun 0.14.1

Source: https://git.zx2c4.com/wintun/

Download: https://www.wintun.net/builds/wintun-0.14.1.zip

Archive SHA256: `07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51`

AMD64 DLL SHA256: `e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce`

Copyright WireGuard LLC. The official prebuilt DLL is distributed under the license in `runtime/LICENSE-wintun.txt`, and is signed by WireGuard LLC.

## .NET 10

The portable package contains Microsoft's .NET 10 Windows Desktop runtime, distributed under the MIT license and its accompanying third-party notices: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf .
