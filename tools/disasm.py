#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Decode instructions at an RVA inside a PE using capstone (32/64 auto)."""
import sys
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_MODE_64


def main(path, rva_hex, count=40):
    rva = int(rva_hex, 16)
    pe = pefile.PE(path, fast_load=True)
    bits = 64 if pe.FILE_HEADER.Machine == 0x8664 else 32
    imgbase = pe.OPTIONAL_HEADER.ImageBase
    for s in pe.sections:
        name = s.Name.rstrip(b'\x00').decode('latin1')
        vs = s.Misc_VirtualSize
        if s.VirtualAddress <= rva < s.VirtualAddress + vs:
            delta = rva - s.VirtualAddress
            data = pe.get_data(s.VirtualAddress, min(vs, s.SizeOfRawData))
            code = data[delta:delta + 512]
            md = Cs(CS_ARCH_X86, CS_MODE_32 if bits == 32 else CS_MODE_64)
            n = 0
            for insn in md.disasm(code, imgbase + rva):
                print('0x%08x  %-30s %s %s' % (insn.address, insn.bytes.hex(), insn.mnemonic, insn.op_str))
                n += 1
                if n >= count:
                    break
            pe.close()
            return
    print('RVA not found in any section')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 40)
