# Unity class layout data

`classdata.tpk` is AssetRipper's LZ4 type tree package, downloaded 2026-11-07
from the artifact built 2026-11-01:
https://nightly.link/AssetRipper/Tpk/workflows/type_tree_tpk/master/lz4_file.zip

SHA-256: `fda11e665762795cf3ae8b04217510e9ebe3096adbddbc1b7786bdb00c5b6835`

Upstream: https://github.com/AssetRipper/Tpk (MIT, license below).
Layout information: https://github.com/AssetRipper/TypeTreeDumps .
This is structural metadata, not game content. It is embedded so extraction
works offline and never fetches changing metadata during installation.

To update, obtain the upstream LZ4 artifact, replace the file and record its
date and hash here; run the stripped Mono fixture and real-game checks again.

MIT License

Copyright (c) 2022 ds5678

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
