# Bundled fonts

Open fonts the toolkit swaps into games whose own font lacks the letters of a translation (task 31; list and hashes in
`../fonts.json`). Shipped inside the app as embedded resources; the CJK fonts of the whitelist are downloaded on demand
from the pinned commit named in `fonts.json`.

| File | Source |
| --- | --- |
| `NotoSans-{Regular,Bold}.ttf` | `notofonts/notofonts.github.io` @ `86ce02c0fe7890f5936c5dffb26fca0797d1be64`, `fonts/NotoSans/hinted/ttf/` |
| `NotoSansArabic-{Regular,Bold}.ttf` | same commit, `fonts/NotoSansArabic/hinted/ttf/` |
| `NotoSansThai-{Regular,Bold}.ttf` | same commit, `fonts/NotoSansThai/hinted/ttf/` |

Copyright 2022 The Noto Project Authors (https://github.com/notofonts). Licensed under the SIL Open Font License,
Version 1.1 (`OFL.txt`). The files are unmodified.
