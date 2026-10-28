param([string]$Keys = "", [int]$Gap = 700)
# Read-only probe of LG UltraFine brightness (HID feature report 0), optionally sends Ctrl+VolUp/Down
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;
public static class Hid {
  [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
  [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
  [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
  [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, byte[] caps);
  [DllImport("setupapi.dll", CharSet=CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int f);
  [DllImport("setupapi.dll", CharSet=CharSet.Unicode)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DID did);
  [DllImport("setupapi.dll", CharSet=CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DID did, IntPtr buf, int size, out int req, IntPtr di);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
  [StructLayout(LayoutKind.Sequential)] public struct SP_DID { public int cbSize; public Guid g; public int flags; public IntPtr r; }
  public static List<string> Read() {
    var res = new List<string>(); Guid g; HidD_GetHidGuid(out g);
    IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
    for (int i = 0; ; i++) {
      var did = new SP_DID(); did.cbSize = Marshal.SizeOf(did);
      if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;
      int req; SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
      IntPtr buf = Marshal.AllocHGlobal(req); Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
      SetupDiGetDeviceInterfaceDetail(set, ref did, buf, req, out req, IntPtr.Zero);
      string path = Marshal.PtrToStringUni(buf + 4); Marshal.FreeHGlobal(buf);
      if (path.IndexOf("vid_043e", StringComparison.OrdinalIgnoreCase) < 0) continue;
      var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
      if (h.IsInvalid) continue;
      IntPtr pp; if (!HidD_GetPreparsedData(h, out pp)) { h.Close(); continue; }
      var caps = new byte[64]; HidP_GetCaps(pp, caps); HidD_FreePreparsedData(pp);
      ushort usagePage = BitConverter.ToUInt16(caps, 2); ushort featLen = BitConverter.ToUInt16(caps, 8);
      if (usagePage == 0x80 && featLen > 2) {
        var b = new byte[featLen];
        bool ok = HidD_GetFeature(h, b, b.Length);
        int raw = b[1] | (b[2] << 8);
        res.Add(ok ? String.Format("raw=0x{0:X4} ({1}%)", raw, (int)Math.Round(raw * 100.0 / 54000)) : "GetFeature failed " + Marshal.GetLastWin32Error());
      }
      h.Close();
    }
    return res;
  }
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  public static void CtrlKey(byte vk) { keybd_event(0x11,0,0,UIntPtr.Zero); keybd_event(vk,0,1,UIntPtr.Zero); keybd_event(vk,0,3,UIntPtr.Zero); keybd_event(0x11,0,2,UIntPtr.Zero); }
}
"@
"avant : " + ([Hid]::Read() -join ", ")
foreach ($k in $Keys.ToCharArray()) {
  if ($k -eq 'u') { [Hid]::CtrlKey(0xAF) } elseif ($k -eq 'd') { [Hid]::CtrlKey(0xAE) }
  Start-Sleep -Milliseconds $Gap
  "apres $k : " + ([Hid]::Read() -join ", ")
}


