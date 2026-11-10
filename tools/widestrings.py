#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Extract candidate UTF-16LE / ASCII identifier strings from a PE's .rdata-like sections."""
import sys, re
import pefile

IDENT = re.compile(r'^[A-Za-z][A-Za-z0-9_.\- ]{2,63}$')


def wide_strings(data):
    out = []
    # utf-16le runs of printable ascii
    i = 0
    n = len(data)
    cur = []
    start = 0
    while i + 1 < n:
        lo, hi = data[i], data[i + 1]
        if hi == 0 and 0x20 <= lo < 0x7f:
            if not cur:
                start = i
            cur.append(chr(lo))
            i += 2
        else:
            if len(cur) >= 4:
                out.append((start, ''.join(cur)))
            cur = []
            i += 1
    if len(cur) >= 4:
        out.append((start, ''.join(cur)))
    return out


def main(path):
    pe = pefile.PE(path, fast_load=True)
    for s in pe.sections:
        name = s.Name.rstrip(b'\x00').decode('latin1')
        if name not in ('.rdata', '.data'):
            continue
        data = pe.get_data(s.VirtualAddress, min(s.Misc_VirtualSize, s.SizeOfRawData))
        ws = wide_strings(data)
        print('=' * 70)
        print('SECTION', name, 'wide strings:', len(ws))
        # group consecutive utf16 strings that look like identifiers
        seen = set()
        for off, txt in ws:
            if not IDENT.match(txt):
                continue
            if txt in seen:
                continue
            seen.add(txt)
            print('  0x%08x  %s' % (s.VirtualAddress + off, txt))
    pe.close()


if __name__ == '__main__':
    main(sys.argv[1])
