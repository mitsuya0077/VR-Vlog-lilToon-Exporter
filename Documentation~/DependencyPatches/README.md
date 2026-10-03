# AAO 1.9.20 vertex-buffer disposal correction

AAO 1.9.20 reads mesh vertex data in `MeshInfo2.GetVertexBuffers` without disposing the returned `GraphicsBuffer`. Native allocation tracing reproduced this in both canonical AAO/NDMF builds and exporter builds. The one-line patch changes the local declaration to `using var`, releasing the handle after readback and on exceptions. Optimize passes remain enabled.

This is an explicit local source correction for **AAO 1.9.20 + `vrvlog.aao-1.9.20.vertex-buffer-dispose.1`**. The package manifest still declares 1.9.20; it is not an upstream upgrade. Record the applied patch identity alongside that version when sharing validation results. Neither exporter runtime code nor this tool finds or patches packages automatically.

Close every Unity Editor using the target copy before applying it. Make a separately owned, writable copy of the AAO package, preserve its contents and metadata, and point the project's local package dependency at that copy. Do not patch the original downloaded source or a shared/Unity package cache. The tool refuses recognized cache paths, symlinks, Windows junctions/reparse points and hard-linked input files.

From the exporter repository, first inspect the explicit copy:

```sh
python Tools/patch-aao-vertex-buffer.py --check-only /absolute/path/to/owned-aao-copy
```

Apply the same validated correction explicitly, then check it again:

```sh
python Tools/patch-aao-vertex-buffer.py /absolute/path/to/owned-aao-copy
python Tools/patch-aao-vertex-buffer.py --check-only /absolute/path/to/owned-aao-copy
```

`--check-only` never writes and reports either `checked_original` or `already_patched`. Applying the exact original reports `applied`; applying the exact patched source again reports `already_patched` without rewriting it. JSON output includes the original version, applied patch identity and source hashes, without machine paths. Other names, versions or source changes fail closed. UTF-8 BOM and individual LF/CRLF line endings are preserved; replacement is atomic after package/source validation. Do not run concurrent imports or source writers during application.

After application, refresh and finish compilation using the project's pinned Unity version. Run the installed-AAO Box/export, expression and PhysBone regressions, then reload with native allocation stack detection enabled. Functional test success alone does not prove native handle cleanup. Re-run no-AAO checks separately; this optional correction does not make AAO required.

## Provenance

- Upstream: [Avatar Optimizer v1.9.20, commit `439a56aae2cc744c2a1590e79067353879496b8e`](https://github.com/anatawa12/AvatarOptimizer/blob/439a56aae2cc744c2a1590e79067353879496b8e/Internal/MeshInfo2/MeshInfo2.cs#L296).
- Disposal contract: [Unity 2022.3 `Mesh.GetVertexBuffer`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Mesh.GetVertexBuffer.html).
- Exact source path: `Internal/MeshInfo2/MeshInfo2.cs`.
- Official source SHA-256: `fc6d349153753d83b9e588727ce3328ef3db6c4f185668d8a6a73218a4a3a4af`.
- Patched source SHA-256: `7c002a439e4a19f83f47f8db1f43db16ec1016cbad933f691004f114671516d2`.

Hashes use UTF-8 bytes after removing an optional UTF-8 BOM and converting CRLF to LF. The supplied unified diff is for review; use the hash-pinned tool to apply it. Upstream AAO is MIT licensed; its copyright and license are preserved in [AAO-LICENSE.txt](AAO-LICENSE.txt). This patch is maintained with the exporter until an explicitly reviewed upstream release supplies the correction.
