#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Scan a directory of PE binaries for interesting ASCII / UTF-16LE strings with context."""
import os, re, sys, json

def read(p):
    try:
        with open(p, 'rb') as f:
            return f.read()
    except Exception:
        return None

def ctx(data, off, before=70, after=90):
    s = data[max(0, off - before): off + after]
    return re.sub(rb'[^\x20-\x7e]', b'.', s).decode('ascii')

def main(root, keys, limit=60):
    keys_b = [(k, k.encode('ascii'), k.encode('utf-16-le')) for k in keys]
    out = {}
    for fn in sorted(os.listdir(root)):
        p = os.path.join(root, fn)
        if not os.path.isfile(p):
            continue
        data = read(p)
        if data is None or len(data) > 40 * 1024 * 1024:
            continue
        found = []
        for name, kb, kw in keys_b:
            for enc, pat in (('ascii', kb), ('utf16', kw)):
                for m in re.finditer(re.escape(pat), data):
                    found.append((name, enc, m.start()))
        if found:
            seen = set()
            print('== %s (%d bytes) hits=%d' % (fn, len(data), len(found)))
            for name, enc, off in found[:limit]:
                c = ctx(data, off)
                key = c[40:110]
                if key in seen:
                    continue
                seen.add(key)
                print('   [%s/%s] @0x%x %s' % (name, enc, off, c))
            out[fn] = len(found)
    print('\nSUMMARY:', json.dumps(out, ensure_ascii=False, indent=1))

if __name__ == '__main__':
    root = sys.argv[1]
    keys = sys.argv[2].split(',')
    lim = int(sys.argv[3]) if len(sys.argv) > 3 else 60
    main(root, keys, lim)
