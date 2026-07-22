# Neo4j Bolt Connector — icon set

Original artwork: three connected nodes, one accented, on the same dark rounded square as the
jonasfiers.eu favicons. Palette matches that site (`#12172B` field, `#C6F135` accent,
`#EDEFF7`/`#C3CADF` nodes and edges).

| File | Use |
|---|---|
| `neo4j-connector.ico` | **Integration Studio** — extension icon and per-action icons. Multi-size: 16/24/32/48/64/128/256. |
| `neo4j-connector.svg` | Master. 32×32 viewBox; edit this and re-render. |
| `neo4j-connector-<n>.png` | 16 … 1024. For the Service Studio application/module icon and Forge listing artwork. |

## Why the .ico is built the way it is

Sizes up to 128 are **BMP/DIB-encoded**, only 256 is PNG-compressed. Integration Studio is a .NET
WinForms application, and `System.Drawing.Icon` does not reliably decode PNG-compressed entries below
256px — the likely cause of the "Unsupported image format or invalid image" error people hit when
selecting a custom icon. Tooling that writes PNG entries at every size (including Pillow's default
`save(..., sizes=[...])`) produces an .ico that looks fine everywhere except the place it is needed.

If you regenerate this, verify the encoding rather than assuming:

```python
import struct
d = open('neo4j-connector.ico','rb').read()
for i in range(struct.unpack_from('<H', d, 4)[0]):
    off = 6 + i*16
    size, o = struct.unpack_from('<II', d, off+8)
    print(d[off] or 256, 'PNG' if d[o:o+8] == b'\x89PNG\r\n\x1a\n' else 'BMP/DIB')
```

## Design constraint

OutSystems' own icon system uses a 16×16 base grid with a 14px live area and 1px padding, scaled to
24 and 32. The artwork was tuned by inspecting the actual rendered pixels at 16px — an earlier draft
with thicker edges closed up the triangle and read as a blob at small sizes.

## Trademark note

This is **not** the Neo4j logo. Neo4j's mark is a registered trademark, and their trademark policy
was not readable at the time of writing (HTTP 403), so it was not used. This original graph motif
avoids the question entirely. If you would rather ship the official mark, check Neo4j's brand
guidelines first — connector components generally may name the product but not all uses of the logo
are permitted.
