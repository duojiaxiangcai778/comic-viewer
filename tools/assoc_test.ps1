Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class AssocTest {
  [DllImport("shlwapi.dll", CharSet=CharSet.Unicode)]
  static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder outBuf, ref int outLen);

  public static string Get(int kind, string ext) {
    var sb = new StringBuilder(2048);
    int len = 2048;
    int hr = AssocQueryString(0, kind, ext, null, sb, ref len);
    if (hr != 0) return "hr=0x" + hr.ToString("x8");
    return sb.ToString();
  }
}
'@ -ErrorAction SilentlyContinue

"[$([Environment]::Is64BitProcess)] 位数进程: $([Environment]::Is64BitProcess)"
"ASSOCSTR_EXECUTABLE (.webp) : " + [AssocTest]::Get(2, '.webp')
"ASSOCSTR_COMMAND    (.webp) : " + [AssocTest]::Get(1, '.webp')
"ASSOCSTR_EXECUTABLE (.png)  : " + [AssocTest]::Get(2, '.png')
"ASSOCSTR_COMMAND    (.png)  : " + [AssocTest]::Get(1, '.png')
