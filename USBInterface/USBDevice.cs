using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

// alternative
using System.Globalization;
using System.IO;
using System.Threading;

namespace USBInterface
{

    public delegate void InputReportArrivedHandler(object sender, ReportEventArgs args);

    public delegate void DeviceDisconnectedHandler(object sender, EventArgs args);

    public class USBDevice : IDisposable
    {

        #region Native Methods
#if WIN64
        public const string DLL_FILE_NAME = "hidapi64.dll";
#else
        public const string DLL_FILE_NAME = "hidapi.dll";
#endif

        /// Return Type: int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_init();


        /// Return Type: int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_exit();


        /// Return Type: hid_device*
        ///vendor_id: unsigned short
        ///product_id: unsigned short
        ///serial_number: wchar_t*
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr hid_open(ushort vendor_id, ushort product_id, [In] string serial_number);


        /// Return Type: hid_device*
        ///path: char*
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr hid_open_path([In] string path);


        /// Return Type: int
        ///device: hid_device*
        ///data: unsigned char*
        ///length: size_t->unsigned int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_write(IntPtr device, [In] byte[] data, uint length);


        /// Return Type: int
        ///dev: hid_device*
        ///data: unsigned char*
        ///length: size_t->unsigned int
        ///milliseconds: int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_read_timeout(IntPtr device, [Out] byte[] buf_data, uint length, int milliseconds);


        /// Return Type: int
        ///device: hid_device*
        ///data: unsigned char*
        ///length: size_t->unsigned int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_read(IntPtr device, [Out] byte[] buf_data, uint length);


        /// Return Type: int
        ///device: hid_device*
        ///nonblock: int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private extern static int hid_set_nonblocking(IntPtr device, int nonblock);


        /// Return Type: int
        ///device: hid_device*
        ///data: char*
        ///length: size_t->unsigned int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_send_feature_report(IntPtr device, [In] byte[] data, uint length);


        /// Return Type: int
        ///device: hid_device*
        ///data: unsigned char*
        ///length: size_t->unsigned int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_get_feature_report(IntPtr device, [Out] byte[] buf_data, uint length);


        /// Return Type: void
        ///device: hid_device*
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl)]
        private extern static void hid_close(IntPtr device);


        /// Return Type: int
        ///device: hid_device*
        ///string: wchar_t*
        ///maxlen: size_t->unsigned int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        private static extern int hid_get_manufacturer_string(IntPtr device, StringBuilder buf_string, uint length);


        /// Return Type: int
        ///device: hid_device*
        ///string: wchar_t*
        ///maxlen: size_t->unsigned int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        private static extern int hid_get_product_string(IntPtr device, StringBuilder buf_string, uint length);


        /// Return Type: int
        ///device: hid_device*
        ///string: wchar_t*
        ///maxlen: size_t->unsigned int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        private static extern int hid_get_serial_number_string(IntPtr device, StringBuilder buf_serial, uint maxlen);


        /// Return Type: int
        ///device: hid_device*
        ///string_index: int
        ///string: wchar_t*
        ///maxlen: size_t->unsigned int
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        private static extern int hid_get_indexed_string(IntPtr device, int string_index, StringBuilder buf_string, uint maxlen);


        /// Return Type: wchar_t*
        ///device: hid_device*
        [DllImport(DLL_FILE_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
        private static extern IntPtr hid_error(IntPtr device);

        #endregion

        // for async reading
        private Object syncLock = new object();
        private Thread readThread;
        private volatile bool asyncReadOn = false;


        // Flag: Has Dispose already been called?
        // Marked as volatile because Dispose() can be called from another thread.
        private volatile bool disposed = false;

        private int readTimeoutInMillisecs = 100;
        public int ReadTimeoutInMillisecs
        {
            get { return readTimeoutInMillisecs; }
            set { readTimeoutInMillisecs = value; }
        }

        public event EventHandler<ReportEventArgs> InputReportArrivedEvent;

        public event EventHandler DeviceDisconnecedEvent;

        public bool isOpen
        {
            get { return DeviceHandle != IntPtr.Zero; }
        }

        private IntPtr DeviceHandle = IntPtr.Zero;

        // this will be the return buffer for strings,
        // make it big, becasue by the HID spec (can not find page)
        // we are allowed to request more bytes than the device can return.
        private StringBuilder pOutBuf = new StringBuilder(1024);

        private int ReportLength;

        // This only affects the read function.
        // receiving / sending a feature report,
        // and writing to device always requiers you to prefix the
        // data with a Report ID (use 0x00 if device does not use Report IDs)
        // however when reading if the device does NOT use Report IDs then
        // the prefix byte is NOT inserted. On the other hand if the device uses 
        // Report IDs then when reading we must read +1 byte and byte 0 
        // of returned data array will be the Report ID.
        bool hasReportIds = false;

        // HIDAPI does not provide any way to get HID Report Descriptor,
        // This means you must know in advance what it the report size for your device.
        // For this reason, reportLen is a necessary parameter to the constructor.
        // 
        // Serial Number is optional, pass null (do NOT pass an empty string) if it is unknown.
        public USBDevice(ushort VendorID
            , ushort ProductID
            , string serial_number
            , int reportLen
            , bool HasReportIDs)
        {
            DeviceHandle = hid_open(VendorID, ProductID, serial_number);
            AssertValidDev();
            ReportLength = reportLen;
            hasReportIds = HasReportIDs;
        }

        private void AssertValidDev()
        {
            if (DeviceHandle == IntPtr.Zero) throw new Exception("No device opened");
        }

        public void GetFeatureReport(byte[] buffer, int length = -1)
        {
            AssertValidDev();
            if (length < 0)
            {
                length = buffer.Length;
            }
            if (hid_get_feature_report(DeviceHandle, buffer, (uint)length) < 0)
            {
                throw new Exception("failed to get feature report");
            }
        }

        public void SendFeatureReport(byte[] buffer, int length = -1)
        {
            AssertValidDev();
            if (length < 0)
            {
                length = buffer.Length;
            }
            if (hid_send_feature_report(DeviceHandle, buffer, (uint)length) < 0)
            {
                throw new Exception("failed to send feature report");
            }
        }

        // either everything is good, or throw exception
        // Meaning InputReport
        // This function is slightly different, as we must return the number of bytes read.
        private int ReadRaw(byte[] buffer, int length = -1)
        {
            AssertValidDev();
            if (length < 0)
            {
                length = buffer.Length;
            }
            int bytes_read = hid_read_timeout(DeviceHandle, buffer, (uint)length, readTimeoutInMillisecs);
            if (bytes_read < 0)
            {
                throw new Exception("Failed to Read.");
            }
            return bytes_read;
        }

        // Meaning OutputReport
        private void WriteRaw(byte[] buffer, int length = -1)
        {
            AssertValidDev();
            if (length < 0)
            {
                length = buffer.Length;
            }
            if (hid_write(DeviceHandle, buffer, (uint)length) < 0)
            {
                throw new Exception("Failed to write.");
            }
        }

        public string GetErrorString()
        {
            AssertValidDev();
            IntPtr ret = hid_error(DeviceHandle);
            // I can not find the info in the docs, but guess this frees 
            // the ret pointer after we created a managed string object
            // else this would be a memory leak
            return Marshal.PtrToStringAuto(ret);
        }

        // All the string functions are in a little bit of trouble becasue 
        // wchar_t is 2 bytes on windows and 4 bytes on linux.
        // So we should just alloc a hell load of space for the return buffer.
        // 
        // We must divide Capacity / 4 because this takes the buffer length in multiples of 
        // wchar_t whoose length is 4 on Linux and 2 on Windows. So we allocate a big 
        // buffer beforehand and just divide the capacity by 4.
        public string GetIndexedString(int index)
        {
            AssertValidDev();
            if (hid_get_indexed_string(DeviceHandle, index, pOutBuf, (uint)pOutBuf.Capacity / 4) < 0)
            {
                throw new Exception("failed to get indexed string");
            }
            return pOutBuf.ToString();
        }

        public string GetManufacturerString()
        {
            AssertValidDev();
            pOutBuf.Clear();
            if (hid_get_manufacturer_string(DeviceHandle, pOutBuf, (uint)pOutBuf.Capacity / 4) < 0)
            {
                throw new Exception("failed to get manufacturer string");
            }
            return pOutBuf.ToString();
        }

        public string GetProductString()
        {
            AssertValidDev();
            pOutBuf.Clear();
            if (hid_get_product_string(DeviceHandle, pOutBuf, (uint)pOutBuf.Capacity / 4) < 0)
            {
                throw new Exception("failed to get product string");
            }
            return pOutBuf.ToString();
        }

        public string GetSerialNumberString()
        {
            AssertValidDev();
            pOutBuf.Clear();
            if (hid_get_serial_number_string(DeviceHandle, pOutBuf, (uint)pOutBuf.Capacity / 4) < 0)
            {
                throw new Exception("failed to get serial number string");
            }
            return pOutBuf.ToString();
        }

        public string Description()
        {
            AssertValidDev();
            return string.Format("Manufacturer: {0}\nProduct: {1}\nSerial number:{2}\n"
                , GetManufacturerString(), GetProductString(), GetSerialNumberString());
        }


        public void Write(byte[] user_data)
        {
            // so we don't read and write at the same time
            lock (syncLock)
            {
                byte[] output_report = new byte[ReportLength + 1];
                // byte 0 is command byte
                output_report[0] = 0;
                Array.Copy(user_data, 0, output_report, 1, output_report.Length);
                WriteRaw(output_report);
            }
        }

        // Returnes a bytes array.
        // If an error occured while reading an exception will be 
        // thrown by the underlying ReadRaw method
        public byte[] Read()
        {
            lock(syncLock)
            {
                int length = hasReportIds ? ReportLength + 1 : ReportLength;
                byte[] input_report = new byte[length];
                int read_bytes = ReadRaw(input_report);
                byte[] ret = new byte[read_bytes];
                Array.Copy(input_report, 0, ret, 0, read_bytes);
                return ret;
            }
        }

        public void RunAsyncRead()
        {
            // Build the thread to listen for reads
            asyncReadOn = true;
            readThread = new Thread(ReadLoop);
            readThread.Name = "HidApiReadAsyncThread";
            readThread.Start();
        }

        public void StopAsyncRead()
        {
            asyncReadOn = false;
        }

        private void ReadLoop()
        {
            var culture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            // The read has a timeout parameter, so every X milliseconds
            // we check if the user wants us to continue reading.
            while (asyncReadOn)
            {
                try
                {
                    byte[] res = Read();
                    // when read >0 bytes, tell others about data
                    if (res.Length > 0 && this.InputReportArrivedEvent != null)
                    {
                        InputReportArrivedEvent(this, new ReportEventArgs(res));
                    }
                }
                catch (Exception)
                {
                    // when read <0 bytes, means an error has occurred
                    // stop device, break from loop and stop this thread
                    if (this.DeviceDisconnecedEvent != null)
                    {
                        DeviceDisconnecedEvent(this, EventArgs.Empty);
                    }
                    // call the dispose method in separate thread, 
                    // otherwise this thread would never get to die
                    new Thread(Dispose).Start();
                    break;
                }
                // when read 0 bytes, sleep and read again
                Thread.Sleep(1);
            }
        }

        // Public implementation of Dispose pattern callable by consumers.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }
            if (disposing)
            {
                // Free any other managed objects here.
                if (asyncReadOn)
                {
                    asyncReadOn = false;
                    readThread.Join(500);
                    if (readThread.IsAlive)
                    {
                        readThread.Abort();
                    }
                }
                if (isOpen)
                {
                    // so we are not reading or writing as the device gets closed
                    lock(syncLock)
                    {
                        AssertValidDev();
                        hid_close(DeviceHandle);
                        DeviceHandle = IntPtr.Zero;
                    }
                }
                hid_exit();
            }
            // Free any unmanaged objects here.
            // mark object as having been disposed
            disposed = true;
        }

        private string EncodeBuffer(byte[] buffer)
        {
            // the buffer contains trailing '\0' char to mark its end.
            return Encoding.Unicode.GetString(buffer).Trim('\0');
        }

    }
}


