#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Dump PE exports + sections + imports for one or more binaries."""
import sys, os
import pefile


def dump(path):
    print('=' * 78)
    print('FILE:', path, os.path.getsize(path), 'bytes')
    pe = pefile.PE(path, fast_load=False)
    print('Machine: 0x%x  TimeDateStamp: %s  ImageBase: 0x%x  Subsystem: %d' % (
        pe.FILE_HEADER.Machine, pe.FILE_HEADER.TimeDateStamp, pe.OPTIONAL_HEADER.ImageBase,
        pe.OPTIONAL_HEADER.Subsystem))
    print('DLL characteristics: 0x%x' % pe.OPTIONAL_HEADER.DllCharacteristics)

    print('-- sections --')
    for s in pe.sections:
        print('  %-8s VA=0x%08x VS=%d RAW=0x%08x RS=%d  ch=0x%08x' % (
            s.Name.rstrip(b'\x00').decode('latin1'), s.VirtualAddress, s.Misc_VirtualSize,
            s.PointerToRawData, s.SizeOfRawData, s.Characteristics))

    if hasattr(pe, 'DIRECTORY_ENTRY_EXPORT'):
        exp = pe.DIRECTORY_ENTRY_EXPORT
        print('-- exports: DLL=%s count=%d --' % (
            exp.name.decode('latin1') if exp.name else '?', len(exp.symbols)))
        for s in exp.symbols:
            nm = s.name.decode('latin1') if s.name else ''
            fwd = ''
            if s.forwarder:
                fwd = ' -> ' + s.forwarder.decode('latin1')
            if nm:
                print('   [%3d] 0x%08x %s%s' % (s.ordinal, s.address, nm, fwd))
            else:
                print('   [%3d] 0x%08x (ordinal only)%s' % (s.ordinal, s.address, fwd))
    else:
        print('-- no export directory --')

    print('-- imports --')
    if hasattr(pe, 'DIRECTORY_ENTRY_IMPORT'):
        for entry in pe.DIRECTORY_ENTRY_IMPORT:
            names = []
            for imp in entry.imports:
                if imp.name:
                    names.append(imp.name.decode('latin1'))
            print('   %s: %d funcs' % (entry.dll.decode('latin1'), len(names)))
            if len(names) <= 60:
                print('      ' + ', '.join(names))
    else:
        print('   none')
    pe.close()


if __name__ == '__main__':
    for p in sys.argv[1:]:
        dump(p)
