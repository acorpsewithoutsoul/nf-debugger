﻿//
// Copyright (c) 2017 The nanoFramework project contributors
// See LICENSE file in the project root for full license information.
//
using nanoFramework.Tools.Debugger.Extensions;
using nanoFramework.Tools.Debugger.Serial;
using nanoFramework.Tools.Debugger.WireProtocol;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Xaml;

namespace nanoFramework.Tools.Debugger.PortSerial
{
    public class SerialPort : PortBase, IPort
    {
        // dictionary with mapping between Serial device watcher and the device ID
        private Dictionary<DeviceWatcher, string> mapDeviceWatchersToDeviceSelector;

        // Serial device watchers suspended flag
        private bool watchersSuspended = false;

        // Serial device watchers started flag
        private bool watchersStarted = false;

        // counter of device watchers completed
        private int deviceWatchersCompletedCount = 0;
        private bool isAllDevicesEnumerated = false;

        private object cancelIoLock = new object();
        private static SemaphoreSlim semaphore;

        /// <summary>
        /// Internal list with the actual nF Serial devices
        /// </summary>
        List<SerialDeviceInformation> SerialDevices;

        /// <summary>
        /// Creates an Serial debug client
        /// </summary>
        public SerialPort(Application callerApp)
        {
            mapDeviceWatchersToDeviceSelector = new Dictionary<DeviceWatcher, String>();
            NanoFrameworkDevices = new ObservableCollection<NanoDeviceBase>();
            SerialDevices = new List<Serial.SerialDeviceInformation>();

            // set caller app property
            EventHandlerForSerialDevice.CallerApp = callerApp;

            // init semaphore
            semaphore = new SemaphoreSlim(1, 1);

            Task.Factory.StartNew(() =>
            {
                StartSerialDeviceWatchers();
            });
        }


        #region Device watchers initialization

        /*////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        Add a device watcher initialization method for each supported device that should be watched.
        That initialization method must be called from the InitializeDeviceWatchers() method above so the watcher is actually started.
        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////*/

        /// <summary>
        /// Registers for Added, Removed, and Enumerated events on the provided deviceWatcher before adding it to an internal list.
        /// </summary>
        /// <param name="deviceWatcher">The device watcher to subscribe the events</param>
        /// <param name="deviceSelector">The AQS used to create the device watcher</param>
        private void AddDeviceWatcher(DeviceWatcher deviceWatcher, String deviceSelector)
        {
            deviceWatcher.Added += new TypedEventHandler<DeviceWatcher, DeviceInformation>(OnDeviceAdded);
            deviceWatcher.Removed += new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(OnDeviceRemoved);
            deviceWatcher.EnumerationCompleted += new TypedEventHandler<DeviceWatcher, object>(OnDeviceEnumerationComplete);

            mapDeviceWatchersToDeviceSelector.Add(deviceWatcher, deviceSelector);
        }

        #endregion

        #region Device watcher management and host app status handling

        /// <summary>
        /// Initialize device watchers. Must call here the initialization methods for all devices that we want to set watch.
        /// </summary>
        private void InitializeDeviceWatchers()
        {
            // Target all Serial Devices present on the system
            var deviceSelector = SerialDevice.GetDeviceSelector();

            // Other variations of GetDeviceSelector() usage are commented for reference
            //
            // Target a specific Serial Device using its VID and PID 
            // var deviceSelector = SerialDevice.GetDeviceSelectorFromUsbVidPid(0x2341, 0x0043);
            //
            // Target a specific Serial Device by its COM PORT Name - "COM3"
            // var deviceSelector = SerialDevice.GetDeviceSelector("COM3");
            //
            // Target a specific UART based Serial Device by its COM PORT Name (usually defined in ACPI) - "UART1"
            // var deviceSelector = SerialDevice.GetDeviceSelector("UART1");
            //

            // Create a device watcher to look for instances of the Serial Device that match the device selector
            // used earlier.

            var deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);

            // Allow the EventHandlerForDevice to handle device watcher events that relates or effects our device (i.e. device removal, addition, app suspension/resume)
            AddDeviceWatcher(deviceWatcher, deviceSelector);
        }

        public void StartSerialDeviceWatchers()
        {
            // Initialize the Serial device watchers to be notified when devices are connected/removed
            InitializeDeviceWatchers();
            StartDeviceWatchers();
        }

        /// <summary>
        /// Starts all device watchers including ones that have been individually stopped.
        /// </summary>
        private void StartDeviceWatchers()
        {
            // Start all device watchers
            watchersStarted = true;
            deviceWatchersCompletedCount = 0;
            isAllDevicesEnumerated = false;

            foreach (DeviceWatcher deviceWatcher in mapDeviceWatchersToDeviceSelector.Keys)
            {
                if ((deviceWatcher.Status != DeviceWatcherStatus.Started)
                    && (deviceWatcher.Status != DeviceWatcherStatus.EnumerationCompleted))
                {
                    deviceWatcher.Start();
                }
            }
        }

        /// <summary>
        /// Should be called on host app OnAppSuspension() event to properly handle that status.
        /// The DeviceWatchers must be stopped because device watchers will continue to raise events even if
        /// the app is in suspension, which is not desired (drains battery). The device watchers will be resumed once the app resumes too.
        /// </summary>
        public void AppSuspending()
        {
            if (watchersStarted)
            {
                watchersSuspended = true;
                StopDeviceWatchers();
            }
            else
            {
                watchersSuspended = false;
            }
        }

        /// <summary>
        /// Should be called on host app OnAppResume() event to properly handle that status.
        /// See AppSuspending for why we are starting the device watchers again.
        /// </summary>
        public void AppResumed()
        {
            if (watchersSuspended)
            {
                watchersSuspended = false;
                StartDeviceWatchers();
            }
        }

        /// <summary>
        /// Stops all device watchers.
        /// </summary>
        private void StopDeviceWatchers()
        {
            // Stop all device watchers
            foreach (DeviceWatcher deviceWatcher in mapDeviceWatchersToDeviceSelector.Keys)
            {
                if ((deviceWatcher.Status == DeviceWatcherStatus.Started)
                    || (deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted))
                {
                    deviceWatcher.Stop();
                }
            }

            // Clear the list of devices so we don't have potentially disconnected devices around
            ClearDeviceEntries();

            watchersStarted = false;
        }

        #endregion

        #region Methods to manage device list add, remove, etc

        /// <summary>
        /// Creates a DeviceListEntry for a device and adds it to the list of devices
        /// </summary>
        /// <param name="deviceInformation">DeviceInformation on the device to be added to the list</param>
        /// <param name="deviceSelector">The AQS used to find this device</param>
        private async void AddDeviceToList(DeviceInformation deviceInformation, String deviceSelector)
        {
            // search the device list for a device with a matching interface ID
            var serialMatch = FindDevice(deviceInformation.Id);

            // Add the device if it's new
            if (serialMatch == null)
            {
                SerialDevices.Add(new SerialDeviceInformation(deviceInformation, deviceSelector));

                // search the NanoFramework device list for a device with a matching interface ID
                var nanoFrameworkDeviceMatch = FindNanoFrameworkDevice(deviceInformation.Id);

                if (nanoFrameworkDeviceMatch == null)
                {
                    //     Create a new element for this device interface, and queue up the query of its
                    //     device information

                    var newNanoFrameworkDevice = new NanoDevice<NanoSerialDevice>();
                    newNanoFrameworkDevice.Device.DeviceInformation = new SerialDeviceInformation(deviceInformation, deviceSelector);
                    newNanoFrameworkDevice.Parent = this;
                    newNanoFrameworkDevice.DebugEngine = new Engine(this, newNanoFrameworkDevice);
                    newNanoFrameworkDevice.Transport = TransportType.Serial;

                    // Add the new element to the end of the list of devices
                    NanoFrameworkDevices.Add(newNanoFrameworkDevice as NanoDeviceBase);

                    // perform check for valid nanoFramework device is this is not the initial enumeration
                    if (isAllDevicesEnumerated)
                    {
                        // try opening the device to check for a valid nanoFramework device
                        if (await ConnectSerialDeviceAsync(newNanoFrameworkDevice.Device.DeviceInformation).ConfigureAwait(false))
                        {
                            Debug.WriteLine("New Serial device: " + deviceInformation.Id);

                            var name = EventHandlerForSerialDevice.Current.DeviceInformation?.Properties["System.ItemNameDisplay"] as string;

                            // acceptable names
                            if (name == "STM32 STLink")
                            {
                                // now fill in the description
                                newNanoFrameworkDevice.Description = name + " @ " + EventHandlerForSerialDevice.Current.Device.PortName;


                                Debug.WriteLine("Add new nanoFramework device to list: " + newNanoFrameworkDevice.Description + " @ " + newNanoFrameworkDevice.Device.DeviceInformation.DeviceSelector);
                            }

                            // done here, close the device
                            EventHandlerForSerialDevice.Current.CloseDevice();

                            return;
                        }

                        // couldn't open device better remove it from the collection right away
                        NanoFrameworkDevices.Remove(newNanoFrameworkDevice as NanoDeviceBase);
                    }
                }
                else
                {
                    // this NanoFramework device is already on the list
                    // stop the dispose countdown!
                    nanoFrameworkDeviceMatch.StopCountdownForDispose();
                }
            }
        }

        private void RemoveDeviceFromList(string deviceId)
        {
            // Removes the device entry from the internal list; therefore the UI
            var deviceEntry = FindDevice(deviceId);

            Debug.WriteLine("Serial device removed: " + deviceId);

            SerialDevices.Remove(deviceEntry);

            // start thread to dispose and remove device from collection if it doesn't enumerate again in 2 seconds
            Task.Factory.StartNew(() =>
            {
                // get device
                var device = FindNanoFrameworkDevice(deviceId);

                if (device != null)
                {
                    // set device to dispose if it doesn't come back in 2 seconds
                    device.StartCountdownForDispose();

                    // hold here for the same time as the default timer of the dispose timer
                    new ManualResetEvent(false).WaitOne(TimeSpan.FromSeconds(2.5));

                    // check is object was disposed
                    if ((device as NanoDevice<NanoSerialDevice>).KillFlag)
                    {
                        // yes, remove it from collection
                        NanoFrameworkDevices.Remove(device);

                        Debug.WriteLine("Removing device " + device.Description);

                        device = null;
                    }
                }
            });
        }

        private void ClearDeviceEntries()
        {
            SerialDevices.Clear();
        }

        /// <summary>
        /// Searches through the existing list of devices for the first DeviceListEntry that has
        /// the specified device Id.
        /// </summary>
        /// <param name="deviceId">Id of the device that is being searched for</param>
        /// <returns>DeviceListEntry that has the provided Id; else a nullptr</returns>
        private SerialDeviceInformation FindDevice(String deviceId)
        {
            if (deviceId != null)
            {
                foreach (SerialDeviceInformation entry in SerialDevices)
                {
                    if (entry.DeviceInformation.Id == deviceId)
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        private NanoDeviceBase FindNanoFrameworkDevice(string deviceId)
        {
            if (deviceId != null)
            {
                // SerialMatch.Device.DeviceInformation
                return NanoFrameworkDevices.FirstOrDefault(d => ((d as NanoDevice<NanoSerialDevice>).Device.DeviceInformation as SerialDeviceInformation).DeviceInformation.Id == deviceId);
            }

            return null;
        }


        /// <summary>
        /// Remove the device from the device list 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformationUpdate"></param>
        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate deviceInformationUpdate)
        {
            RemoveDeviceFromList(deviceInformationUpdate.Id);
        }

        /// <summary>
        /// This function will add the device to the listOfDevices
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformation"></param>
        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation deviceInformation)
        {
            AddDeviceToList(deviceInformation, mapDeviceWatchersToDeviceSelector[sender]);
        }

        #endregion

        #region Handlers and events for Device Enumeration Complete 

        private void OnDeviceEnumerationComplete(DeviceWatcher sender, object args)
        {
            // add another device watcher completed
            deviceWatchersCompletedCount++;

            if (deviceWatchersCompletedCount == mapDeviceWatchersToDeviceSelector.Count)
            {
                // prepare a list of devices that are to be removed if they are deemed as not valid nanoFramework devices
                var devicesToRemove = new List<NanoDeviceBase>();

                foreach (NanoDeviceBase device in NanoFrameworkDevices)
                {
                    // connect to the device (as Task to get rid of the await)
                    var connectTask = ConnectDeviceAsync(device);

                    if (connectTask.Result)
                    {
                        if(!CheckValidNanoFrameworkSerialDevice())
                        {
                            // mark this device for removal
                            devicesToRemove.Add(device);
                        }
                        else
                        {
                            Debug.WriteLine($"New Serial device: {device.Description} {(((NanoDevice<NanoSerialDevice>)device).Device.DeviceInformation.DeviceInformation.Id)}");
                        }

                        // done here, disconnect from the device now
                        ((NanoDevice<NanoSerialDevice>)device).Disconnect();
                    }
                    else
                    {
                        // couldn't open device
                        // mark this device for removal
                        devicesToRemove.Add(device);
                    }
                }

                // remove device form nanoFramework device collection (force Linq to execute with call to Count())
                devicesToRemove.Select(d => NanoFrameworkDevices.Remove(d)).Count();

                // all watchers have completed enumeration
                isAllDevicesEnumerated = true;

                Debug.WriteLine($"Serial device enumeration completed. Found {NanoFrameworkDevices.Count} devices");

                // fire event that Serial enumeration is complete 
                OnDeviceEnumerationCompleted();
            }
        }

        private bool CheckValidNanoFrameworkSerialDevice()
        {
            // get name
            var name = EventHandlerForSerialDevice.Current.DeviceInformation?.Properties["System.ItemNameDisplay"] as string;

            // try get serial number
            var serialNumber = EventHandlerForSerialDevice.Current.DeviceInformation.GetSerialNumber();

            // acceptable names and that are know valid nanoFramework devices
            if (
                // STM32 COM port on on-board ST Link found in most NUCLEO boards
                (name == "STM32 STLink")
               )
            {
                // fill in description for this device
                FindNanoFrameworkDevice(EventHandlerForSerialDevice.Current.DeviceInformation.Id).Description = name + " @ " + EventHandlerForSerialDevice.Current.Device.PortName;

                // should be a valid nanoFramework device, done here
                return true;
            }
            else if(serialNumber != null)
            {
                if(serialNumber.Contains("NANO_"))
                {
                    FindNanoFrameworkDevice(EventHandlerForSerialDevice.Current.DeviceInformation.Id).Description = serialNumber + " @ " + EventHandlerForSerialDevice.Current.Device.PortName;
                }

                // should be a valid nanoFramework device, done here
                return true;
            }

            // default to false
            return false;
        }

        protected virtual void OnDeviceEnumerationCompleted()
        {
            DeviceEnumerationCompleted?.Invoke(this, new EventArgs());
        }

        /// <summary>
        /// Event that is raised when enumeration of all watched devices is complete.
        /// </summary>
        public override event EventHandler DeviceEnumerationCompleted;

        #endregion

        public async Task<bool> ConnectDeviceAsync(NanoDeviceBase device)
        {
            return await ConnectSerialDeviceAsync((device as NanoDevice<NanoSerialDevice>).Device.DeviceInformation as SerialDeviceInformation);
        }

        private async Task<bool> ConnectSerialDeviceAsync(SerialDeviceInformation serialDeviceInfo)
        {
            // try to determine if we already have this device opened.
            if (EventHandlerForSerialDevice.Current != null)
            {
                // device matches
                if (EventHandlerForSerialDevice.Current.DeviceInformation == serialDeviceInfo.DeviceInformation)
                {
                    return true;
                }
            }

            // Create an EventHandlerForDevice to watch for the device we are connecting to
            EventHandlerForSerialDevice.CreateNewEventHandlerForDevice();

            return await EventHandlerForSerialDevice.Current.OpenDeviceAsync(serialDeviceInfo.DeviceInformation, serialDeviceInfo.DeviceSelector).ConfigureAwait(false);
        }

        public void DisconnectDevice(NanoDeviceBase device)
        {
            if (FindDevice(((device as NanoDevice<NanoSerialDevice>).Device.DeviceInformation as SerialDeviceInformation).DeviceInformation.Id) != null)
            {
                EventHandlerForSerialDevice.Current.CloseDevice();
            }
        }

        #region Interface implementations

        public DateTime LastActivity { get; set; }

        public void DisconnectDevice(SerialDevice device)
        {
            EventHandlerForSerialDevice.Current.CloseDevice();
        }

        public async Task<uint> SendBufferAsync(byte[] buffer, TimeSpan waiTimeout, CancellationToken cancellationToken)
        {
            uint bytesWritten = 0;

            // device must be connected
            if (EventHandlerForSerialDevice.Current.IsDeviceConnected)
            {
                // get data  writer for current serial device
                var writer = new DataWriter(EventHandlerForSerialDevice.Current.Device.OutputStream);
                
                // write buffer to device
                writer.WriteBytes(buffer);

                // need to have a timeout to cancel the read task otherwise it may end up waiting forever for this to return
                var timeoutCancelatioToken = new CancellationTokenSource(waiTimeout).Token;

                // because we have an external cancellation token and the above timeout cancellation token, need to combine both
                var linkedCancelationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancelatioToken).Token;

                Task<uint> storeAsyncTask;

                try
                {

                    // Don't start any IO if the task has been canceled
                    lock (cancelIoLock)
                    {
                        // set this makes sure that an exception is thrown when the cancellation token is set
                        linkedCancelationToken.ThrowIfCancellationRequested();

                        // Now the buffer data is actually flushed out to the device.
                        // We should implement a cancellation Token here so we are able to stop the task operation explicitly if needed
                        // The completion function should still be called so that we can properly handle a canceled task
                        storeAsyncTask = writer.StoreAsync().AsTask(linkedCancelationToken);
                    }

                    bytesWritten = await storeAsyncTask;

                    if (bytesWritten > 0)
                    {
                        LastActivity = DateTime.Now;
                    }
                }
                catch (TaskCanceledException)
                {
                    // this is expected to happen, don't do anything with this 
                }
            }
            else
            {
                // FIXME 
                // NotifyDeviceNotConnected
            }

            return bytesWritten;
        }

        public async Task<DataReader> ReadBufferAsync(uint bytesToRead, TimeSpan waiTimeout, CancellationToken cancellationToken)
        {
            // device must be connected
            if (EventHandlerForSerialDevice.Current.IsDeviceConnected)
            {
                // get data  reader for current serial device
                DataReader reader = new DataReader(EventHandlerForSerialDevice.Current.Device.InputStream); ;
                uint bytesRead = 0;

                // need to have a timeout to cancel the read task otherwise it may end up waiting forever for this to return
                var timeoutCancelatioToken = new CancellationTokenSource(waiTimeout).Token;

                // because we have an external cancellation token and the above timeout cancellation token, need to combine both
                var linkedCancelationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancelatioToken).Token;

                Task<UInt32> loadAsyncTask;

                //Debug.WriteLine("### waiting");
                await semaphore.WaitAsync();
                //Debug.WriteLine("### got it");

                try
                {
                    // Don't start any IO if the task has been canceled
                    lock (cancelIoLock)
                    {
                        // set this makes sure that an exception is thrown when the cancellation token is set
                        linkedCancelationToken.ThrowIfCancellationRequested();

                        // We should implement a cancellation Token here so we are able to stop the task operation explicitly if needed
                        // The completion function should still be called so that we can properly handle a canceled task
                        loadAsyncTask = reader.LoadAsync(bytesToRead).AsTask(linkedCancelationToken);
                    }

                    bytesRead = await loadAsyncTask;
                }
                catch (TaskCanceledException)
                {
                    // this is expected to happen, don't do anything with this 
                }
                finally
                {
                    semaphore.Release();
                    //Debug.WriteLine("### released");
                }

                return reader;
            }
            else
            {
                // FIXME 
                // NotifyDeviceNotConnected
            }

            return null;
        }

        #endregion
    }
}
