#!/usr/bin/env python3
"""pc_audit — push-constant size audit for every compute kernel in the repo.

Godot rounds a push-constant block up to a multiple of 16 bytes, so a kernel declaring
e.g. 40 B makes the pipeline demand 48 and rejects the host's 10-float array outright at
dispatch time. Nothing catches it at build: the C# compiles, the shader compiles, and the
scene fails only when it runs. That has cost four separate debugging passes on this repo.

This prints the declared size and the size the pipeline will actually demand, so any kernel
whose host array must be padded is visible before it is written rather than after it fails.

    python3 tools/pc_audit.py
"""
import glob, os, re, sys

SIZES = {'vec2': 8, 'vec3': 12, 'vec4': 16, 'float': 4, 'int': 4, 'uint': 4}


def block_bytes(src):
    m = re.search(r'layout\(push_constant[^)]*\)\s*uniform\s+\w+\s*\{(.*?)\}', src, re.S)
    if not m:
        return None
    body = re.sub(r'//.*', '', m.group(1))
    return sum(SIZES[t] for t, _ in re.findall(r'\b(' + '|'.join(SIZES) + r')\s+([A-Za-z_]\w*)\s*;', body))


def main():
    pads = 0
    for f in sorted(glob.glob('shaders/**/*.glslinc', recursive=True) + glob.glob('shaders/**/*.glsl', recursive=True)):
        b = block_bytes(open(f).read())
        if b is None:
            continue
        need = ((b + 15) // 16) * 16
        note = '' if b == need else f'  <-- host MUST send {need} B ({need // 4} floats)'
        if note:
            pads += 1
        print(f'{os.path.basename(f):32s} declares {b:3d} B ({b // 4:2d} floats){note}')
    print(f'\n{pads} kernel(s) need host-side padding.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
