using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

namespace USBInterface
{
    /**
     * Copy of the hid_device_info structure in the hidapi dll.
     */
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct HidDeviceInfo_Raw
    {
        public IntPtr path; //sbyte pointer
        public ushort vendor_id;
        public ushort product_id;
        public IntPtr serial_number; //wchar poitner
        public ushort release_number;
        public IntPtr manufacturer_string; //wchar pointer
        public IntPtr product_string; //wchar pointer
        public ushort usage_page;
        public ushort usage;
        public int interface_number;
        public IntPtr next; //HidDeviceInfo pointer
    }

    public class HidDeviceInfo
    {
        public string path;
        public ushort vendor_id;
        public ushort product_id;
        public string serial_number;
        public ushort release_number;
        public string manufacturer_string;
        public string product_string;
        public ushort usage_page;
        public ushort usage;
        public int interface_number;

        internal HidDeviceInfo(HidDeviceInfo_Raw raw)
        {
            path = Marshal.PtrToStringAnsi(raw.path);
            vendor_id = raw.vendor_id;
            product_id = raw.product_id;
            serial_number = Marshal.PtrToStringUni(raw.serial_number);
            release_number = raw.release_number;
            manufacturer_string = Marshal.PtrToStringUni(raw.manufacturer_string);
            product_string = Marshal.PtrToStringUni(raw.product_string);
            usage_page = raw.usage_page;
            usage = raw.usage;
            interface_number = raw.interface_number;
        }

        public override int GetHashCode()
        {
            return path.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            HidDeviceInfo info = obj as HidDeviceInfo;
            if (info != null)
            {
                return path == info.path;
            }
            return false;
        }
    }

}
