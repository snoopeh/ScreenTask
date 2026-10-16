using System;
using System.Runtime.InteropServices;

namespace ScreenTask
{
    // Read Windows' active output color space and calibrated display luminance.
    // No system HDR settings or monitor profiles are changed.
    internal static class HdrDisplayProbe
    {
        internal sealed class Profile
        {
            internal bool Known, Enabled;
            internal int PeakNits = 1000;
        }
        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Enumerate(IntPtr self, uint index, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescription(IntPtr self, out Description description);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Description
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
            internal int Left, Top, Right, Bottom;
            internal int Attached, Rotation;
            internal IntPtr Monitor;
            internal uint BitsPerColor, ColorSpace;
            internal float RedX, RedY, GreenX, GreenY, BlueX, BlueY, WhiteX, WhiteY;
            internal float MinLuminance, MaxLuminance, FullFrameLuminance;
        }
        private static T Method<T>(IntPtr pointer, int index) where T : class
        {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), index * IntPtr.Size), typeof(T)) as T;
        }

        internal static Profile Read(string deviceName)
        {
            var result = new Profile();
            IntPtr factory = IntPtr.Zero;
            try
            {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref iid, out factory) < 0) return result;
                for (uint a = 0; a < 32; a++)
                {
                    IntPtr adapter;
                    if (Method<Enumerate>(factory, 12)(factory, a, out adapter) < 0) break;
                    try
                    {
                        for (uint n = 0; n < 32; n++)
                        {
                            IntPtr output;
                            if (Method<Enumerate>(adapter, 7)(adapter, n, out output) < 0) break;
                            IntPtr output6 = IntPtr.Zero;
                            try
                            {
                                var outputId = new Guid("068346e8-aaec-4b84-add7-137f513f77a1");
                                if (Marshal.QueryInterface(output, ref outputId, out output6) < 0) continue;
                                Description desc;
                                if (Method<GetDescription>(output6, 27)(output6, out desc) < 0 || !String.Equals(deviceName, desc.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                                return FromMetadata(desc.ColorSpace, desc.MaxLuminance);
                            }
                            finally { if (output6 != IntPtr.Zero) Marshal.Release(output6); Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is COMException) { }
            finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
            return result;
        }

        internal static Profile FromMetadata(uint colorSpace, float peak)
        {
            return new Profile { Known = true, Enabled = colorSpace == 12,
                PeakNits = Single.IsNaN(peak) || Single.IsInfinity(peak) || peak <= 0 ? 1000 : (int)Math.Max(400, Math.Min(4000, Math.Round(peak))) };
        }
    }
}
