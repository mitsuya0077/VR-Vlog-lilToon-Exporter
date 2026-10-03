# LAN transfer dependencies

Only the .NET Standard 2.0 core assemblies below are redistributed. Both use
Editor-only plugin import settings and are referenced only by the LAN transfer
Editor and test assemblies. The package builder accepts these exact DLL paths and verifies their
SHA-256 before producing a package; arbitrary DLL files remain excluded.

| Library | Fixed version | DLL SHA-256 |
| --- | --- | --- |
| BouncyCastle.Cryptography | 2.7.0 | `d61c1f2ba929a230a58e101ccd850e21f2675fa6b9814ec279633e8a089c3495` |
| ZXing.Net | 0.16.11 | `f3b823b6fd6492525a7547989056883def5d43be1e12c4f63fa54df73e3c5cfc` |

Bouncy Castle is distributed under MIT: see `BouncyCastle-LICENSE.txt`.
Official source: https://github.com/bcgit/bc-csharp/tree/release-2.7.0
Official package: https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0
Original NuGet package SHA-256:
`f091ffccab4d03993e660bace277659a79dee0972f54d7f1f4bd46d680966241`.

ZXing.Net is distributed under Apache-2.0: see `ZXing-LICENSE.txt`.
Copyright 2008 ZXing authors; Copyright 2012 Michael Jahn.
Official source: https://github.com/micjahn/ZXing.Net/tree/v0.16.11.0
Official package: https://www.nuget.org/packages/ZXing.Net/0.16.11
Original NuGet package SHA-256:
`7d39234d668e558b3d374d18116ed57021d7c0df482801f1e1db4f1db9314ec3`.

The redistributed DLLs are unmodified. No online QR generation service,
NuGet-for-Unity runtime dependency, certificate store installation, or native
cryptographic binary is required.
