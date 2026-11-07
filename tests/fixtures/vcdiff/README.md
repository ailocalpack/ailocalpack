# VCDIFF fixtures

Synthetic `source.bin` → `target.bin` (text and random bytes, no game files) and the deltas xdelta3 3.2.2 made from them
(`VcdiffDecoderTests`, `PatchApplierTests`):

```sh
xdelta3 -e -f -s source.bin target.bin lzma.vcdiff                       # default: lzma secondary compression
xdelta3 -e -f -S none -s source.bin target.bin plain.vcdiff
xdelta3 -e -f -S none -W 16384 -s source.bin target.bin windows.vcdiff  # 4 windows
xdelta3 -e -f -W 16384 -s source.bin target.bin lzma-windows.vcdiff
xdelta3 -e -f -S djw -s source.bin target.bin djw.vcdiff                # not read: Unsupported
xdelta3 -e -f -S none -n -s source.bin target.bin no-checksum.vcdiff    # no per-window Adler-32
xdelta3 -e -f target.bin no-source.vcdiff                               # no source file at all
```
