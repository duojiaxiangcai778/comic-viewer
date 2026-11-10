#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Find code references to a string (ascii or utf-16le) inside a PE and disassemble around each hit."""
import sys, os, re
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_MODE_64


def sections_of(pe):
    out = []
    for s in pe.sections:
        name = s.Name.rstrip(b'\x00').decode('latin1')
        out.append((name, s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData, s.SizeOfRawData))
    return out


def off_to_va(pe, off):
    for name, va, vs, raw, rs in sections_of(pe):
        if raw <= off < raw + rs:
            return va + (off - raw), name
    return None, None


def read_va(pe, va, n):
    for name, sva, vs, raw, rs in sections_of(pe):
        if sva <= va < sva + vs:
            delta = va - sva
            data = pe.get_data(sva, min(vs, rs))
            return data[delta:delta + n]
    return b''


def find_xrefs(pe, target_va):
    """Scan .text for 32-bit immediates equal to target_va (push/mov/cmp encodings)."""
    hits = []
    for name, sva, vs, raw, rs in sections_of(pe):
        if name != '.text':
            continue
        code = pe.get_data(sva, vs)
        needle = target_va.to_bytes(4, 'little')
        start = 0
        while True:
            i = code.find(needle, start)
            if i < 0:
                break
            # check preceding bytes for common immediate-load encodings
            prev = code[max(0, i - 1):i]
            hits.append((sva + i, prev))
            start = i + 1
    return hits


def disasm_window(pe, va, before=64, after=96, bits=32):
    md = Cs(CS_ARCH_X86, CS_MODE_32 if bits == 32 else CS_MODE_64)
    md.detail = False
    data = read_va(pe, va - before, before + after)
    base = va - before
    lines = []
    for insn in md.disasm(data, base):
        mark = ' <== ' if insn.address <= va < insn.address + insn.size else '     '
        lines.append('%s0x%08x  %-24s %s' % (mark, insn.address, insn.bytes.hex(), insn.mnemonic + ' ' + insn.op_str))
    return lines


def main():
    path = sys.argv[1]
    targets = sys.argv[2].split(',')
    pe = pefile.PE(path, fast_load=True)
    bits = 64 if pe.FILE_HEADER.Machine == 0x8664 else 32
    imgbase = pe.OPTIONAL_HEADER.ImageBase
    raw = open(path, 'rb').read()

    for t in targets:
        print('#' * 78)
        print('# TARGET STRING:', t)
        for enc, pat in (('ascii', t.encode('ascii')), ('utf16le', t.encode('utf-16-le'))):
            for m in re.finditer(re.escape(pat), raw):
                va, sec = off_to_va(pe, m.start())
                if va is None:
                    continue
                full = imgbase + va
                print('-- %s occurrence @file 0x%x -> RVA 0x%x -> VA 0x%x (sec %s)' % (enc, m.start(), va, full, sec))
                hits = find_xrefs(pe, full)
                if not hits:
                    print('   (no direct imm32 xref found in .text)')
                for hv, prev in hits:
                    print('   XREF at RVA 0x%x (VA 0x%x) prevbyte=%s' % (hv, imgbase + hv, prev.hex()))
                    for line in disasm_window(pe, imgbase + hv, 48, 64, bits):
                        print('      ' + line)
    pe.close()


if __name__ == '__main__':
    main()
