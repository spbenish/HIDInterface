using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;

namespace USBInterface
{
    /**
     * Custom extension of the device scanner that parses the device info
     * on connection and disconnect events.
     * 
     * The scanner provides a static option to scan the devices, or the
     * scanner can be instantiated to run a scan loop, keeping a list
     * of devices that are connected to it.
     */
    public class DeviceScannerExt
    {
        private HidDeviceInfo[] _devices = Array.Empty<HidDeviceInfo>();

        public event EventHandler<DeviceArrivedArgs> DeviceArrived;
        public event EventHandler<DeviceRemovedArgs> DeviceRemoved;

        public HidDeviceInfo[] ConnectedDevices { get { lock (syncLock) { return (HidDeviceInfo[])_devices.Clone(); } } }

        public bool isDeviceConnected
        {
            get { return _devices.Length > 0; }
        }

        // for async reading
        private object syncLock = new object();
        private Thread scannerThread;
        private volatile bool asyncScanOn = false;

        private int scanIntervalMillisecs = 10;
        public int ScanIntervalInMillisecs
        {
            get { lock (syncLock) { return scanIntervalMillisecs; } }
            set { lock (syncLock) { scanIntervalMillisecs = value; } }
        }

        public bool isScanning
        {
            get { return asyncScanOn; }
        }

        private ushort vendorId;
        private ushort productId;

        // The VendorID filtered on, or null if the VID is a wildcard.
        public ushort? VIDFilter
        {
            get
            {
                if (vendorId == 0)
                    return null;
                return vendorId;
            }
        }

        // The ProductID filtered on, or null if the PID is a wildcard.
        public ushort? PIDFilter
        {
            get
            {
                if (productId == 0)
                    return null;
                return productId;
            }
        }

        // Use this class to monitor when your devices connects.
        // Note that scanning for device when it is open by another process will return FALSE
        // even though the device is connected (because the device is unavailiable)
        public DeviceScannerExt(ushort VendorID, ushort ProductID, int scanIntervalMillisecs = 100)
        {
            vendorId = VendorID;
            productId = ProductID;
            ScanIntervalInMillisecs = scanIntervalMillisecs;
        }

        /**
         * Statically scans the connected devices, returning a list of device information structs.
         * \param[in]       vid The VID to filter on.  If set to null, the vid is not a filter.
         * \param[in]       pid The PID to filter on.  If set to null, the pid is not a filter.
         * \return An array of HID information structs that were detected and passed the filters.
         */
        public HidDeviceInfo[] ScanOnce(ushort? vid, ushort? pid)
        {
            if (vid is null)
                vid = 0;
            if (pid is null)
                pid = 0;

            IntPtr ll = HidApi.hid_enumerate((ushort)vid, (ushort)pid);
            var rt = HandleInfoLL(ll);
            HidApi.hid_free_enumeration(ll);

            return rt;
        }

        /**
         * Begins the event-driven scanner thread.
         */
        public void StartAsyncScan()
        {
            // Build the thread to listen for reads
            if (asyncScanOn)
            {
                // dont run more than one thread
                return;
            }
            asyncScanOn = true;
            scannerThread = new Thread(ScanLoop);
            scannerThread.Name = "HidApiAsyncDeviceScanThread";
            scannerThread.Start();
        }

        /**
         * Stops the event-driven scanner thread.
         */
        public void StopAsyncScan()
        {
            asyncScanOn = false;
        }

        private void ScanLoop()
        {
            var culture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            // The read has a timeout parameter, so every X milliseconds
            // we check if the user wants us to continue scanning.
            while (asyncScanOn)
            {
                IntPtr device_info = IntPtr.Zero;
                try
                {
                    // Get the devices on the bus.
                    device_info = HidApi.hid_enumerate(vendorId, productId);
                    HidDeviceInfo[] devices = HandleInfoLL(device_info);
                    bool device_on_bus = device_info != IntPtr.Zero;
                    // freeing the enumeration releases the device, 
                    // do it as soon as you can, so we dont block device from others
                    HidApi.hid_free_enumeration(device_info);
                    device_info = IntPtr.Zero; // Reset the pointer so it does not double-free on a later exception.

                    // Find what devices were added and removed.
                    var added_devices = devices.Except(_devices);
                    var removed_devices = _devices.Except(devices);

                    // Set up the devices list.
                    lock (syncLock)
                    {
                        _devices = devices;
                    }

                    foreach (var device in added_devices)
                    {
                        DeviceArrivedArgs args = new DeviceArrivedArgs(device);
                        DeviceArrived?.Invoke(this, args);
                    }

                    foreach (var device in removed_devices)
                    {
                        DeviceRemovedArgs args = new DeviceRemovedArgs(device);
                        DeviceRemoved?.Invoke(this, args);
                    }
                }
                catch (Exception e)
                {
                    // stop scan, user can manually restart again with StartAsyncScan()
                    asyncScanOn = false;
                    
                    // Check to see if the pointer needs to be freed.
                    if (device_info != IntPtr.Zero)
                    {
                        HidApi.hid_free_enumeration(device_info);
                    }
                }
                // when read 0 bytes, sleep and read again
                Thread.Sleep(ScanIntervalInMillisecs);
            }
        }

        /**
         * Create an array of managed objects from the hid_device_info linked list.
         */
        private HidDeviceInfo[] HandleInfoLL (IntPtr p_device_info)
        {
            List<HidDeviceInfo> list = new List<HidDeviceInfo>();

            // If the pointer is null, then no structures exist.
            while (p_device_info != IntPtr.Zero)
            {
                // Grab a structure from the pointer.
                HidDeviceInfo_Raw raw_info = Marshal.PtrToStructure<HidDeviceInfo_Raw>(p_device_info);

                // Convert to manged structure.
                HidDeviceInfo info = new HidDeviceInfo(raw_info);

                // Add it to the linked list.
                list.Add(info);

                // Move the pointer to the next element in the linked list.
                p_device_info = raw_info.next;
            }

            return list.ToArray();
        }
    }

    /**
     * Arguments for the DeviceScannerExt's DeviceArrived event.
     */
    public class DeviceArrivedArgs
    {
        // The device that was added.
        public HidDeviceInfo AttachedDevice { get; }

        internal DeviceArrivedArgs(HidDeviceInfo attachedDevice)
        {
            AttachedDevice = attachedDevice;
        }
    }

    /**
     * Arguments for the DeviceScannerExt's DeviceRemoved event.
     */
    public class DeviceRemovedArgs
    {
        // The device that was removed.
        public HidDeviceInfo RemovedDevice { get; }

        internal DeviceRemovedArgs(HidDeviceInfo attachedDevice)
        {
            RemovedDevice = attachedDevice;
        }
    }

}
